using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Recibos de VISITA / cambio de producto (2026-08-05). Alta con descripcion libre + firma,
/// recibo con QR, y seguimiento (pendiente -> realizada). Ver Api/Models/Visita.cs.
/// </summary>
[ApiController]
[Route("api/visitas")]
[Authorize]
public class VisitasController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly QrRepartidorService _qr;

    public VisitasController(AppDbContext db, QrRepartidorService qr)
    {
        _db = db;
        _qr = qr;
    }

    private static VisitaDto Map(Visita v) => new(
        v.Id, v.ClienteId, v.ClienteNombre, v.Direccion, v.Localidad, v.Telefono,
        v.Descripcion, v.Estado, !string.IsNullOrEmpty(v.FirmaBase64), v.NombreFirmante,
        v.PublicToken, v.ComentarioResolucion, v.RealizadaAt, v.MapeoLat, v.MapeoLng,
        v.CreadoPor, v.CreatedAt, v.UpdatedAt);

    /// <summary>Genera un token aleatorio ~22 chars base64-url-safe para el link publico /visita/{token}.</summary>
    private static string GeneratePublicToken()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        return Convert.ToBase64String(bytes)
            .Replace("/", "_").Replace("+", "-").TrimEnd('=');
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? estado = null)
    {
        var q = _db.Visitas.AsQueryable();
        if (!string.IsNullOrWhiteSpace(estado))
        {
            var e = estado.Trim().ToLowerInvariant();
            q = q.Where(v => v.Estado == e);
        }
        var list = await q.OrderByDescending(v => v.CreatedAt).Take(200).ToListAsync();
        return Ok(list.Select(Map).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var v = await _db.Visitas.FindAsync(id);
        if (v is null) return NotFound(new { error = "Visita no encontrada" });
        return Ok(Map(v));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateVisitaRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ClienteNombre))
            return BadRequest(new { error = "Falta el nombre del cliente" });
        if (string.IsNullOrWhiteSpace(req.Descripcion))
            return BadRequest(new { error = "La descripcion es obligatoria" });

        var v = new Visita
        {
            ClienteId = req.ClienteId,
            ClienteNombre = req.ClienteNombre.Trim(),
            Direccion = string.IsNullOrWhiteSpace(req.Direccion) ? null : req.Direccion.Trim(),
            Localidad = string.IsNullOrWhiteSpace(req.Localidad) ? null : req.Localidad.Trim(),
            Telefono = string.IsNullOrWhiteSpace(req.Telefono) ? null : req.Telefono.Trim(),
            Descripcion = req.Descripcion.Trim(),
            Estado = "pendiente",
            FirmaBase64 = string.IsNullOrWhiteSpace(req.FirmaBase64) ? null : req.FirmaBase64,
            NombreFirmante = string.IsNullOrWhiteSpace(req.NombreFirmante) ? null : req.NombreFirmante.Trim(),
            MapeoLat = req.MapeoLat,
            MapeoLng = req.MapeoLng,
            CreadoPor = string.IsNullOrWhiteSpace(req.CreadoPor) ? null : req.CreadoPor.Trim(),
            PublicToken = GeneratePublicToken(),
            CreatedAt = DateTime.UtcNow
        };
        _db.Visitas.Add(v);
        await _db.SaveChangesAsync();
        return Ok(Map(v));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateVisitaRequest req)
    {
        var v = await _db.Visitas.FindAsync(id);
        if (v is null) return NotFound(new { error = "Visita no encontrada" });
        if (req.Descripcion is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Descripcion))
                return BadRequest(new { error = "La descripcion no puede quedar vacia" });
            v.Descripcion = req.Descripcion.Trim();
        }
        if (req.FirmaBase64 is not null)
            v.FirmaBase64 = string.IsNullOrWhiteSpace(req.FirmaBase64) ? null : req.FirmaBase64;
        if (req.NombreFirmante is not null)
            v.NombreFirmante = string.IsNullOrWhiteSpace(req.NombreFirmante) ? null : req.NombreFirmante.Trim();
        v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(v));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var v = await _db.Visitas.FindAsync(id);
        if (v is null) return NotFound(new { error = "Visita no encontrada" });
        _db.Visitas.Remove(v);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    /// <summary>PNG del QR que lleva al recibo publico /visita/{token}. Lo muestra la pantalla al operador.</summary>
    [HttpGet("{id:int}/qr")]
    public async Task<IActionResult> GetQr(int id)
    {
        var v = await _db.Visitas.FindAsync(id);
        if (v is null) return NotFound();
        var png = await _qr.GenerarQrVisitaAsync(v.PublicToken);
        if (png is null) return NotFound(new { error = "No hay URL publica configurada (mapeo.public_base_url)" });
        return File(png, "image/png");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ENDPOINTS PUBLICOS (sin login) — el recibo por QR y marcar realizada
    // ═══════════════════════════════════════════════════════════════════════

    [HttpGet("publica/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetByPublicToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return NotFound();
        var v = await _db.Visitas.FirstOrDefaultAsync(x => x.PublicToken == token);
        if (v is null) return NotFound(new { error = "Visita no encontrada" });
        return Ok(new VisitaPublicaDto(
            v.Id, v.ClienteNombre, v.Direccion, v.Localidad, v.Telefono, v.Descripcion,
            v.Estado, v.FirmaBase64, v.NombreFirmante, v.ComentarioResolucion, v.RealizadaAt, v.CreatedAt));
    }

    /// <summary>Etapa 2: desde el escaneo del QR se marca la visita como realizada + comentario.</summary>
    [HttpPost("publica/{token}/realizada")]
    [AllowAnonymous]
    public async Task<IActionResult> MarcarRealizada(string token, [FromBody] MarcarVisitaRealizadaRequest req)
    {
        if (string.IsNullOrWhiteSpace(token)) return NotFound();
        var v = await _db.Visitas.FirstOrDefaultAsync(x => x.PublicToken == token);
        if (v is null) return NotFound(new { error = "Visita no encontrada" });
        v.Estado = "realizada";
        v.RealizadaAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(req?.Comentario))
            v.ComentarioResolucion = req.Comentario.Trim();
        v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, estado = v.Estado });
    }
}
