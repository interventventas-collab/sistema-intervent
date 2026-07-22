using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Configuración del respondedor automático de preguntas MeLi:
/// mensajes que rotan, horario por día, colchón de minutos, firma y feriados.
/// </summary>
[ApiController]
[Route("api/meli/autoreply")]
[Authorize]
public class MeliAutoReplyController : ControllerBase
{
    private readonly AppDbContext _db;

    public MeliAutoReplyController(AppDbContext db) { _db = db; }

    private const string CfgEnabled = "meli.autoreply.enabled";
    private const string CfgDelayMinutes = "meli.autoreply.delayMinutes";
    private const string CfgSignature = "meli.autoreply.signature";
    private const string CfgHolidayDate = "meli.autoreply.holidayDate";

    private static DateTime NowArgentina() => DateTime.UtcNow.AddHours(-3);

    private async Task<string?> GetAsync(string key)
        => (await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == key))?.Value;

    private async Task SetAsync(string key, string value)
    {
        var s = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == key);
        if (s is null) { s = new AppSetting { Key = key }; _db.AppSettings.Add(s); }
        s.Value = value;
        s.UpdatedAt = DateTime.UtcNow;
    }

    // ===================== CONFIG (todo junto para la pantalla) =====================

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig()
    {
        var enabled = await GetAsync(CfgEnabled);
        var delay = await GetAsync(CfgDelayMinutes);
        var signature = await GetAsync(CfgSignature);
        var holiday = await GetAsync(CfgHolidayDate);
        var todayArt = NowArgentina().ToString("yyyy-MM-dd");

        var schedule = await _db.MeliAutoReplySchedule.OrderBy(s => s.DayOfWeek).ToListAsync();
        var messages = await _db.MeliAutoReplyMessages.OrderBy(m => m.Id).ToListAsync();

        return Ok(new
        {
            enabled = enabled == "1" || string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase),
            delayMinutes = int.TryParse(delay, out var d) ? d : 30,
            signature = signature ?? "",
            holidayToday = !string.IsNullOrWhiteSpace(holiday) && holiday == todayArt,
            nowArgentina = NowArgentina().ToString("yyyy-MM-dd HH:mm"),
            schedule = schedule.Select(s => new
            {
                dayOfWeek = s.DayOfWeek,
                isActive = s.IsActive,
                allDay = s.AllDay,
                startTime = s.StartTime,
                endTime = s.EndTime
            }),
            messages = messages.Select(m => new { id = m.Id, body = m.Body, isActive = m.IsActive })
        });
    }

    public record ConfigRequest(bool Enabled, int DelayMinutes, string Signature);

    [HttpPut("config")]
    public async Task<IActionResult> SaveConfig([FromBody] ConfigRequest req)
    {
        await SetAsync(CfgEnabled, req.Enabled ? "1" : "0");
        await SetAsync(CfgDelayMinutes, Math.Clamp(req.DelayMinutes, 0, 1440).ToString());
        await SetAsync(CfgSignature, (req.Signature ?? "").Trim());
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ===================== ON / OFF rápido =====================

    public record ToggleRequest(bool Enabled);

    [HttpPost("toggle")]
    public async Task<IActionResult> Toggle([FromBody] ToggleRequest req)
    {
        await SetAsync(CfgEnabled, req.Enabled ? "1" : "0");
        await _db.SaveChangesAsync();
        return Ok(new { enabled = req.Enabled });
    }

    // ===================== FERIADO: "hoy responder todo el día" =====================

    [HttpPost("holiday-today")]
    public async Task<IActionResult> HolidayToday([FromBody] ToggleRequest req)
    {
        // Si se activa, guardamos la fecha de hoy (ART). Se "vence" solo mañana.
        await SetAsync(CfgHolidayDate, req.Enabled ? NowArgentina().ToString("yyyy-MM-dd") : "");
        await _db.SaveChangesAsync();
        return Ok(new { holidayToday = req.Enabled });
    }

    // ===================== MENSAJES (CRUD) =====================

    public record MessageRequest(string Body, bool IsActive);

    [HttpPost("messages")]
    public async Task<IActionResult> CreateMessage([FromBody] MessageRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Body))
            return BadRequest(new { error = "El mensaje no puede estar vacío" });
        var m = new MeliAutoReplyMessage { Body = req.Body.Trim(), IsActive = req.IsActive };
        _db.MeliAutoReplyMessages.Add(m);
        await _db.SaveChangesAsync();
        return Ok(new { id = m.Id, body = m.Body, isActive = m.IsActive });
    }

    [HttpPut("messages/{id:int}")]
    public async Task<IActionResult> UpdateMessage(int id, [FromBody] MessageRequest req)
    {
        var m = await _db.MeliAutoReplyMessages.FindAsync(id);
        if (m is null) return NotFound(new { error = "Mensaje no encontrado" });
        if (string.IsNullOrWhiteSpace(req.Body))
            return BadRequest(new { error = "El mensaje no puede estar vacío" });
        m.Body = req.Body.Trim();
        m.IsActive = req.IsActive;
        m.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { id = m.Id, body = m.Body, isActive = m.IsActive });
    }

    [HttpDelete("messages/{id:int}")]
    public async Task<IActionResult> DeleteMessage(int id)
    {
        var m = await _db.MeliAutoReplyMessages.FindAsync(id);
        if (m is null) return NotFound(new { error = "Mensaje no encontrado" });
        _db.MeliAutoReplyMessages.Remove(m);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ===================== HORARIOS (grilla por día) =====================

    public record ScheduleRow(int DayOfWeek, bool IsActive, bool AllDay, string StartTime, string EndTime);

    [HttpPut("schedule")]
    public async Task<IActionResult> SaveSchedule([FromBody] List<ScheduleRow> rows)
    {
        foreach (var r in rows)
        {
            if (r.DayOfWeek < 0 || r.DayOfWeek > 6) continue;
            var row = await _db.MeliAutoReplySchedule.FirstOrDefaultAsync(s => s.DayOfWeek == r.DayOfWeek);
            if (row is null)
            {
                row = new MeliAutoReplySchedule { DayOfWeek = r.DayOfWeek };
                _db.MeliAutoReplySchedule.Add(row);
            }
            row.IsActive = r.IsActive;
            row.AllDay = r.AllDay;
            row.StartTime = NormalizeHhmm(r.StartTime, "21:00");
            row.EndTime = NormalizeHhmm(r.EndTime, "06:00");
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    private static string NormalizeHhmm(string? v, string fallback)
        => TimeSpan.TryParse(v, out var ts) ? ts.ToString(@"hh\:mm") : fallback;

    // ===================== PROBAR (arma un mensaje al azar, NO envía nada) =====================

    [HttpGet("preview")]
    public async Task<IActionResult> Preview()
    {
        var bodies = await _db.MeliAutoReplyMessages.Where(m => m.IsActive).Select(m => m.Body).ToListAsync();
        if (bodies.Count == 0)
            return Ok(new { text = "(No hay mensajes activos para mostrar)" });
        var signature = (await GetAsync(CfgSignature) ?? "").Trim();
        var body = bodies[Random.Shared.Next(bodies.Count)].Trim();
        var text = string.IsNullOrEmpty(signature) ? body : $"{body} {signature}";
        return Ok(new { text });
    }

    // ===================== ÚLTIMAS RESPONDIDAS POR EL ROBOT (para ver qué contestó) =====================

    [HttpGet("recent")]
    public async Task<IActionResult> Recent([FromQuery] int limit = 30)
    {
        var list = await _db.MeliQuestions
            .Where(q => q.AutoAnswered)
            .OrderByDescending(q => q.DateAnswered)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(q => new
            {
                id = q.Id,
                itemTitle = q.ItemTitle,
                fromNickname = q.FromNickname,
                text = q.Text,
                answerText = q.AnswerText,
                dateAnswered = q.DateAnswered
            })
            .ToListAsync();
        return Ok(list);
    }
}
