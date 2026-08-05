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
    private readonly WhatsAppOutboundService _outbound;
    private readonly VisitaMapeoService _mapeo;

    public VisitasController(AppDbContext db, QrRepartidorService qr, WhatsAppOutboundService outbound, VisitaMapeoService mapeo)
    {
        _db = db;
        _qr = qr;
        _outbound = outbound;
        _mapeo = mapeo;
    }

    private static VisitaDto Map(Visita v) => new(
        v.Id, v.Numero, v.ClienteId, v.ClienteNombre, v.Direccion, v.Localidad, v.Telefono,
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

        // Numero correlativo: el mayor actual + 1 (arranca en 1). Volumen bajo, sin concurrencia real.
        var ultimoNumero = await _db.Visitas.MaxAsync(x => (int?)x.Numero) ?? 0;

        var v = new Visita
        {
            Numero = ultimoNumero + 1,
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
        // Sacar también la parada del mapa (si estaba), para no dejar huérfanos.
        var refId = id.ToString();
        var stop = await _db.MapeoStops.FirstOrDefaultAsync(s => s.Origin == "visita" && s.OriginRefId == refId);
        if (stop is not null) _db.MapeoStops.Remove(stop);
        _db.Visitas.Remove(v);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    /// <summary>Suma la visita al mapa de reparto como una parada (Origin='visita'). Etapa 3.</summary>
    [HttpPost("{id:int}/sumar-al-mapa")]
    public async Task<IActionResult> SumarAlMapa(int id)
    {
        var v = await _db.Visitas.FindAsync(id);
        if (v is null) return NotFound(new { error = "Visita no encontrada" });
        var r = await _mapeo.SumarVisitaAsync(v);
        return Ok(new { ok = r.Ok, yaEstaba = r.YaEstaba, mensaje = r.Mensaje, sinUbicacion = r.SinUbicacion, stopId = r.StopId });
    }

    /// <summary>Envia el link del recibo de la visita al cliente por WhatsApp (API oficial Meta),
    /// desde la linea elegida. Reemplaza el viejo link wa.me. El numero se normaliza al formato
    /// canonico para que caiga en el hilo existente del cliente.</summary>
    [HttpPost("{id:int}/enviar-whatsapp")]
    public async Task<IActionResult> EnviarWhatsApp(int id, [FromBody] EnviarVisitaWhatsAppRequest req)
    {
        var v = await _db.Visitas.FindAsync(id);
        if (v is null) return NotFound(new { error = "Visita no encontrada" });

        var crudo = !string.IsNullOrWhiteSpace(req?.Numero) ? req!.Numero : v.Telefono;
        if (string.IsNullOrWhiteSpace(crudo))
            return BadRequest(new { error = "No hay teléfono para enviar. Cargá el número del cliente." });
        var destino = MetaWhatsAppService.ToInboxWhatsApp(crudo);

        var baseUrl = (await _db.AppSettings.FindAsync("mapeo.public_base_url"))?.Value;
        string? url = (!string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(v.PublicToken))
            ? $"{baseUrl.TrimEnd('/')}/visita/{v.PublicToken}"
            : null;
        var num = v.Numero.ToString("D4");
        var mensaje = url is not null
            ? $"Hola! Te paso el recibo de tu visita N° {num}:\n\n{url}\n\nCualquier cosa avisame. Gracias!"
            : $"Hola! Te paso el recibo de tu visita N° {num}. Cualquier cosa avisame. Gracias!";

        try
        {
            var (msgId, canal, _) = await _outbound.SendTextAsync(destino, mensaje, req?.LineaPhoneId);
            if (msgId is null)
                return StatusCode(503, new { error = "No se pudo enviar (WhatsApp no configurado o fuera de la ventana de 24hs)." });
            return Ok(new { ok = true, canal });
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
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
            v.Id, v.Numero, v.ClienteNombre, v.Direccion, v.Localidad, v.Telefono, v.Descripcion,
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
