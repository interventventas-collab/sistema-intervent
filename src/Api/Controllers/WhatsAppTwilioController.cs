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

    /// <summary>GET /api/whatsapp/twilio/conversaciones — lista numeros agrupados con ultimo mensaje.</summary>
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
            .OrderByDescending(x => x.UltimoAt)
            .ToListAsync();
        return Ok(conv);
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
