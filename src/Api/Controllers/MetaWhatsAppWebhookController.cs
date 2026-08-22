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

    // 2026-08-18: aviso en vivo a las pantallas abiertas cuando entra un mensaje.
    private readonly Api.Services.WaLiveNotifier _waLive;

    public MetaWhatsAppWebhookController(IServiceScopeFactory scopeFactory, IConfiguration config,
        ILogger<MetaWhatsAppWebhookController> logger, Api.Services.WaLiveNotifier waLive)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
        _waLive = waLive;
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
        // 2026-08-03: bot interno de empleados (palabra clave por persona → menú de consultas)
        var empBot = sp.GetRequiredService<WhatsAppEmpleadoBotService>();
        // 2026-08-13: asistente para cargar un pago escribiendo "PAGO" (empleado/proveedor → pendiente)
        var pagoBot = sp.GetRequiredService<WhatsAppPagoBotService>();
        // 2026-08-06: aviso de venta a internos (atiende los botones comprobante/cuenta corriente/detalle)
        var avisoSvc = sp.GetRequiredService<VentaAvisoWhatsAppService>();

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return;

        // Instagram DM: Meta postea al MISMO webhook pero con object="instagram" y un formato
        // distinto (entry[].messaging[] estilo Messenger, no entry[].changes[].value.messages).
        var objeto = root.TryGetProperty("object", out var objEl) ? objEl.GetString() : null;
        if (objeto == "instagram")
        {
            var igSvc = sp.GetRequiredService<InstagramDmService>();
            await ProcesarInstagramAsync(db, igSvc, entries, baseUrl);
            return;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var change in changes.EnumerateArray())
            {
                if (!change.TryGetProperty("value", out var value)) continue;

                // 2026-07-24: acuses de entrega — Meta manda "statuses[]" (sent/delivered/read/failed)
                // por cada mensaje que MANDAMOS. Actualizamos el EstadoEntrega del OUTGOING (match por wamid).
                if (value.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
                    await ProcesarEstadosAsync(db, statuses);

                // 2026-08-02: eventos de LLAMADA de voz (Meta Business Calling API). Meta manda
                // "calls[]" cuando un cliente llama, atiende o corta. PRIMER LADRILLO: solo los
                // registramos (todavia no se atiende audio en vivo). Ver ProcesarLlamadasAsync.
                if (value.TryGetProperty("calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                    await ProcesarLlamadasAsync(db, value, calls);

                // Solo procesamos mensajes ENTRANTES abajo (los statuses ya se manejaron arriba).
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
                    await ProcesarMensajeAsync(db, meta, pedidoSvc, listasCtrl, empBot, pagoBot, avisoSvc, m, nombres, baseUrl, lineaId);
            }
        }
    }

    // ═══════════════ INSTAGRAM DM (2026-07-31) ═══════════════
    // Meta manda los DM de Instagram al MISMO webhook con object="instagram". El formato es
    // estilo Messenger: entry[].messaging[] con sender/recipient/message. Guardamos en la MISMA
    // bandeja (WhatsApp_TwilioMensajes) con Canal="INSTAGRAM", número "ig:{IGSID}" y LineaPhoneId =
    // IG User ID de la cuenta, así se lee y responde desde la misma pantalla del chat (selector "Línea").

    private async Task ProcesarInstagramAsync(AppDbContext db, InstagramDmService ig, JsonElement entries, string baseUrl)
    {
        foreach (var entry in entries.EnumerateArray())
        {
            // entry.id = IG User ID de NUESTRA cuenta (la línea). Con eso sabemos por cuál responder.
            var cuentaId = entry.TryGetProperty("id", out var eid) ? eid.GetString() : null;
            if (string.IsNullOrEmpty(cuentaId)) continue;

            // Etiqueta visible de la línea en el chat (ej "IG @frikaf_cafe").
            var cuenta = ig.CuentaPorId(cuentaId);
            if (cuenta is not null)
                await RegistrarLineaAsync(db, cuentaId!, $"IG @{cuenta.Label}");

            if (!entry.TryGetProperty("messaging", out var messaging) || messaging.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var ev in messaging.EnumerateArray())
            {
                if (!ev.TryGetProperty("message", out var message)) continue; // ignoramos reacciones/lecturas/etc.

                var mid = message.TryGetProperty("mid", out var midEl) ? midEl.GetString() : null;
                var esEco = message.TryGetProperty("is_echo", out var ecoEl) && ecoEl.ValueKind == JsonValueKind.True;

                var senderId = ev.TryGetProperty("sender", out var s) && s.TryGetProperty("id", out var sid) ? sid.GetString() : null;
                var recipientId = ev.TryGetProperty("recipient", out var r) && r.TryGetProperty("id", out var rid) ? rid.GetString() : null;

                // is_echo=true → lo mandó NUESTRA cuenta (ej. desde la app de IG). El "otro" es el destinatario.
                var direccion = esEco ? "OUTGOING" : "INCOMING";
                var otroId = esEco ? recipientId : senderId;
                if (string.IsNullOrEmpty(otroId)) continue;

                // Deduplicar: Meta entrega "at least once" y además nos re-eco nuestros propios envíos.
                if (!string.IsNullOrEmpty(mid) &&
                    await db.WhatsAppTwilioMensajes.AnyAsync(x => x.TwilioMessageSid == mid))
                {
                    _logger.LogInformation("[Instagram DM] mid {Mid} ya procesado, salteo", mid);
                    continue;
                }

                var texto = message.TryGetProperty("text", out var tEl) ? tEl.GetString() : null;

                // Adjuntos: en IG la URL viene DIRECTA en el webhook (no un media_id como WhatsApp).
                string? mediaUrl = null, mediaNombre = null;
                if (message.TryGetProperty("attachments", out var atts) && atts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var att in atts.EnumerateArray())
                    {
                        var tipoAtt = att.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                        var url = att.TryGetProperty("payload", out var pl) && pl.TryGetProperty("url", out var ul) ? ul.GetString() : null;
                        if (string.IsNullOrWhiteSpace(url)) continue;
                        (mediaUrl, mediaNombre) = await GuardarAdjuntoIgAsync(db, ig, url!, tipoAtt ?? "file", baseUrl);
                        if (mediaUrl != null) break; // guardamos el primero; alcanza para verlo en el chat
                        if (string.IsNullOrWhiteSpace(texto)) texto = $"[{tipoAtt} de Instagram]";
                    }
                }

                var numero = WhatsAppOutboundService.IgPrefix + otroId;

                // Nombre del remitente: reusamos el @usuario que ya tengamos guardado para este contacto
                // (así TODOS los mensajes lo llevan, no solo el primero). Si no hay ninguno, lo pedimos a Instagram.
                string? nombrePerfil = null;
                if (direccion == "INCOMING")
                {
                    nombrePerfil = await db.WhatsAppTwilioMensajes
                        .Where(m => m.Numero == numero && m.NombrePerfil != null)
                        .OrderByDescending(m => m.CreatedAt)
                        .Select(m => m.NombrePerfil)
                        .FirstOrDefaultAsync();
                    if (string.IsNullOrEmpty(nombrePerfil))
                    {
                        var (username, name) = await ig.GetPerfilAsync(cuentaId!, otroId!);
                        nombrePerfil = username != null ? "@" + username : name;
                    }
                }

                db.WhatsAppTwilioMensajes.Add(new WhatsAppTwilioMensaje
                {
                    Direccion = direccion,
                    Numero = numero,
                    NombrePerfil = nombrePerfil,
                    Cuerpo = texto,
                    MediaUrl = mediaUrl,
                    MediaFilename = mediaNombre,
                    LineaPhoneId = cuentaId,
                    NumMedia = mediaUrl != null ? 1 : 0,
                    TwilioMessageSid = mid,
                    Canal = "INSTAGRAM",
                    Procesado = true,
                    CreatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
                // 2026-08-18: mismo aviso en vivo que WhatsApp (los DM de IG caen en la misma bandeja).
                await _waLive.AvisarAsync(numero, cuentaId, direccion);
                _logger.LogInformation("[Instagram DM] {Dir} {Numero} (@{Label}): {Body}", direccion, numero, cuenta?.Label, texto);
            }
        }
    }

    /// <summary>Baja un adjunto de Instagram (URL directa) y lo re-hostea igual que los de WhatsApp,
    /// para que la pantalla del chat lo muestre. Devuelve (url pública, nombre) o (null, null).</summary>
    private async Task<(string? Url, string? Nombre)> GuardarAdjuntoIgAsync(AppDbContext db, InstagramDmService ig,
        string url, string tipo, string baseUrl)
    {
        try
        {
            var (bytes, contentType) = await ig.DownloadAsync(url);
            if (bytes is null || bytes.Length == 0) return (null, null);

            Directory.CreateDirectory(UploadsDir);
            var ext = MetaWhatsAppService.ExtensionDesdeMime(contentType);
            var token = GenerarToken();
            var stored = token + ext;
            await System.IO.File.WriteAllBytesAsync(Path.Combine(UploadsDir, stored), bytes);

            var nombre = $"ig-{tipo}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}";
            db.WhatsAppTwilioUploads.Add(new WhatsAppTwilioUpload
            {
                Token = token,
                OriginalFilename = nombre,
                StoredFilename = stored,
                ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType!,
                SizeBytes = bytes.LongLength,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddYears(5) // lo que manda el cliente se conserva
            });
            await db.SaveChangesAsync();

            _logger.LogInformation("[Instagram DM] Adjunto guardado: {Nombre} ({Bytes} bytes)", nombre, bytes.Length);
            return ($"{baseUrl}/api/whatsapp/twilio/files/{token}{ext}", nombre);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Instagram DM] No pude guardar el adjunto de {Url}", url);
            return (null, null);
        }
    }

    /// <summary>2026-07-24: procesa los acuses de entrega de Meta. Cada status trae el wamid del
    /// mensaje (=TwilioMessageSid nuestro) y el estado. Solo "sube" de nivel (sent→delivered→read),
    /// nunca baja, así un delivered tardío no pisa un read.</summary>
    private static async Task ProcesarEstadosAsync(AppDbContext db, JsonElement statuses)
    {
        static int Rank(string? s) => s switch { "sent" => 1, "delivered" => 2, "read" => 3, "failed" => 4, _ => 0 };
        foreach (var st in statuses.EnumerateArray())
        {
            var wamid = st.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var estado = st.TryGetProperty("status", out var stEl) ? stEl.GetString() : null;
            if (string.IsNullOrEmpty(wamid) || string.IsNullOrEmpty(estado)) continue;

            var msg = await db.WhatsAppTwilioMensajes.FirstOrDefaultAsync(m => m.TwilioMessageSid == wamid);
            if (msg is null) continue;
            // failed siempre gana; el resto solo sube de nivel
            if (estado == "failed" || Rank(estado) > Rank(msg.EstadoEntrega))
            {
                msg.EstadoEntrega = estado;
                // 2026-08-22: guardamos POR QUE fallo. Meta lo manda en errors[0] (code + title +
                // error_data.details). Antes se descartaba y en la pantalla quedaba un ⚠ sin explicacion.
                if (estado == "failed")
                {
                    int? codigo = null; string? titulo = null, detalle = null;
                    if (st.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array
                        && errs.GetArrayLength() > 0)
                    {
                        var e0 = errs[0];
                        if (e0.TryGetProperty("code", out var cEl) && cEl.TryGetInt32(out var c)) codigo = c;
                        if (e0.TryGetProperty("title", out var tEl)) titulo = tEl.GetString();
                        if (e0.TryGetProperty("message", out var mEl) && string.IsNullOrWhiteSpace(titulo)) titulo = mEl.GetString();
                        if (e0.TryGetProperty("error_data", out var edEl) && edEl.ValueKind == JsonValueKind.Object
                            && edEl.TryGetProperty("details", out var dEl)) detalle = dEl.GetString();
                    }
                    var motivo = MetaWhatsAppService.MotivoFallaEnCastellano(codigo, titulo, detalle);
                    msg.EntregaErrorCodigo = codigo;
                    msg.EntregaError = motivo.Length > 300 ? motivo.Substring(0, 300) : motivo;
                }
                await db.SaveChangesAsync();
            }
        }
    }

    /// <summary>2026-08-02: PRIMER LADRILLO de llamadas de voz. Registra cada evento de llamada
    /// que Meta manda en value.calls[] (connect / terminate / etc). Todavia NO atiende audio:
    /// esto es la base para tener el historial y, mas adelante, disparar el softphone del navegador.
    /// Deduplica por (CallId, Evento) porque Meta entrega "at least once".</summary>
    private async Task ProcesarLlamadasAsync(AppDbContext db, JsonElement value, JsonElement calls)
    {
        // La línea (a qué número nuestro entró la llamada) viene en value.metadata.
        string? lineaId = null, lineaNumero = null;
        if (value.TryGetProperty("metadata", out var md))
        {
            lineaId = md.TryGetProperty("phone_number_id", out var pid) ? pid.GetString() : null;
            lineaNumero = md.TryGetProperty("display_phone_number", out var dpn) ? dpn.GetString() : null;
        }

        foreach (var call in calls.EnumerateArray())
        {
            var callId = call.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var evento = call.TryGetProperty("event", out var evEl) ? evEl.GetString() : null;

            // Deduplicar: el mismo evento de la misma llamada puede llegar repetido.
            if (!string.IsNullOrEmpty(callId) && !string.IsNullOrEmpty(evento) &&
                await db.WhatsAppLlamadas.AnyAsync(x => x.CallId == callId && x.Evento == evento))
            {
                _logger.LogInformation("[Meta WA llamada] evento {Evento} de {CallId} ya registrado, salteo", evento, callId);
                continue;
            }

            var from = call.TryGetProperty("from", out var fromEl) ? fromEl.GetString() : null;
            var direccion = call.TryGetProperty("direction", out var dirEl) ? dirEl.GetString() : null;
            var estado = call.TryGetProperty("status", out var stEl) ? stEl.GetString() : null;

            int? duracion = null;
            if (call.TryGetProperty("duration", out var durEl))
            {
                if (durEl.ValueKind == JsonValueKind.Number && durEl.TryGetInt32(out var d)) duracion = d;
                else if (durEl.ValueKind == JsonValueKind.String && int.TryParse(durEl.GetString(), out var ds)) duracion = ds;
            }

            DateTime? ts = null;
            if (call.TryGetProperty("timestamp", out var tsEl))
            {
                var tsStr = tsEl.ValueKind == JsonValueKind.String ? tsEl.GetString()
                          : tsEl.ValueKind == JsonValueKind.Number ? tsEl.GetRawText() : null;
                if (long.TryParse(tsStr, out var unix)) ts = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            }

            db.WhatsAppLlamadas.Add(new WhatsAppLlamada
            {
                CallId = callId,
                Numero = MetaWhatsAppService.NormalizeTo(from),
                LineaPhoneId = lineaId,
                LineaNumero = lineaNumero,
                Evento = evento,
                Direccion = direccion,
                Estado = estado,
                DuracionSegundos = duracion,
                TimestampEvento = ts,
                RecibidoAt = DateTime.UtcNow,
                RawJson = call.GetRawText()
            });
            await db.SaveChangesAsync();
            _logger.LogInformation("[Meta WA llamada] {Evento} de {From} (línea {Linea}) callId={CallId}",
                evento, from, lineaNumero, callId);
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
        WhatsAppEmpleadoBotService empBot, WhatsAppPagoBotService pagoBot, VentaAvisoWhatsAppService avisoSvc,
        JsonElement m, Dictionary<string, string> nombres, string baseUrl, string? lineaId)
    {
        var wamid = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var fromWaId = m.TryGetProperty("from", out var fromEl) ? fromEl.GetString() : null;
        var tipo = m.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : "text";
        if (string.IsNullOrEmpty(fromWaId)) return;

        // 2026-08-05: "responder citando". Si el cliente responde a un mensaje puntual, Meta manda
        // context.id = wamid del mensaje citado. Lo guardamos para mostrar la burbuja citada en la pantalla.
        string? replyToSid = null;
        if (m.TryGetProperty("context", out var ctxEl) && ctxEl.TryGetProperty("id", out var ctxIdEl))
            replyToSid = ctxIdEl.GetString();

        // Deduplicar: Meta entrega "at least once".
        if (!string.IsNullOrEmpty(wamid) &&
            await db.WhatsAppTwilioMensajes.AnyAsync(x => x.TwilioMessageSid == wamid))
        {
            _logger.LogInformation("[Meta WA webhook] wamid {Wamid} ya procesado, salteo", wamid);
            return;
        }

        // 2026-08-05: REACCIONES del cliente. Cuando el cliente reacciona con un emoji a uno de
        // NUESTROS mensajes, Meta manda un evento type="reaction" con { message_id, emoji }. No es
        // un mensaje de texto: lo enganchamos como reacción al mensaje original (igual que las
        // reacciones nuestras) para que aparezca el chip debajo del mensaje, en vez de una burbuja
        // vacía. UsuarioId = -1 marca que la reacción es DEL CLIENTE (las nuestras van con null).
        if (tipo == "reaction")
        {
            await ProcesarReaccionEntranteAsync(db, m);
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
            case "contacts":
                // 2026-08-02: nos compartieron uno o más contactos. Los guardamos con un marcador
                // "CONTACTO_WA:" + JSON para mostrarlos como tarjeta (nombre + número + botón escribir).
                cuerpo = ParseContactosEntrantes(m);
                break;
            case "location":
                // 2026-08-05: ubicación compartida → texto + link a Google Maps (antes salía vacío).
                cuerpo = TryGetUbicacion(m);
                break;
            case "order":
                // 2026-08-05: pedido armado desde el catálogo (carrito).
                cuerpo = TryGetPedidoCatalogo(m);
                break;
            case "system":
                // 2026-08-05: aviso de sistema (ej: el cliente cambió su número de teléfono).
                cuerpo = TryGetSistema(m);
                break;
            default:
                // 2026-08-05: cualquier tipo que no sepamos mostrar (unsupported, unknown, encuestas,
                // "ver una vez", ubicación en vivo, pagos, tipos nuevos…). Antes quedaba en BLANCO.
                // Ahora dejamos un cartel CLARO para el que atiende: qué pasó + qué hacer, más el
                // detalle de Meta si vino, y el tipo técnico chiquito al final (para soporte).
                var errMsg = TryGetPrimerErrorMensaje(m);
                cuerpo =
                    "⚠️ El cliente te mandó un mensaje que WhatsApp no permite mostrar acá "
                    + "(puede ser una encuesta, un mensaje de \"ver una vez\", ubicación en vivo, un pago u otro formato especial).\n"
                    + "👉 Pedile que te lo reenvíe como texto o foto así lo podés ver."
                    + (string.IsNullOrWhiteSpace(errMsg) ? "" : $"\n\nℹ️ WhatsApp informó: {errMsg}")
                    + $"\n\n🔧 (dato técnico: tipo \"{tipo}\")";
                _logger.LogWarning("[Meta WA webhook] tipo NO soportado '{Tipo}' de {From}. Payload: {Raw}",
                    tipo, fromWaId, m.GetRawText());
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
            ReplyToSid = replyToSid,
            Canal = "CLOUD",
            Procesado = true,
            CreatedAt = DateTime.UtcNow
        };
        db.WhatsAppTwilioMensajes.Add(msg);
        await db.SaveChangesAsync();
        // 2026-08-18: avisar EN EL MOMENTO a las pantallas abiertas (celu incluido). Va antes de
        // los bots para que el mensaje se vea enseguida aunque despues haya procesamiento.
        await _waLive.AvisarAsync(numero, lineaId, "INCOMING");
        _logger.LogInformation("[Meta WA webhook] IN {Numero} ({Name}): {Body}", numero, nombrePerfil, cuerpo);

        // 2026-08-05 (pedido del usuario): la línea FIJO TRANSRADIO NO tiene automatismos.
        // El mensaje entra a la bandeja igual (arriba ya se guardó), pero acá cortamos ANTES de
        // los robots: nada de bot de empleados, ni detección de pedidos, ni bot de bienvenida.
        // Esa línea se responde 100% a mano. (Fase 2: hacerlo configurable por línea desde pantalla.)
        const string LINEA_SIN_AUTOMATISMOS = "1195191513683780"; // FIJO TRANSRADIO
        if (lineaId == LINEA_SIN_AUTOMATISMOS)
        {
            _logger.LogInformation("[Meta WA webhook] línea FIJO TRANSRADIO: sin automatismos, no corre ningún bot.");
            return;
        }

        // 2026-08-06: AVISO DE VENTA A INTERNOS. Si tocó uno de los botones del aviso
        // ("bot:venta:{accion}:{ventaId}"), le mandamos lo que pidió (comprobante / cuenta corriente /
        // detalle) y cortamos acá. Va ANTES de los otros bots para que no lo confundan.
        var idInteractivo = tipo == "interactive" ? TryGetInteractiveId(m) : null;
        if (!string.IsNullOrEmpty(idInteractivo) && idInteractivo!.StartsWith("bot:venta:"))
        {
            var p = idInteractivo.Split(':'); // bot : venta : accion : ventaId
            if (p.Length == 4 && int.TryParse(p[3], out var ventaIdBtn))
            {
                try { await avisoSvc.HandleBotonAsync(fromWaId!, numero, p[2], ventaIdBtn, lineaId, baseUrl); }
                catch (Exception ex) { _logger.LogError(ex, "[Meta WA webhook] Error atendiendo botón de aviso de venta {Id}", idInteractivo); }
            }
            return;
        }

        // 2026-08-13: ASISTENTE DE PAGO. Si un número autorizado escribió "PAGO", tocó una opción del
        // asistente ("pago:..."), o está a mitad de la carga, lo atiende el bot de pagos y cortamos acá.
        // Va ANTES del bot de empleados para que la carga de pago tenga prioridad.
        var idInteractivoPago = tipo == "interactive" ? TryGetInteractiveId(m) : null;
        if (await pagoBot.TryHandleAsync(fromWaId!, numero, tipo, idInteractivoPago, cuerpo, lineaId))
            return;

        // 2026-08-03: BOT INTERNO DE EMPLEADOS. Si el mensaje es una palabra clave de empleado, una
        // opción de su menú, o la respuesta a una consulta pendiente, lo atiende el bot de empleados
        // y cortamos acá (no dispara pedido ni bienvenida).
        var idBotEmpleado = tipo == "interactive" ? TryGetInteractiveId(m) : null;
        if (await empBot.TryHandleAsync(fromWaId!, numero, tipo, idBotEmpleado, cuerpo, lineaId, baseUrl))
            return;

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
        // Textos del bot: lo que el usuario editó en ⚙️ WhatsApp → "Mensajes del bot" (o los defaults).
        var textos = await BotTextos.CargarAsync(db);

        // ¿Tocó un botón/opción nuestra? El id viene en interactive.button_reply/list_reply.id
        var idTocado = tipo == "interactive" ? TryGetInteractiveId(m) : null;
        var parsed = WhatsAppBotFlow.ParseId(idTocado);

        if (parsed is not null)
        {
            var (nivel, empresa, accion) = parsed.Value;

            if (nivel == "1")
            {
                // Eligió empresa → mandar la lista de opciones (nivel 2)
                // 2026-08-04: sale por la MISMA línea a la que escribió el cliente (lineaId). Antes no
                // se pasaba y caía a la línea por defecto → mensaje fuera de la ventana de 24hs de ESA
                // otra línea → Meta lo rechazaba (failed) y el cliente no recibía nada tras tocar el botón.
                var sid = await meta.SendListAsync(fromWaId, textos.CuerpoNivel2(empresa),
                    textos.BotonListaNivel2, textos.FilasNivel2(empresa), lineaPhoneId: lineaId);
                await RegistrarSalienteAsync(db, numero, textos.CuerpoNivel2(empresa) + " [opciones]", sid, lineaId: lineaId);
                return;
            }

            // Nivel 2: eligió una acción
            var (respuesta, rol) = textos.AccionNivel2(accion ?? "", empresa);

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
        // 2026-07-23 (Centro de Automatizaciones): interruptor del bot. Apagado = no arranca
        // el menú con desconocidos (las respuestas a botones ya mandados siguen andando arriba).
        if (await db.AppSettings.AnyAsync(s => s.Key == "whatsapp.bot.bienvenida_enabled" && s.Value == "false"))
            return;
        if (await db.WhatsAppTwilioContactos.AnyAsync(c => c.Numero == numero && c.Activo)) return;
        // "Ya le mandé el menú" se detecta por la etiqueta "[botones:" que se agrega SIEMPRE al guardar
        // el saliente (así sigue funcionando aunque el usuario edite el texto del saludo).
        if (await db.WhatsAppTwilioMensajes.AnyAsync(x => x.Numero == numero
                && x.Direccion == "OUTGOING" && x.Cuerpo != null && x.Cuerpo.Contains("[botones:")))
            return;

        var sid1 = await meta.SendButtonsAsync(fromWaId, textos.CuerpoNivel1, textos.BotonesNivel1, lineaPhoneId: lineaId);
        await RegistrarSalienteAsync(db, numero, textos.CuerpoNivel1 + " [botones: Frikaf / Intervent / Intereventos]", sid1, lineaId: lineaId);
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

    /// <summary>2026-08-05: guarda (o quita) la reacción con emoji que el cliente puso sobre uno de
    /// NUESTROS mensajes. El evento trae reaction.message_id (el wamid del mensaje reaccionado) y
    /// reaction.emoji (vacío si el cliente SACÓ la reacción). La reacción del cliente se marca con
    /// UsuarioId = -1 para poder mostrarla distinta de las nuestras.</summary>
    private async Task ProcesarReaccionEntranteAsync(AppDbContext db, JsonElement m)
    {
        const int UsuarioCliente = -1; // sentinela: la reacción es del cliente, no nuestra
        if (!m.TryGetProperty("reaction", out var reac))
            return;

        var wamidOriginal = reac.TryGetProperty("message_id", out var midEl) ? midEl.GetString() : null;
        var emoji = reac.TryGetProperty("emoji", out var emEl) ? emEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(wamidOriginal))
            return;

        // Buscar el mensaje NUESTRO al que reaccionó (por su wamid).
        var mensaje = await db.WhatsAppTwilioMensajes
            .FirstOrDefaultAsync(x => x.TwilioMessageSid == wamidOriginal);
        if (mensaje is null)
        {
            _logger.LogInformation("[Meta WA reaccion] no encontré el mensaje {Wamid} para enganchar la reacción", wamidOriginal);
            return;
        }

        // Siempre limpiamos la reacción anterior del cliente sobre este mensaje: WhatsApp permite
        // UNA reacción por persona por mensaje (cambiar de emoji reemplaza la anterior).
        var previas = await db.WhatsAppTwilioReacciones
            .Where(r => r.MensajeId == mensaje.Id && r.UsuarioId == UsuarioCliente)
            .ToListAsync();
        if (previas.Count > 0)
            db.WhatsAppTwilioReacciones.RemoveRange(previas);

        // Emoji vacío = el cliente SACÓ la reacción. Solo la quitamos (ya lo hicimos arriba).
        if (!string.IsNullOrWhiteSpace(emoji))
        {
            db.WhatsAppTwilioReacciones.Add(new WhatsAppTwilioReaccion
            {
                MensajeId = mensaje.Id,
                Emoji = emoji!,
                UsuarioId = UsuarioCliente,
                CreatedAt = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();
        _logger.LogInformation("[Meta WA reaccion] cliente {Emoji} sobre mensaje {Id}",
            string.IsNullOrWhiteSpace(emoji) ? "(quitó)" : emoji, mensaje.Id);
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

    // 2026-08-05: coordenada (lat/lng) que Meta puede mandar como número o como string.
    private static string? NumOStr(JsonElement o, string key)
        => o.TryGetProperty(key, out var e)
            ? (e.ValueKind == JsonValueKind.Number ? e.GetRawText() : e.GetString())
            : null;

    /// <summary>2026-08-05: mensaje de UBICACIÓN entrante → texto legible + link a Google Maps.</summary>
    private static string TryGetUbicacion(JsonElement m)
    {
        if (!m.TryGetProperty("location", out var loc)) return "📍 Ubicación (sin datos)";
        var lat = NumOStr(loc, "latitude");
        var lng = NumOStr(loc, "longitude");
        var nombre = loc.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
        var direccion = loc.TryGetProperty("address", out var aEl) ? aEl.GetString() : null;
        var partes = new List<string> { "📍 Ubicación" };
        if (!string.IsNullOrWhiteSpace(nombre)) partes.Add(nombre!);
        if (!string.IsNullOrWhiteSpace(direccion)) partes.Add(direccion!);
        if (!string.IsNullOrWhiteSpace(lat) && !string.IsNullOrWhiteSpace(lng))
            partes.Add($"https://www.google.com/maps?q={lat},{lng}");
        return string.Join("\n", partes);
    }

    /// <summary>2026-08-05: pedido armado desde el catálogo (carrito) → resumen legible.</summary>
    private static string TryGetPedidoCatalogo(JsonElement m)
    {
        if (!m.TryGetProperty("order", out var ord)) return "🛒 Pedido del catálogo";
        var items = ord.TryGetProperty("product_items", out var pit) && pit.ValueKind == JsonValueKind.Array
            ? pit.GetArrayLength() : 0;
        var nota = ord.TryGetProperty("text", out var tEl) ? tEl.GetString() : null;
        var cuerpo = $"🛒 Pedido del catálogo — {items} producto(s)";
        if (!string.IsNullOrWhiteSpace(nota)) cuerpo += $"\n{nota}";
        return cuerpo;
    }

    /// <summary>2026-08-05: mensaje de sistema (ej: el cliente cambió su número).</summary>
    private static string TryGetSistema(JsonElement m)
        => m.TryGetProperty("system", out var s) && s.TryGetProperty("body", out var b) && !string.IsNullOrWhiteSpace(b.GetString())
            ? "⚙️ " + b.GetString()
            : "⚙️ Mensaje de sistema de WhatsApp";

    /// <summary>2026-08-05: cuando WhatsApp marca un mensaje como "no soportado", suele venir un
    /// array "errors" con un título/detalle que aclara qué era. Devuelve ese texto o null.</summary>
    private static string? TryGetPrimerErrorMensaje(JsonElement m)
    {
        if (!m.TryGetProperty("errors", out var errs) || errs.ValueKind != JsonValueKind.Array || errs.GetArrayLength() == 0)
            return null;
        var e = errs[0];
        if (e.TryGetProperty("error_data", out var ed) && ed.TryGetProperty("details", out var det)
            && !string.IsNullOrWhiteSpace(det.GetString()))
            return det.GetString();
        if (e.TryGetProperty("title", out var ti) && !string.IsNullOrWhiteSpace(ti.GetString()))
            return ti.GetString();
        if (e.TryGetProperty("message", out var ms) && !string.IsNullOrWhiteSpace(ms.GetString()))
            return ms.GetString();
        return null;
    }

    /// <summary>2026-08-02: arma el cuerpo de un mensaje de contactos entrante. Devuelve
    /// "CONTACTO_WA:" + JSON [{n:nombre, t:numero}, …] para que el frontend lo muestre como tarjeta.</summary>
    private static string? ParseContactosEntrantes(JsonElement m)
    {
        if (!m.TryGetProperty("contacts", out var contactos) || contactos.ValueKind != JsonValueKind.Array)
            return null;
        var lista = new List<Dictionary<string, string>>();
        foreach (var c in contactos.EnumerateArray())
        {
            string nombre = "";
            if (c.TryGetProperty("name", out var nm))
            {
                nombre = nm.TryGetProperty("formatted_name", out var fn) ? fn.GetString() ?? ""
                       : nm.TryGetProperty("first_name", out var fi) ? fi.GetString() ?? "" : "";
            }
            string numero = "";
            if (c.TryGetProperty("phones", out var phones) && phones.ValueKind == JsonValueKind.Array)
            {
                foreach (var ph in phones.EnumerateArray())
                {
                    numero = ph.TryGetProperty("wa_id", out var wid) && !string.IsNullOrWhiteSpace(wid.GetString())
                        ? wid.GetString()!
                        : (ph.TryGetProperty("phone", out var phe) ? phe.GetString() ?? "" : "");
                    if (!string.IsNullOrWhiteSpace(numero)) break;
                }
            }
            numero = MetaWhatsAppService.NormalizeTo(numero);   // solo dígitos
            if (string.IsNullOrWhiteSpace(nombre) && string.IsNullOrWhiteSpace(numero)) continue;
            lista.Add(new Dictionary<string, string> { ["n"] = nombre, ["t"] = numero });
        }
        if (lista.Count == 0) return "[contacto]";
        return "CONTACTO_WA:" + System.Text.Json.JsonSerializer.Serialize(lista);
    }

    /// <summary>Convierte el wa_id de Meta (dígitos, ej "5491122334455") al formato de la bandeja ("whatsapp:+5491122334455").</summary>
    private static string NormalizeToInbox(string? waId)
    {
        var digits = MetaWhatsAppService.NormalizeTo(waId);
        return string.IsNullOrEmpty(digits) ? "" : $"whatsapp:+{digits}";
    }
}
