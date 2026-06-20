using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Webhook receptor + envio Twilio WhatsApp + chat para el dashboard.
/// </summary>
[ApiController]
[Route("api/whatsapp/twilio")]
public class WhatsAppTwilioController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<WhatsAppTwilioController> _logger;
    private readonly TwilioWhatsAppService _twilio;

    public WhatsAppTwilioController(AppDbContext db, ILogger<WhatsAppTwilioController> logger, TwilioWhatsAppService twilio)
    {
        _db = db;
        _logger = logger;
        _twilio = twilio;
    }

    /// <summary>POST /api/whatsapp/twilio/webhook — Twilio postea aca cada mensaje entrante.</summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Webhook([FromForm] IFormCollection form)
    {
        var from = form["From"].ToString();
        var body = form["Body"].ToString();
        var profileName = form["ProfileName"].ToString();
        var messageSid = form["MessageSid"].ToString();
        int.TryParse(form["NumMedia"].ToString(), out var numMedia);
        var mediaUrl = numMedia > 0 ? form["MediaUrl0"].ToString() : null;

        _logger.LogInformation("WhatsApp Twilio IN: {From} ({Name}) → {Body}", from, profileName, body);

        var msg = new WhatsAppTwilioMensaje
        {
            Direccion = "INCOMING",
            Numero = from,
            NombrePerfil = string.IsNullOrEmpty(profileName) ? null : profileName,
            Cuerpo = body,
            MediaUrl = mediaUrl,
            NumMedia = numMedia,
            TwilioMessageSid = messageSid,
            Procesado = true, // Fase 2: marcamos como visto. Conversion a venta es manual desde el chat.
            CreatedAt = DateTime.UtcNow
        };
        _db.WhatsAppTwilioMensajes.Add(msg);
        await _db.SaveChangesAsync();

        // No respondemos automaticamente en Fase 2 — el operador responde manual desde el chat.
        // (Si despues queremos auto-respuesta de "te leimos", se agrega aca)
        return Content("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response></Response>", "text/xml");
    }

    public record SendRequest(string Numero, string Mensaje);

    /// <summary>POST /api/whatsapp/twilio/send — envia un mensaje desde el chat del dashboard.</summary>
    [HttpPost("send")]
    [Authorize]
    public async Task<IActionResult> Send([FromBody] SendRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Numero) || string.IsNullOrWhiteSpace(req.Mensaje))
            return BadRequest(new { error = "Numero y mensaje son obligatorios" });

        if (!_twilio.IsConfigured)
            return StatusCode(503, new { error = "Twilio no configurado: agregar TWILIO_ACCOUNT_SID y TWILIO_AUTH_TOKEN al .env" });

        try
        {
            var sid = await _twilio.SendTextAsync(req.Numero, req.Mensaje);
            var msg = new WhatsAppTwilioMensaje
            {
                Direccion = "OUTGOING",
                Numero = req.Numero,
                Cuerpo = req.Mensaje,
                TwilioMessageSid = sid,
                Procesado = true,
                CreatedAt = DateTime.UtcNow
            };
            _db.WhatsAppTwilioMensajes.Add(msg);
            await _db.SaveChangesAsync();
            return Ok(new { ok = true, sid, id = msg.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando mensaje WhatsApp Twilio");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>GET /api/whatsapp/twilio/conversaciones — lista numeros agrupados con ultimo mensaje.
    /// Si el numero esta en WhatsApp_TwilioContactos, devuelve NombreContacto + Rol (prevalece sobre NombrePerfil de WhatsApp).</summary>
    [HttpGet("conversaciones")]
    [Authorize]
    public async Task<IActionResult> Conversaciones()
    {
        var conv = await _db.WhatsAppTwilioMensajes
            .AsNoTracking()
            .GroupBy(m => m.Numero)
            .Select(g => new
            {
                Numero = g.Key,
                NombrePerfil = g.OrderByDescending(m => m.CreatedAt).Where(m => m.Direccion == "INCOMING").Select(m => m.NombrePerfil).FirstOrDefault(),
                UltimoMensaje = g.OrderByDescending(m => m.CreatedAt).Select(m => m.Cuerpo).FirstOrDefault(),
                UltimoDireccion = g.OrderByDescending(m => m.CreatedAt).Select(m => m.Direccion).FirstOrDefault(),
                UltimoAt = g.Max(m => m.CreatedAt),
                Total = g.Count()
            })
            .ToListAsync();
        // Join in-memory con contactos (poco volumen, mas simple que LINQ join)
        var contactos = await _db.WhatsAppTwilioContactos.AsNoTracking()
            .Where(c => c.Activo).ToDictionaryAsync(c => c.Numero, c => c);
        var result = conv.Select(x =>
        {
            contactos.TryGetValue(x.Numero, out var c);
            return new
            {
                x.Numero,
                NombrePerfil = c?.Nombre ?? x.NombrePerfil,
                Rol = c?.Rol,
                x.UltimoMensaje,
                x.UltimoDireccion,
                x.UltimoAt,
                x.Total
            };
        }).OrderByDescending(x => x.UltimoAt).ToList();
        return Ok(result);
    }

    // ===== Respuestas rapidas CRUD =====
    public record RespuestaUpsert(string Nombre, string Texto, int Orden, bool Activo);

    [HttpGet("respuestas-rapidas")]
    [Authorize]
    public async Task<IActionResult> ListarRespuestas()
    {
        var list = await _db.WhatsAppTwilioRespuestasRapidas.AsNoTracking()
            .OrderBy(r => r.Orden).ThenBy(r => r.Id).ToListAsync();
        return Ok(list);
    }

    [HttpPost("respuestas-rapidas")]
    [Authorize]
    public async Task<IActionResult> CrearRespuesta([FromBody] RespuestaUpsert req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre) || string.IsNullOrWhiteSpace(req.Texto))
            return BadRequest(new { error = "Nombre y texto son obligatorios" });
        var r = new WhatsAppTwilioRespuestaRapida
        {
            Nombre = req.Nombre.Trim(),
            Texto = req.Texto,
            Orden = req.Orden,
            Activo = req.Activo
        };
        _db.WhatsAppTwilioRespuestasRapidas.Add(r);
        await _db.SaveChangesAsync();
        return Ok(r);
    }

    [HttpPut("respuestas-rapidas/{id:int}")]
    [Authorize]
    public async Task<IActionResult> EditarRespuesta(int id, [FromBody] RespuestaUpsert req)
    {
        var r = await _db.WhatsAppTwilioRespuestasRapidas.FindAsync(id);
        if (r == null) return NotFound();
        r.Nombre = req.Nombre.Trim();
        r.Texto = req.Texto;
        r.Orden = req.Orden;
        r.Activo = req.Activo;
        await _db.SaveChangesAsync();
        return Ok(r);
    }

    [HttpDelete("respuestas-rapidas/{id:int}")]
    [Authorize]
    public async Task<IActionResult> BorrarRespuesta(int id)
    {
        var r = await _db.WhatsAppTwilioRespuestasRapidas.FindAsync(id);
        if (r == null) return NotFound();
        _db.WhatsAppTwilioRespuestasRapidas.Remove(r);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ===== Contactos CRUD =====
    public record ContactoUpsert(string Numero, string Nombre, string Rol, string? Notas, bool Activo);

    [HttpGet("contactos")]
    [Authorize]
    public async Task<IActionResult> ListarContactos()
    {
        var list = await _db.WhatsAppTwilioContactos.AsNoTracking()
            .OrderBy(c => c.Nombre).ToListAsync();
        return Ok(list);
    }

    [HttpPost("contactos")]
    [Authorize]
    public async Task<IActionResult> CrearContacto([FromBody] ContactoUpsert req)
    {
        if (string.IsNullOrWhiteSpace(req.Numero) || string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { error = "Numero y nombre son obligatorios" });
        var numero = req.Numero.Trim();
        if (!numero.StartsWith("whatsapp:")) numero = "whatsapp:" + numero;
        if (await _db.WhatsAppTwilioContactos.AnyAsync(c => c.Numero == numero))
            return BadRequest(new { error = "Ese numero ya esta cargado" });
        var c = new WhatsAppTwilioContacto
        {
            Numero = numero,
            Nombre = req.Nombre.Trim(),
            Rol = string.IsNullOrWhiteSpace(req.Rol) ? "otro" : req.Rol.Trim(),
            Notas = req.Notas,
            Activo = req.Activo
        };
        _db.WhatsAppTwilioContactos.Add(c);
        await _db.SaveChangesAsync();
        return Ok(c);
    }

    [HttpPut("contactos/{id:int}")]
    [Authorize]
    public async Task<IActionResult> EditarContacto(int id, [FromBody] ContactoUpsert req)
    {
        var c = await _db.WhatsAppTwilioContactos.FindAsync(id);
        if (c == null) return NotFound();
        c.Nombre = req.Nombre.Trim();
        c.Rol = string.IsNullOrWhiteSpace(req.Rol) ? "otro" : req.Rol.Trim();
        c.Notas = req.Notas;
        c.Activo = req.Activo;
        await _db.SaveChangesAsync();
        return Ok(c);
    }

    [HttpDelete("contactos/{id:int}")]
    [Authorize]
    public async Task<IActionResult> BorrarContacto(int id)
    {
        var c = await _db.WhatsAppTwilioContactos.FindAsync(id);
        if (c == null) return NotFound();
        _db.WhatsAppTwilioContactos.Remove(c);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>GET /api/whatsapp/twilio/mensajes?numero=whatsapp:+34... — devuelve el hilo de un numero.</summary>
    [HttpGet("mensajes")]
    [Authorize]
    public async Task<IActionResult> Mensajes([FromQuery] string? numero, [FromQuery] int top = 200)
    {
        var q = _db.WhatsAppTwilioMensajes.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(numero)) q = q.Where(m => m.Numero == numero);
        var msgs = await q
            .OrderByDescending(m => m.CreatedAt)
            .Take(Math.Clamp(top, 1, 500))
            .Select(m => new
            {
                m.Id, m.Direccion, m.Numero, m.NombrePerfil,
                m.Cuerpo, m.MediaUrl, m.NumMedia,
                m.Procesado, m.RespuestaEnviada, m.CreatedAt
            })
            .ToListAsync();
        // Devolver orden cronológico ascendente para el chat
        msgs.Reverse();
        return Ok(msgs);
    }
}
