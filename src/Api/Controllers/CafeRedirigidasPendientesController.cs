using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-09-26: redirigidas cargadas por WhatsApp ("redi" a la línea FRIKAF, ver
/// WhatsAppRedirigidaBotService). Aparecen en la bolsita 💰 / Cobranzas a aprobar junto a los cobros
/// de repartidores. Volcar = la cobranza de siempre, precargada; al guardarla se llama a
/// "vincular", que la marca APROBADA y copia las fotos/comprobantes a los adjuntos de la cobranza.
/// </summary>
[ApiController]
[Route("api/cafe/redirigidas-pendientes")]
[Authorize]
public class CafeRedirigidasPendientesController : ControllerBase
{
    private const string UploadsDir = "/data/whatsapp-uploads";
    private readonly AppDbContext _db;
    private readonly FileStorageService _files;
    private readonly AuditLogService _audit;

    public CafeRedirigidasPendientesController(AppDbContext db, FileStorageService files, AuditLogService audit)
    {
        _db = db; _files = files; _audit = audit;
    }

    public record AdjuntoDto(int Id, string NombreOriginal, string? MimeType);
    public record RediPendienteDto(
        int Id, string Estado, DateTime CreatedAt, decimal Importe, string? EnviadoPor,
        int? ClienteId, string? ClienteNombre, string? ClienteTexto,
        string? RecibeTipo, int? EmpleadoId, string? EmpleadoNombre, int? ProveedorId, string? ProveedorNombre,
        string? RecibeTexto, string? Destino,
        int? CobranzaCreadaId, string? CobranzaNumero, string? RechazadaMotivo, string? RevisadaPor, DateTime? RevisadaAt,
        List<AdjuntoDto> Adjuntos);
    public record VincularRequest(int CobranzaId, string? Operador);
    public record RechazarRequest(string? Motivo, string? Operador);

    [HttpGet("count-pendientes")]
    public async Task<IActionResult> CountPendientes()
        => Ok(new { count = await _db.CafeRedirigidasPendientes.CountAsync(x => x.Estado == "PENDIENTE") });

    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] string estado = "PENDIENTE")
    {
        var q = _db.CafeRedirigidasPendientes.AsNoTracking().Where(x => x.Estado == estado);
        // Aprobadas y rechazadas: las últimas, para no traer la historia entera.
        if (estado != "PENDIENTE") q = q.OrderByDescending(x => x.RevisadaAt).Take(200);
        var lista = await q.Include(x => x.Adjuntos).ToListAsync();
        return Ok(await MapAsync(lista));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var p = await _db.CafeRedirigidasPendientes.AsNoTracking().Include(x => x.Adjuntos).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null || p.Estado == "BORRADOR") return NotFound();
        return Ok((await MapAsync(new() { p }))[0]);
    }

    /// <summary>Muestra la foto / el PDF que mandaron por WhatsApp.</summary>
    [HttpGet("adjuntos/{adjId:int}/archivo")]
    public async Task<IActionResult> Archivo(int adjId)
    {
        var a = await _db.CafeRedirigidasPendientesAdjuntos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == adjId);
        if (a is null) return NotFound();
        var path = Path.Combine(UploadsDir, Path.GetFileName(a.StoredFilename));
        if (!System.IO.File.Exists(path)) return NotFound(new { error = "El archivo ya no está" });
        return PhysicalFile(path, string.IsNullOrEmpty(a.MimeType) ? "application/octet-stream" : a.MimeType);
    }

    /// <summary>Al guardar la cobranza precargada: la redirigida queda APROBADA y sus comprobantes pasan
    /// a ser adjuntos de la cobranza (se ven en el detalle, como los que se suben a mano).</summary>
    [HttpPost("{id:int}/vincular")]
    public async Task<IActionResult> Vincular(int id, [FromBody] VincularRequest req)
    {
        var p = await _db.CafeRedirigidasPendientes.Include(x => x.Adjuntos).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = $"Ya está {p.Estado}" });
        if (!await _db.CafeCobranzas.AnyAsync(c => c.Id == req.CobranzaId)) return BadRequest(new { error = "No existe esa cobranza" });

        p.Estado = "APROBADA";
        p.CobranzaCreadaId = req.CobranzaId;
        p.RevisadaPor = req.Operador;
        p.RevisadaAt = DateTime.UtcNow;
        p.UpdatedAt = DateTime.UtcNow;

        var relativeDir = $"cobranzas/{req.CobranzaId}";
        var absDir = _files.ResolveSafe(relativeDir);
        Directory.CreateDirectory(absDir);
        foreach (var a in p.Adjuntos)
        {
            var origen = Path.Combine(UploadsDir, Path.GetFileName(a.StoredFilename));
            if (!System.IO.File.Exists(origen)) continue;
            var ext = Path.GetExtension(a.StoredFilename);
            var fileName = $"{Guid.NewGuid():N}{ext}";
            System.IO.File.Copy(origen, Path.Combine(absDir, fileName));
            _db.CafeCobranzaAdjuntos.Add(new CafeCobranzaAdjunto
            {
                CobranzaId = req.CobranzaId, Tipo = "TRANSFERENCIA",
                FilePath = $"{relativeDir}/{fileName}", NombreOriginal = a.NombreOriginal,
                MimeType = a.MimeType, Tamano = a.Tamano, CreatedAt = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeRedirigidaPendiente", id.ToString(), "VINCULAR",
            $"Redirigida por WhatsApp de {p.EnviadoPor} volcada en la cobranza {req.CobranzaId} ({p.Adjuntos.Count} adjuntos)");
        return Ok(new { ok = true });
    }

    [HttpPost("{id:int}/rechazar")]
    public async Task<IActionResult> Rechazar(int id, [FromBody] RechazarRequest req)
    {
        var p = await _db.CafeRedirigidasPendientes.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = $"Ya está {p.Estado}" });
        p.Estado = "RECHAZADA";
        p.RechazadaMotivo = string.IsNullOrWhiteSpace(req.Motivo) ? null : req.Motivo.Trim()[..Math.Min(200, req.Motivo.Trim().Length)];
        p.RevisadaPor = req.Operador;
        p.RevisadaAt = DateTime.UtcNow;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeRedirigidaPendiente", id.ToString(), "RECHAZAR", p.RechazadaMotivo ?? "");
        return Ok(new { ok = true });
    }

    [HttpPost("{id:int}/restaurar")]
    public async Task<IActionResult> Restaurar(int id)
    {
        var p = await _db.CafeRedirigidasPendientes.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado != "RECHAZADA") return BadRequest(new { error = "Solo se restaura una rechazada" });
        p.Estado = "PENDIENTE"; p.RechazadaMotivo = null; p.RevisadaAt = null; p.RevisadaPor = null;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    private async Task<List<RediPendienteDto>> MapAsync(List<CafeRedirigidaPendiente> lista)
    {
        var cliIds = lista.Where(x => x.ClienteId.HasValue).Select(x => x.ClienteId!.Value).Distinct().ToList();
        var empIds = lista.Where(x => x.EmpleadoId.HasValue).Select(x => x.EmpleadoId!.Value).Distinct().ToList();
        var provIds = lista.Where(x => x.ProveedorId.HasValue).Select(x => x.ProveedorId!.Value).Distinct().ToList();
        var cobIds = lista.Where(x => x.CobranzaCreadaId.HasValue).Select(x => x.CobranzaCreadaId!.Value).Distinct().ToList();
        var clis = await _db.CafeClientes.AsNoTracking().Where(c => cliIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Nombre);
        var emps = await _db.NomEmpleados.AsNoTracking().Where(e => empIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Nombre);
        var provs = await _db.CafeProveedores.AsNoTracking().Where(v => provIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Nombre);
        var cobs = await _db.CafeCobranzas.AsNoTracking().Where(c => cobIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Numero);
        return lista.OrderByDescending(x => x.CreatedAt).Select(x => new RediPendienteDto(
            x.Id, x.Estado, x.CreatedAt, x.Importe, x.EnviadoPor,
            x.ClienteId, x.ClienteId is int c && clis.TryGetValue(c, out var cn) ? cn : null, x.ClienteTexto,
            x.RecibeTipo, x.EmpleadoId, x.EmpleadoId is int e && emps.TryGetValue(e, out var en) ? en : null,
            x.ProveedorId, x.ProveedorId is int v && provs.TryGetValue(v, out var vn) ? vn : null,
            x.RecibeTexto, x.Destino,
            x.CobranzaCreadaId, x.CobranzaCreadaId is int k && cobs.TryGetValue(k, out var kn) ? kn : null,
            x.RechazadaMotivo, x.RevisadaPor, x.RevisadaAt,
            x.Adjuntos.Select(a => new AdjuntoDto(a.Id, a.NombreOriginal, a.MimeType)).ToList())).ToList();
    }
}
