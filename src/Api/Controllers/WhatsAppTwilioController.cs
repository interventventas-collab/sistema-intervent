using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Security.Cryptography;

namespace Api.Controllers;

/// <summary>
/// Webhook receptor de Twilio WhatsApp. Twilio postea aca cada vez que un cliente
/// nos manda un mensaje. Guardamos en DB y respondemos con TwiML.
/// 2026-06-19: Fase 1 - solo guarda mensajes y responde acuse. Sin procesamiento de pedidos aun.
/// </summary>
[ApiController]
[Route("api/whatsapp/twilio")]
public class WhatsAppTwilioController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<WhatsAppTwilioController> _logger;
    private readonly IConfiguration _config;

    public WhatsAppTwilioController(AppDbContext db, ILogger<WhatsAppTwilioController> logger, IConfiguration config)
    {
        _db = db;
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// POST /api/whatsapp/twilio/webhook
    /// Twilio postea aca con application/x-www-form-urlencoded.
    /// Form fields: From, To, Body, ProfileName, MessageSid, NumMedia, MediaUrl0, etc.
    /// </summary>
    [HttpPost("webhook")]
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
            Procesado = false,
            CreatedAt = DateTime.UtcNow
        };
        _db.WhatsAppTwilioMensajes.Add(msg);
        await _db.SaveChangesAsync();

        // Respuesta TwiML (XML). En Fase 1, acuse simple.
        var respuesta = body.Trim().Equals("confirm", StringComparison.OrdinalIgnoreCase)
            ? "Cita confirmada para 12/1 a las 3pm. Te esperamos!"
            : $"Recibimos tu mensaje: \"{body}\". Pronto te respondemos.";

        msg.RespuestaEnviada = respuesta;
        msg.Procesado = true;
        await _db.SaveChangesAsync();

        var twiml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response><Message>{System.Net.WebUtility.HtmlEncode(respuesta)}</Message></Response>";
        return Content(twiml, "text/xml");
    }

    /// <summary>
    /// GET /api/whatsapp/twilio/mensajes — lista los ultimos mensajes recibidos para monitoreo.
    /// </summary>
    [HttpGet("mensajes")]
    public async Task<IActionResult> Listar([FromQuery] int top = 50)
    {
        var msgs = await _db.WhatsAppTwilioMensajes
            .AsNoTracking()
            .OrderByDescending(m => m.CreatedAt)
            .Take(Math.Clamp(top, 1, 500))
            .Select(m => new
            {
                m.Id,
                m.Direccion,
                m.Numero,
                m.NombrePerfil,
                m.Cuerpo,
                m.NumMedia,
                m.Procesado,
                m.RespuestaEnviada,
                m.CreatedAt
            })
            .ToListAsync();
        return Ok(msgs);
    }
}
