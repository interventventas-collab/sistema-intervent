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
        // 2026-07-23: para que el bot pueda mandar la lista de precios en PDF (opción del nivel 2)
        var listasCtrl = sp.GetRequiredService<Api.Controllers.CafeListasCustomController>();

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

                // 2026-07-23 (multi-línea): a QUÉ número nuestro le escribieron. Con 2+ líneas en la
                // misma cuenta, esto permite etiquetar cada chat y responder por la línea correcta.
                string? lineaId = null, lineaNumero = null;
                if (value.TryGetProperty("metadata", out var md))
                {
                    lineaId = md.TryGetProperty("phone_number_id", out var pid) ? pid.GetString() : null;
                    lineaNumero = md.TryGetProperty("display_phone_number", out var dpn) ? dpn.GetString() : null;
                }
                if (!string.IsNullOrEmpty(lineaId))
                    await RegistrarLineaAsync(db, lineaId!, lineaNumero);

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
                    await ProcesarMensajeAsync(db, meta, pedidoSvc, listasCtrl, m, nombres, baseUrl, lineaId);
            }
        }
    }

    /// <summary>Guarda (una sola vez) el número visible de cada línea nuestra, para mostrarlo
    /// como etiqueta en el chat cuando haya más de una. Clave: whatsapp.linea.{phone_number_id}.</summary>
    private static async Task RegistrarLineaAsync(AppDbContext db, string lineaId, string? lineaNumero)
    {
        var key = $"whatsapp.linea.{lineaId}";
        if (await db.AppSettings.AnyAsync(s => s.Key == key)) return;
        db.AppSettings.Add(new AppSetting { Key = key, Value = lineaNumero ?? lineaId, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task ProcesarMensajeAsync(AppDbContext db, MetaWhatsAppService meta,
        WhatsAppPedidoService pedidoSvc, Api.Controllers.CafeListasCustomController listasCtrl,
        JsonElement m, Dictionary<string, string> nombres, string baseUrl, string? lineaId)
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
            LineaPhoneId = lineaId,
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
            return; // un pedido no dispara el bot de bienvenida
        }

        // 2026-07-23 (pedido Osmar): BOT DE BIENVENIDA con botones.
        try
        {
            await BotBienvenidaAsync(db, meta, listasCtrl, m, tipo, fromWaId!, numero, nombrePerfil, baseUrl, lineaId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Meta WA webhook] Error en el bot de bienvenida para {Numero}", numero);
        }
    }

    // ═══════════════ BOT DE BIENVENIDA (2026-07-23, pedido Osmar) ═══════════════
    // Nivel 1: número desconocido escribe → 3 botones para elegir empresa.
    // Nivel 2: eligió empresa → lista con 4 opciones (pedido / lista de precios / proveedor / persona).
    // Final: lo etiqueta como contacto y responde (la lista de Frikaf manda el PDF solo).
    // Los textos viven en Services/WhatsAppBotFlow.cs.

    private async Task BotBienvenidaAsync(AppDbContext db, MetaWhatsAppService meta,
        Api.Controllers.CafeListasCustomController listasCtrl, JsonElement m,
        string? tipo, string fromWaId, string numero, string? nombrePerfil, string baseUrl, string? lineaId)
    {
        // ¿Tocó un botón/opción nuestra? El id viene en interactive.button_reply/list_reply.id
        var idTocado = tipo == "interactive" ? TryGetInteractiveId(m) : null;
        var parsed = WhatsAppBotFlow.ParseId(idTocado);

        if (parsed is not null)
        {
            var (nivel, empresa, accion) = parsed.Value;

            if (nivel == "1")
            {
                // Eligió empresa → mandar la lista de opciones (nivel 2)
                var sid = await meta.SendListAsync(fromWaId, WhatsAppBotFlow.CuerpoNivel2(empresa),
                    WhatsAppBotFlow.BotonListaNivel2, WhatsAppBotFlow.FilasNivel2(empresa));
                await RegistrarSalienteAsync(db, numero, WhatsAppBotFlow.CuerpoNivel2(empresa) + " [opciones]", sid);
                return;
            }

            // Nivel 2: eligió una acción
            var (respuesta, rol) = WhatsAppBotFlow.AccionNivel2(accion ?? "", empresa);

            // Etiquetar como contacto (solo si todavía no existe — no pisamos contactos cargados a mano)
            if (!await db.WhatsAppTwilioContactos.AnyAsync(c => c.Numero == numero))
            {
                db.WhatsAppTwilioContactos.Add(new WhatsAppTwilioContacto
                {
                    Numero = numero,
                    Nombre = string.IsNullOrWhiteSpace(nombrePerfil) ? numero.Replace("whatsapp:", "") : nombrePerfil!,
                    Rol = rol,
                    Notas = $"🤖 Bot {DateTime.UtcNow.AddHours(-3):dd/MM HH:mm}: eligió {WhatsAppBotFlow.NombreEmpresa(empresa)} → {accion}",
                    Activo = true
                });
                await db.SaveChangesAsync();
            }

            // Acción especial: "lista de precios" de Frikaf manda el PDF automático
            if (accion == "lista" && empresa == "frikaf"
                && await EnviarListaPreciosBotAsync(db, meta, listasCtrl, fromWaId, numero, baseUrl, lineaId))
                return;

            var sid2 = await meta.SendTextAsync(fromWaId, respuesta, lineaPhoneId: lineaId);
            await RegistrarSalienteAsync(db, numero, respuesta, sid2, lineaId: lineaId);
            return;
        }

        // No es un botón nuestro: ¿hay que arrancar el bot? Solo con MENSAJES DE TEXTO de números
        // DESCONOCIDOS (sin contacto) a los que nunca les mandamos el menú. Así no molestamos a
        // clientes/hermanos ya anotados ni repetimos el menú si lo ignoran.
        if (tipo != "text") return;
        if (await db.WhatsAppTwilioContactos.AnyAsync(c => c.Numero == numero && c.Activo)) return;
        if (await db.WhatsAppTwilioMensajes.AnyAsync(x => x.Numero == numero
                && x.Direccion == "OUTGOING" && x.Cuerpo != null && x.Cuerpo.Contains(WhatsAppBotFlow.MarcaNivel1)))
            return;

        var sid1 = await meta.SendButtonsAsync(fromWaId, WhatsAppBotFlow.CuerpoNivel1, WhatsAppBotFlow.BotonesNivel1, lineaPhoneId: lineaId);
        await RegistrarSalienteAsync(db, numero, WhatsAppBotFlow.CuerpoNivel1 + " [botones: Frikaf / Intervent / Intereventos]", sid1, lineaId: lineaId);
    }

    /// <summary>Manda por el bot el PDF de la lista de precios GENERAL activa más reciente
    /// (las que no apuntan a un cliente puntual). Devuelve false si no hay o algo falla,
    /// para que el bot caiga al texto genérico.</summary>
    private async Task<bool> EnviarListaPreciosBotAsync(AppDbContext db, MetaWhatsAppService meta,
        Api.Controllers.CafeListasCustomController listasCtrl, string fromWaId, string numero, string baseUrl, string? lineaId)
    {
        try
        {
            var lista = await db.CafeListasPreciosCustom.AsNoTracking()
                .Where(l => l.IsActive && l.ClienteId == null)
                .OrderByDescending(l => l.UpdatedAt)
                .FirstOrDefaultAsync();
            if (lista is null) return false;

            var (bytes, filename) = await listasCtrl.GenerarPdfBytesAsync(lista.Id);
            if (bytes is null) return false;

            Directory.CreateDirectory(UploadsDir);
            var token = GenerarToken();
            var stored = token + ".pdf";
            await System.IO.File.WriteAllBytesAsync(Path.Combine(UploadsDir, stored), bytes);
            db.WhatsAppTwilioUploads.Add(new WhatsAppTwilioUpload
            {
                Token = token,
                OriginalFilename = filename,
                StoredFilename = stored,
                ContentType = "application/pdf",
                SizeBytes = bytes.Length,
                NumeroDestino = numero,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(24)
            });
            await db.SaveChangesAsync();

            var mediaUrl = $"{baseUrl}/api/whatsapp/twilio/files/{token}.pdf";
            var caption = "¡Acá tenés nuestra lista de precios! ☕ Cualquier consulta escribinos por acá 👍";
            var sid = await meta.SendMediaAsync(fromWaId, mediaUrl, caption, isDocument: true, filename: filename, lineaPhoneId: lineaId);
            await RegistrarSalienteAsync(db, numero, caption, sid, mediaUrl, filename, lineaId);
            return sid != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Meta WA webhook] Bot: no pude mandar la lista de precios a {Numero}", numero);
            return false;
        }
    }

    /// <summary>Registra un mensaje saliente del bot en la bandeja, así se ve en el chat.</summary>
    private static async Task RegistrarSalienteAsync(AppDbContext db, string numero, string cuerpo,
        string? sid, string? mediaUrl = null, string? mediaFilename = null, string? lineaId = null)
    {
        db.WhatsAppTwilioMensajes.Add(new WhatsAppTwilioMensaje
        {
            Direccion = "OUTGOING",
            Numero = numero,
            Cuerpo = cuerpo,
            MediaUrl = mediaUrl,
            MediaFilename = mediaFilename,
            LineaPhoneId = lineaId,
            NumMedia = mediaUrl != null ? 1 : 0,
            TwilioMessageSid = sid,
            Canal = "CLOUD",
            Procesado = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Saca el ID del botón o de la fila de lista que tocó el cliente.</summary>
    private static string? TryGetInteractiveId(JsonElement m)
    {
        if (!m.TryGetProperty("interactive", out var i)) return null;
        if (i.TryGetProperty("button_reply", out var br) && br.TryGetProperty("id", out var bid)) return bid.GetString();
        if (i.TryGetProperty("list_reply", out var lr) && lr.TryGetProperty("id", out var lid)) return lid.GetString();
        return null;
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
