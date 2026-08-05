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
    private readonly VisitaReciboPdfService _reciboPdf;

    public VisitasController(AppDbContext db, QrRepartidorService qr, WhatsAppOutboundService outbound,
        VisitaMapeoService mapeo, VisitaReciboPdfService reciboPdf)
    {
        _db = db;
        _qr = qr;
        _outbound = outbound;
        _mapeo = mapeo;
        _reciboPdf = reciboPdf;
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

    // ═══════════════════════════════════════════════════════════════════════
    //  PREPARACION DE PEDIDOS (tablero de Osmar) — sumar visitas para armar
    // ═══════════════════════════════════════════════════════════════════════

    private static VisitaPreparacionDto MapPrep(Visita v) => new(
        v.Id, v.Numero, v.ClienteNombre, v.Direccion, v.Localidad, v.Telefono,
        v.Descripcion, v.PublicToken, !string.IsNullOrEmpty(v.FirmaBase64), v.PreparadoAt, v.CreatedAt);

    /// <summary>Manda la visita al tablero de Preparación de pedidos (para armar).</summary>
    [HttpPost("{id:int}/mandar-preparacion")]
    public async Task<IActionResult> MandarPreparacion(int id)
    {
        var v = await _db.Visitas.FindAsync(id);
        if (v is null) return NotFound(new { error = "Visita no encontrada" });
        v.EnPreparacion = true;
        v.PreparadoAt = null;
        v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Visitas mandadas a preparación que todavía NO se armaron (para el tablero).</summary>
    [HttpGet("preparacion")]
    public async Task<IActionResult> ListarPreparacion()
    {
        var list = await _db.Visitas
            .Where(v => v.EnPreparacion && v.PreparadoAt == null)
            .OrderByDescending(v => v.CreatedAt)
            .Take(200).ToListAsync();
        return Ok(list.Select(MapPrep).ToList());
    }

    /// <summary>Visitas ya armadas (para la sección "Ya armados", últimos N días).</summary>
    [HttpGet("preparacion/armados")]
    public async Task<IActionResult> ListarPreparacionArmados([FromQuery] int dias = 7)
    {
        var desde = DateTime.UtcNow.Date.AddDays(-dias);
        var list = await _db.Visitas
            .Where(v => v.EnPreparacion && v.PreparadoAt != null && v.PreparadoAt >= desde)
            .OrderByDescending(v => v.PreparadoAt)
            .Take(200).ToListAsync();
        return Ok(list.Select(MapPrep).ToList());
    }

    /// <summary>Marca la visita como ARMADA (la saca de "para armar" y pasa a "ya armados").</summary>
    [HttpPost("{id:int}/preparacion/armada")]
    public async Task<IActionResult> MarcarArmada(int id)
    {
        var v = await _db.Visitas.FindAsync(id);
        if (v is null) return NotFound(new { error = "Visita no encontrada" });
        v.PreparadoAt = DateTime.UtcNow;
        v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Vuelve a poner la visita "para armar" (deshace el armada).</summary>
    [HttpPost("{id:int}/preparacion/volver")]
    public async Task<IActionResult> VolverAArmar(int id)
    {
        var v = await _db.Visitas.FindAsync(id);
        if (v is null) return NotFound(new { error = "Visita no encontrada" });
        v.PreparadoAt = null;
        v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Saca la visita del tablero de preparación por completo.</summary>
    [HttpPost("{id:int}/preparacion/quitar")]
    public async Task<IActionResult> QuitarDePreparacion(int id)
    {
        var v = await _db.Visitas.FindAsync(id);
        if (v is null) return NotFound(new { error = "Visita no encontrada" });
        v.EnPreparacion = false;
        v.PreparadoAt = null;
        v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
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

        // Le mandamos el PDF del recibo COMO ARCHIVO adjunto (documento), no un link. Meta baja el
        // PDF de nuestro endpoint público, así que necesita la URL pública configurada.
        var baseUrl = (await _db.AppSettings.FindAsync("mapeo.public_base_url"))?.Value;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(v.PublicToken))
            return StatusCode(503, new { error = "No está configurada la dirección pública del sistema (mapeo.public_base_url), no puedo enviar el PDF." });
        var num = v.Numero.ToString("0000");
        var mediaUrl = $"{baseUrl.TrimEnd('/')}/api/visitas/publica/{v.PublicToken}/recibo.pdf";
        var filename = $"Visita-{num}.pdf";
        var caption = $"Hola! Te paso el recibo de tu visita N° {num}. Cualquier cosa avisame. Gracias!";

        try
        {
            var (msgId, canal, lin) = await _outbound.SendMediaAsync(destino, mediaUrl, caption, filename, req?.LineaPhoneId);
            if (msgId is null)
                return StatusCode(503, new { error = "WhatsApp no lo aceptó. Suele pasar cuando el cliente NO te escribió en las últimas 24hs: Meta solo deja mandar dentro de esa ventana. Esperá a que el cliente escriba." });

            // Registrar el saliente (con el PDF) para que aparezca en el chat del cliente y se le
            // pueda seguir el estado de entrega. Numero en formato canónico del inbox.
            _db.WhatsAppTwilioMensajes.Add(new WhatsAppTwilioMensaje
            {
                Direccion = "OUTGOING",
                Numero = destino,
                Cuerpo = caption,
                MediaUrl = mediaUrl,
                MediaFilename = filename,
                TwilioMessageSid = msgId,
                Canal = canal,
                LineaPhoneId = lin,
                Procesado = true,
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

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

    /// <summary>PDF del recibo de visita, PUBLICO (sin login) — mismo formato que el recibo de entrega
    /// de ventas, con logo/branding, la descripcion, el QR y el cuadro de firma. Es lo que se imprime.</summary>
    [HttpGet("publica/{token}/recibo.pdf")]
    [AllowAnonymous]
    public async Task<IActionResult> GetReciboPdf(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return NotFound();
        var v = await _db.Visitas.FirstOrDefaultAsync(x => x.PublicToken == token);
        if (v is null) return NotFound(new { error = "Visita no encontrada" });
        var cfg = await _db.CafeSettings.FindAsync(1);
        var qr = await _qr.GenerarQrVisitaAsync(v.PublicToken);
        var bytes = _reciboPdf.GenerarPdfBytes(v, cfg, qr);
        return File(bytes, "application/pdf", $"Visita-{v.Numero:0000}.pdf");
    }

    /// <summary>PNG del QR del recibo, PUBLICO (sin login) para que la pagina del recibo /visita/{token}
    /// lo muestre e imprima. Apunta a la misma pagina publica (para escanear el papel y hacer seguimiento).</summary>
    [HttpGet("publica/{token}/qr")]
    [AllowAnonymous]
    public async Task<IActionResult> GetQrPublico(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return NotFound();
        var v = await _db.Visitas.FirstOrDefaultAsync(x => x.PublicToken == token);
        if (v is null) return NotFound();
        var png = await _qr.GenerarQrVisitaAsync(v.PublicToken);
        if (png is null) return NotFound(new { error = "No hay URL publica configurada (mapeo.public_base_url)" });
        return File(png, "image/png");
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
