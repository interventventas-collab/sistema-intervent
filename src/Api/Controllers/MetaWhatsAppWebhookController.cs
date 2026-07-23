using System.Text.Json;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Endpoint publico que recibe los webhooks de la API oficial de WhatsApp (Meta Cloud API).
/// Es el equivalente al webhook de Twilio (<see cref="WhatsAppTwilioController"/>) pero por la via oficial.
///
/// Requisitos de Meta:
///   - GET /webhook: handshake de verificacion. Meta manda ?hub.mode=subscribe&amp;hub.verify_token=XXX&amp;hub.challenge=NNN
///     y hay que devolver el hub.challenge en texto plano SI el verify_token coincide con META_WA_VERIFY_TOKEN.
///   - POST /webhook: responder 200 rapido (sino Meta reintenta). Meta entrega "at least once" -> deduplicar por wamid.
///
/// Reuso: los mensajes entrantes se guardan en la MISMA tabla que Twilio (WhatsApp_TwilioMensajes) con
/// Canal="CLOUD" y el numero normalizado a "whatsapp:+E164", asi caen en la misma bandeja del dashboard.
/// Si el texto es un trigger de pedido (## o #NUMERO), dispara el MISMO parseo con IA (WhatsAppPedidoService).
/// </summary>
[ApiController]
[Route("api/whatsapp/meta")]
[AllowAnonymous]
public class MetaWhatsAppWebhookController : ControllerBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<MetaWhatsAppWebhookController> _logger;

    public MetaWhatsAppWebhookController(IServiceScopeFactory scopeFactory, IConfiguration config,
        ILogger<MetaWhatsAppWebhookController> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    private string VerifyToken => _config["META_WA_VERIFY_TOKEN"]
        ?? Environment.GetEnvironmentVariable("META_WA_VERIFY_TOKEN") ?? "";

    /// <summary>GET /api/whatsapp/meta/webhook — handshake de verificacion de Meta.</summary>
    [HttpGet("webhook")]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (mode == "subscribe" && !string.IsNullOrEmpty(VerifyToken) && verifyToken == VerifyToken)
        {
            _logger.LogInformation("[Meta WA webhook] handshake OK");
            return Content(challenge ?? "", "text/plain");
        }
        _logger.LogWarning("[Meta WA webhook] handshake RECHAZADO (mode={Mode}, tokenMatch={Match})",
            mode, verifyToken == VerifyToken);
        return StatusCode(403, "verify_token invalido");
    }

    /// <summary>POST /api/whatsapp/meta/webhook — Meta postea aca cada evento (mensajes entrantes, estados, etc).</summary>
    [HttpPost("webhook")]
    public async Task<IActionResult> Receive()
    {
        string raw;
        using (var reader = new StreamReader(Request.Body))
            raw = await reader.ReadToEndAsync();

        // Capturamos la URL publica ACA, porque el procesamiento va en background y ahi
        // el HttpContext ya no esta disponible. Se usa para armar el link de los adjuntos.
        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        // Responder 200 al toque y procesar en background (Meta corta si tardamos).
        _ = Task.Run(async () =>
        {
            try { await ProcesarAsync(raw, baseUrl); }
            catch (Exception ex) { _logger.LogError(ex, "[Meta WA webhook] Error procesando payload"); }
        });

        return Ok();
    }

    private async Task ProcesarAsync(string raw, string baseUrl)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var meta = sp.GetRequiredService<MetaWhatsAppService>();
        var pedidoSvc = sp.GetRequiredService<WhatsAppPedidoService>();

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return;

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value)) continue;

                // Solo nos interesan mensajes entrantes (ignoramos "statuses" = acuses de entrega).
                if (!value.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                    continue;

                // Mapa wa_id -> nombre de perfil (de value.contacts[]).
                var nombres = new Dictionary<string, string>();
                if (value.TryGetProperty("contacts", out var contacts) && contacts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in contacts.EnumerateArray())
                    {
                        var waid = c.TryGetProperty("wa_id", out var w) ? w.GetString() : null;
                        var nombre = c.TryGetProperty("profile", out var p) && p.TryGetProperty("name", out var n)
                            ? n.GetString() : null;
                        if (!string.IsNullOrEmpty(waid) && !string.IsNullOrEmpty(nombre))
                            nombres[waid!] = nombre!;
                    }
                }

                foreach (var m in messages.EnumerateArray())
                    await ProcesarMensajeAsync(db, meta, pedidoSvc, m, nombres, baseUrl);
            }
        }
    }

    private async Task ProcesarMensajeAsync(AppDbContext db, MetaWhatsAppService meta,
        WhatsAppPedidoService pedidoSvc, JsonElement m, Dictionary<string, string> nombres, string baseUrl)
    {
        var wamid = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var fromWaId = m.TryGetProperty("from", out var fromEl) ? fromEl.GetString() : null;
        var tipo = m.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : "text";
        if (string.IsNullOrEmpty(fromWaId)) return;

        // Deduplicar: Meta entrega "at least once".
        if (!string.IsNullOrEmpty(wamid) &&
            await db.WhatsAppTwilioMensajes.AnyAsync(x => x.TwilioMessageSid == wamid))
        {
            _logger.LogInformation("[Meta WA webhook] wamid {Wamid} ya procesado, salteo", wamid);
            return;
        }

        // Extraer el cuerpo segun el tipo. Si es un archivo (foto, PDF, audio…), ademas lo
        // BAJAMOS de Meta y lo guardamos, porque el webhook solo trae un media_id, no el archivo.
        string? cuerpo = null;
        string? mediaUrlPublica = null;
        string? mediaNombre = null;
        switch (tipo)
        {
            case "text":
                cuerpo = m.TryGetProperty("text", out var t) && t.TryGetProperty("body", out var tb) ? tb.GetString() : null;
                break;

            case "image":
            case "document":
            case "audio":
            case "video":
            case "sticker":
                cuerpo = TryGetCaption(m, tipo);
                var mediaId = m.TryGetProperty(tipo, out var mediaEl) && mediaEl.TryGetProperty("id", out var midEl)
                    ? midEl.GetString() : null;
                var nombreOriginal = m.TryGetProperty(tipo, out var mediaEl2) && mediaEl2.TryGetProperty("filename", out var fnEl)
                    ? fnEl.GetString() : null;

                if (!string.IsNullOrWhiteSpace(mediaId))
                    (mediaUrlPublica, mediaNombre) = await GuardarAdjuntoAsync(db, meta, mediaId!, tipo, nombreOriginal, baseUrl);

                // Si no se pudo bajar, al menos dejamos constancia de que mandaron algo.
                if (mediaUrlPublica is null && string.IsNullOrWhiteSpace(cuerpo))
                    cuerpo = $"[{tipo} — no se pudo descargar]";
                break;

            case "button":
                cuerpo = m.TryGetProperty("button", out var btn) && btn.TryGetProperty("text", out var bt) ? bt.GetString() : null;
                break;
            case "interactive":
                cuerpo = TryGetInteractive(m);
                break;
            default:
                cuerpo = null;
                break;
        }

        var numero = NormalizeToInbox(fromWaId);
        nombres.TryGetValue(fromWaId!, out var nombrePerfil);

        var msg = new WhatsAppTwilioMensaje
        {
            Direccion = "INCOMING",
            Numero = numero,
            NombrePerfil = string.IsNullOrEmpty(nombrePerfil) ? null : nombrePerfil,
            Cuerpo = cuerpo,
            MediaUrl = mediaUrlPublica,
            MediaFilename = mediaNombre,
            NumMedia = mediaUrlPublica != null ? 1 : 0,
            TwilioMessageSid = wamid,
            Canal = "CLOUD",
            Procesado = true,
            CreatedAt = DateTime.UtcNow
        };
        db.WhatsAppTwilioMensajes.Add(msg);
        await db.SaveChangesAsync();
        _logger.LogInformation("[Meta WA webhook] IN {Numero} ({Name}): {Body}", numero, nombrePerfil, cuerpo);

        // Si es un trigger de pedido (## o #NUMERO), meterlo en la MISMA cola de pedidos con IA.
        if (WhatsAppPedidoService.EsTriggerValido(cuerpo))
        {
            try
            {
                var telParaPedido = "+" + MetaWhatsAppService.NormalizeTo(fromWaId);
                await pedidoSvc.RecibirPedidoAsync(telParaPedido, cuerpo!, source: "whatsapp_cloud");
                _logger.LogInformation("[Meta WA webhook] pedido encolado desde {Numero}", numero);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Meta WA webhook] Error encolando pedido desde {Numero}", numero);
            }
        }
    }

    // Mismo directorio que usan los adjuntos que subimos nosotros (volumen wa_uploads_prod).
    private const string UploadsDir = "/data/whatsapp-uploads";

    /// <summary>
    /// Baja de Meta el archivo que mandó el cliente y lo guarda igual que los adjuntos propios,
    /// asi la pantalla del chat lo muestra sin tener que tocar nada de la UI.
    /// Devuelve la URL publica del archivo, o null si no se pudo.
    /// </summary>
    private async Task<(string? Url, string? Nombre)> GuardarAdjuntoAsync(AppDbContext db, MetaWhatsAppService meta,
        string mediaId, string tipo, string? nombreOriginal, string baseUrl)
    {
        try
        {
            var (bytes, contentType, fileNameMeta) = await meta.DownloadMediaAsync(mediaId);
            if (bytes is null || bytes.Length == 0) return (null, null);

            Directory.CreateDirectory(UploadsDir);

            // Extension: la del nombre original si vino; si no, la deducimos del tipo de archivo.
            var ext = Path.GetExtension(nombreOriginal ?? fileNameMeta ?? "");
            if (string.IsNullOrWhiteSpace(ext)) ext = MetaWhatsAppService.ExtensionDesdeMime(contentType);

            var token = GenerarToken();
            var stored = token + ext;
            await System.IO.File.WriteAllBytesAsync(Path.Combine(UploadsDir, stored), bytes);

            var nombre = nombreOriginal ?? fileNameMeta ?? $"{tipo}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}";

            db.WhatsAppTwilioUploads.Add(new WhatsAppTwilioUpload
            {
                Token = token,
                OriginalFilename = nombre,
                StoredFilename = stored,
                ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType!,
                SizeBytes = bytes.LongLength,
                CreatedAt = DateTime.UtcNow,
                // OJO: los adjuntos que subimos NOSOTROS duran 24h (solo para que el proveedor los baje).
                // Los que manda el CLIENTE hay que conservarlos (ej: comprobantes de transferencia).
                ExpiresAt = DateTime.UtcNow.AddYears(5)
            });
            await db.SaveChangesAsync();

            _logger.LogInformation("[Meta WA webhook] Adjunto guardado: {Nombre} ({Bytes} bytes)", nombre, bytes.Length);
            // La extension va en la URL para que el chat muestre la vista previa si es una imagen.
            return ($"{baseUrl}/api/whatsapp/twilio/files/{token}{ext}", nombre);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Meta WA webhook] No pude guardar el adjunto {MediaId}", mediaId);
            return (null, null);
        }
    }

    /// <summary>Token random para la URL publica del archivo (mismo formato que los adjuntos propios).</summary>
    private static string GenerarToken()
    {
        var bytes = new byte[24];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').Replace("=", "");
    }

    private static string? TryGetCaption(JsonElement m, string tipo)
        => m.TryGetProperty(tipo, out var media) && media.TryGetProperty("caption", out var cap) ? cap.GetString() : null;

    private static string? TryGetInteractive(JsonElement m)
    {
        if (!m.TryGetProperty("interactive", out var i)) return null;
        if (i.TryGetProperty("button_reply", out var br) && br.TryGetProperty("title", out var bt)) return bt.GetString();
        if (i.TryGetProperty("list_reply", out var lr) && lr.TryGetProperty("title", out var lt)) return lt.GetString();
        return null;
    }

    /// <summary>Convierte el wa_id de Meta (dígitos, ej "5491122334455") al formato de la bandeja ("whatsapp:+5491122334455").</summary>
    private static string NormalizeToInbox(string? waId)
    {
        var digits = MetaWhatsAppService.NormalizeTo(waId);
        return string.IsNullOrEmpty(digits) ? "" : $"whatsapp:+{digits}";
    }
}
