using System.Security.Claims;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/backups")]
[Authorize(Roles = "admin")]
public class BackupsController : ControllerBase
{
    private readonly BackupService _service;
    private readonly ScheduledProcessService _scheduled;

    public BackupsController(BackupService service, ScheduledProcessService scheduled)
    {
        _service = service;
        _scheduled = scheduled;
    }

    public record BackupFileDto(int Id, string FileName, long SizeBytes, string BackupType, string Status, string? ErrorMessage, DateTime CreatedAt);

    public record BackupSettingsDto(bool Enabled, int IntervalMinutes, int RetentionDays, DateTime? LastRunAt, string? LastRunStatus, DateTime? NextRunAt);

    public record UpdateBackupSettingsRequest(bool Enabled, int IntervalMinutes, int RetentionDays);

    public record RestoreRequest(string FileName, string Confirmation);

    private int? CurrentUserId
    {
        get
        {
            var id = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(id, out var v) ? v : null;
        }
    }

    // ---- Listar ----

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var items = await _service.ListAsync();
        var dtos = items.Select(b => new BackupFileDto(
            b.Id, b.FileName, b.SizeBytes, b.BackupType, b.Status, b.ErrorMessage, b.CreatedAt)).ToList();
        return Ok(dtos);
    }

    // ---- Configuracion ----

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        var sp = await _scheduled.GetByCodeAsync("BackupDatabase");
        var retention = await _service.GetRetentionDaysAsync();

        if (sp is null)
            return Ok(new BackupSettingsDto(false, 1440, retention, null, null, null));

        return Ok(new BackupSettingsDto(
            sp.IsEnabled,
            sp.IntervalMinutes ?? 1440,
            retention,
            sp.LastRunAt,
            sp.LastRunStatus,
            sp.NextRunAt));
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateBackupSettingsRequest request)
    {
        if (request.IntervalMinutes < 15)
            return BadRequest(new { error = "El intervalo minimo es 15 minutos" });
        if (request.RetentionDays < 1 || request.RetentionDays > 365)
            return BadRequest(new { error = "La retencion debe estar entre 1 y 365 dias" });

        await _scheduled.UpdateScheduleAsync("BackupDatabase", new Api.DTOs.UpdateScheduleRequest(
            TriggerType: "Interval",
            IntervalMinutes: request.IntervalMinutes,
            DailyAtTime: null,
            IsEnabled: request.Enabled));

        await _service.SetRetentionDaysAsync(request.RetentionDays);
        return await GetSettings();
    }

    // ---- Crear manual ----

    [HttpPost]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        try
        {
            var record = await _service.CreateBackupAsync("Manual", CurrentUserId, ct);
            return Ok(new BackupFileDto(record.Id, record.FileName, record.SizeBytes, record.BackupType, record.Status, record.ErrorMessage, record.CreatedAt));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ---- Subir .bak ----

    [HttpPost("upload")]
    [RequestSizeLimit(2L * 1024 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Archivo vacio" });

        try
        {
            using var stream = file.OpenReadStream();
            var record = await _service.UploadBackupAsync(stream, file.FileName, CurrentUserId, ct);
            return Ok(new BackupFileDto(record.Id, record.FileName, record.SizeBytes, record.BackupType, record.Status, record.ErrorMessage, record.CreatedAt));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ---- Descargar ----

    [HttpGet("{id:int}/download")]
    public async Task<IActionResult> Download(int id)
    {
        var all = await _service.ListAsync();
        var record = all.FirstOrDefault(b => b.Id == id);
        if (record is null) return NotFound(new { error = "Backup no encontrado" });

        var download = _service.OpenDownload(record.FileName);
        if (download is null) return NotFound(new { error = "El archivo no existe en disco" });

        return File(download.Value.stream, "application/octet-stream", download.Value.fileName);
    }

    // ---- Restaurar ----

    [HttpPost("restore")]
    public async Task<IActionResult> Restore([FromBody] RestoreRequest request, CancellationToken ct)
    {
        if (request.Confirmation != request.FileName)
            return BadRequest(new { error = "Para confirmar la restauracion, escribi el nombre exacto del archivo" });

        try
        {
            await _service.RestoreAsync(request.FileName, ct);
            return Ok(new { ok = true, message = "Restauracion completada correctamente" });
        }
        catch (FileNotFoundException)
        {
            return NotFound(new { error = "Archivo de backup no encontrado" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ---- Eliminar ----

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var ok = await _service.DeleteAsync(id);
        if (!ok) return NotFound(new { error = "Backup no encontrado" });
        return Ok(new { ok = true });
    }
}
