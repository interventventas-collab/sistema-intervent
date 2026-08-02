using System.Text.Json;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// TRAMO 2 del proyecto de llamadas de voz (2026-08-02): el "teléfono en la pantalla".
///
/// El navegador (softphone WebRTC en Llamadas.razor) usa estos endpoints:
///   GET  pendientes  -> ¿hay alguna llamada sonando? (devuelve la oferta SDP que mandó Meta)
///   POST atender     -> el navegador ya armó su respuesta SDP; la reenviamos a Meta (accept)
///   POST rechazar    -> le decimos a Meta que rechace la llamada
///   POST colgar      -> le decimos a Meta que corte una llamada en curso
///   GET  config      -> servidores STUN/TURN para que el audio pueda pasar (y si está habilitado)
///   GET  historial   -> últimas llamadas registradas (para la solapa de historial)
///
/// El estado de cada llamada se deduce del log de eventos (tabla WhatsApp_Llamadas): una llamada
/// está "sonando" si tiene un evento connect reciente y todavía NINGÚN evento de cierre
/// (terminate/reject del lado de Meta, o atendida/rechazada/colgada que anotamos nosotros).
/// </summary>
[ApiController]
[Route("api/whatsapp/llamadas")]
[Authorize]
public class WhatsAppLlamadasController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly MetaWhatsAppService _meta;
    private readonly IConfiguration _config;
    private readonly ILogger<WhatsAppLlamadasController> _logger;

    public WhatsAppLlamadasController(AppDbContext db, MetaWhatsAppService meta, IConfiguration config,
        ILogger<WhatsAppLlamadasController> logger)
    {
        _db = db;
        _meta = meta;
        _config = config;
        _logger = logger;
    }

    // Eventos que "cierran" una llamada: si el CallId tiene alguno, ya no está sonando.
    private static readonly string[] EventosCierre =
        { "terminate", "reject", "failed", "atendida", "rechazada", "colgada" };

    /// <summary>GET pendientes — llamadas ENTRANTES que están sonando ahora (últimos 3 minutos),
    /// con la oferta SDP para que el navegador arme la respuesta.</summary>
    [HttpGet("pendientes")]
    public async Task<IActionResult> Pendientes()
    {
        var desde = DateTime.UtcNow.AddMinutes(-3);

        // CallIds que ya se cerraron (por Meta o por nosotros) — se excluyen.
        var cerrados = await _db.WhatsAppLlamadas
            .Where(l => l.CallId != null && EventosCierre.Contains(l.Evento))
            .Select(l => l.CallId!)
            .Distinct()
            .ToListAsync();

        // Eventos "connect" recientes de llamadas que inició el cliente y que NO están cerradas.
        var connects = await _db.WhatsAppLlamadas
            .Where(l => l.Evento == "connect" && l.RecibidoAt >= desde
                        && l.CallId != null && !cerrados.Contains(l.CallId))
            .OrderByDescending(l => l.Id)
            .ToListAsync();

        // Una fila por CallId (la más reciente).
        var resultado = connects
            .GroupBy(l => l.CallId)
            .Select(g => g.First())
            .Select(l => new
            {
                callId = l.CallId,
                numero = l.Numero,
                lineaPhoneId = l.LineaPhoneId,
                lineaNumero = l.LineaNumero,
                sdpOffer = ExtraerSdp(l.RawJson),
                nombre = NombreContacto(l.Numero),
                recibidoAt = l.RecibidoAt
            })
            .Where(x => x.sdpOffer != null)
            .ToList();

        return Ok(resultado);
    }

    public record AtenderDto(string CallId, string SdpAnswer);

    /// <summary>POST atender — el navegador ya generó su respuesta SDP; se la reenviamos a Meta
    /// para aceptar la llamada. Después el audio fluye por WebRTC.</summary>
    [HttpPost("atender")]
    public async Task<IActionResult> Atender([FromBody] AtenderDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.CallId) || string.IsNullOrWhiteSpace(dto.SdpAnswer))
            return BadRequest(new { error = "Faltan callId o sdpAnswer" });

        var linea = await LineaDeLlamada(dto.CallId);
        var (ok, err) = await _meta.SendCallActionAsync(dto.CallId, "accept", dto.SdpAnswer, linea);
        if (!ok) return StatusCode(502, new { error = "Meta rechazó el accept", detalle = err });

        await AnotarEventoLocalAsync(dto.CallId, "atendida", linea);
        return Ok(new { ok = true });
    }

    public record CallIdDto(string CallId);

    /// <summary>POST rechazar — le decimos a Meta que rechace la llamada (no se atiende).</summary>
    [HttpPost("rechazar")]
    public async Task<IActionResult> Rechazar([FromBody] CallIdDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.CallId)) return BadRequest(new { error = "Falta callId" });
        var linea = await LineaDeLlamada(dto.CallId);
        var (ok, err) = await _meta.SendCallActionAsync(dto.CallId, "reject", null, linea);
        await AnotarEventoLocalAsync(dto.CallId, "rechazada", linea);
        return ok ? Ok(new { ok = true }) : StatusCode(502, new { error = "Meta rechazó el reject", detalle = err });
    }

    /// <summary>POST colgar — cortar una llamada que está en curso.</summary>
    [HttpPost("colgar")]
    public async Task<IActionResult> Colgar([FromBody] CallIdDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.CallId)) return BadRequest(new { error = "Falta callId" });
        var linea = await LineaDeLlamada(dto.CallId);
        var (ok, err) = await _meta.SendCallActionAsync(dto.CallId, "terminate", null, linea);
        await AnotarEventoLocalAsync(dto.CallId, "colgada", linea);
        return ok ? Ok(new { ok = true }) : StatusCode(502, new { error = "Meta rechazó el terminate", detalle = err });
    }

    /// <summary>GET config — servidores STUN/TURN para el navegador y si las llamadas están habilitadas.
    /// STUN es gratis (Google). TURN es opcional (se configura en .env cuando se levante en DonWeb).</summary>
    [HttpGet("config")]
    public IActionResult Config()
    {
        var iceServers = new List<object>
        {
            new { urls = "stun:stun.l.google.com:19302" }
        };

        var turnUrl = Cfg("TURN_URL");
        if (!string.IsNullOrWhiteSpace(turnUrl))
        {
            var turnUser = Cfg("TURN_USERNAME");
            var turnCred = Cfg("TURN_CREDENTIAL");
            iceServers.Add(new { urls = turnUrl, username = turnUser ?? "", credential = turnCred ?? "" });
        }

        var habilitado = string.Equals(Cfg("WA_LLAMADAS_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);
        return Ok(new { habilitado, iceServers, turnConfigurado = !string.IsNullOrWhiteSpace(turnUrl) });
    }

    /// <summary>GET historial — últimas 100 filas de eventos de llamada, para ver quién llamó y cuándo.</summary>
    [HttpGet("historial")]
    public async Task<IActionResult> Historial()
    {
        var filas = await _db.WhatsAppLlamadas
            .OrderByDescending(l => l.Id)
            .Take(100)
            .Select(l => new
            {
                l.Id, l.CallId, l.Numero, l.LineaNumero, l.Evento, l.Direccion,
                l.Estado, l.DuracionSegundos, l.TimestampEvento, l.RecibidoAt
            })
            .ToListAsync();
        return Ok(filas);
    }

    // ─────────── helpers ───────────

    private string? Cfg(string key) => _config[key] ?? Environment.GetEnvironmentVariable(key);

    /// <summary>Saca el SDP (oferta) del JSON crudo del evento connect: call.session.sdp.</summary>
    private static string? ExtraerSdp(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.TryGetProperty("session", out var s) && s.TryGetProperty("sdp", out var sdp))
                return sdp.GetString();
        }
        catch { /* json raro: lo ignoramos */ }
        return null;
    }

    /// <summary>La línea (phone_number_id) por la que entró la llamada, para responder por ahí.</summary>
    private async Task<string?> LineaDeLlamada(string callId)
        => await _db.WhatsAppLlamadas
            .Where(l => l.CallId == callId && l.LineaPhoneId != null)
            .OrderBy(l => l.Id)
            .Select(l => l.LineaPhoneId)
            .FirstOrDefaultAsync();

    /// <summary>Nombre del contacto si lo tenemos guardado (para mostrar "Juan" en vez del número).</summary>
    private string? NombreContacto(string? numeroDigitos)
    {
        if (string.IsNullOrWhiteSpace(numeroDigitos)) return null;
        var inbox = $"whatsapp:+{numeroDigitos}";
        return _db.WhatsAppTwilioContactos
            .Where(c => c.Numero == inbox)
            .Select(c => c.Nombre)
            .FirstOrDefault();
    }

    /// <summary>Anota un evento nuestro (atendida/rechazada/colgada) en el mismo log, para que la
    /// llamada deje de figurar como "sonando".</summary>
    private async Task AnotarEventoLocalAsync(string callId, string evento, string? linea)
    {
        _db.WhatsAppLlamadas.Add(new WhatsAppLlamada
        {
            CallId = callId,
            LineaPhoneId = linea,
            Evento = evento,
            RecibidoAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        _logger.LogInformation("[Llamadas] evento local {Evento} para {CallId}", evento, callId);
    }
}
