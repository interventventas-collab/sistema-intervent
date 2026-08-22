using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Api.Services;

/// <summary>
/// Envío de mensajes WhatsApp por la API oficial de Meta (WhatsApp Cloud API, graph.facebook.com).
/// Alternativa/espejo de <see cref="TwilioWhatsAppService"/> pero SIN intermediario (más barato).
/// Lee credenciales del entorno:
///   META_WA_TOKEN         -> token de acceso (System User token, no expira si es permanente)
///   META_WA_PHONE_ID      -> ID del número de teléfono (Phone Number ID) que da el Administrador de WhatsApp
///   META_WA_API_VERSION   -> opcional, default "v21.0"
/// El número destino se manda en dígitos E.164 sin "+" ni prefijo "whatsapp:" (lo normaliza esta clase).
/// </summary>
public class MetaWhatsAppService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<MetaWhatsAppService> _logger;

    public MetaWhatsAppService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<MetaWhatsAppService> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    private string Token => _config["META_WA_TOKEN"] ?? Environment.GetEnvironmentVariable("META_WA_TOKEN") ?? "";
    private string PhoneId => _config["META_WA_PHONE_ID"] ?? Environment.GetEnvironmentVariable("META_WA_PHONE_ID") ?? "";

    /// <summary>
    /// 2026-08-20: la línea POR DEFECTO (META_WA_PHONE_ID), la que se usa cuando un envío no dice
    /// por cuál sale. Se expone para que el repartidor de salientes pueda PREFERIRLA en vez de
    /// mandar por donde el destinatario haya escrito último — que hacía saltar los avisos de una
    /// empresa a la otra. Hoy es FRIKAF by INTERVENT (11-2252-5458), la que más se usa con clientes.
    /// </summary>
    public string LineaPorDefecto => PhoneId;
    private string ApiVersion => _config["META_WA_API_VERSION"] ?? Environment.GetEnvironmentVariable("META_WA_API_VERSION") ?? "v21.0";
    // 2026-08-01: WhatsApp Business Account (WABA) ID — necesario para listar las plantillas de mensajes.
    private string WabaId => _config["META_WA_WABA_ID"] ?? Environment.GetEnvironmentVariable("META_WA_WABA_ID") ?? "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Token) && !string.IsNullOrWhiteSpace(PhoneId);

    /// <summary>Deja el número como dígitos puros E.164 (sin "+" ni "whatsapp:"), que es lo que espera Cloud API.</summary>
    public static string NormalizeTo(string? to)
    {
        if (string.IsNullOrWhiteSpace(to)) return "";
        return Regex.Replace(to, "\\D", "");
    }

    /// <summary>
    /// Normaliza un teléfono al formato canónico de la bandeja: "whatsapp:+&lt;E164&gt;".
    /// Pensado para números argentinos que en la ficha del cliente vienen sueltos
    /// (ej "11 5994-5852", "011 5994-5852", "+54 9 11 5994-5852") para que caigan en la
    /// MISMA conversación que abre el webhook de Meta (wa_id "5491159945852") y para que
    /// la Cloud API los entregue (necesita el código de país + el 9 de celular).
    /// Idempotente: si ya viene "whatsapp:+549..." lo deja igual.
    /// </summary>
    public static string ToInboxWhatsApp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw ?? "";
        var digits = NormalizeTo(raw);
        if (string.IsNullOrEmpty(digits)) return raw;

        // 2026-08-06 (fix OSMAR +34 España): si el número YA viene internacional (trae "+", ej
        // "whatsapp:+34643013190"), tiene su propio código de país → NO le pegamos el 549 argentino.
        // Antes, a un +34 de España lo convertíamos en +549 34..., abría un chat fantasma y los
        // adjuntos NO se entregaban. El 549 solo se agrega a números LOCALES argentinos sueltos.
        bool yaInternacional = raw.Contains('+');

        if (digits.StartsWith("549"))
        {
            // ya es canónico argentino (país 54 + 9 de celular)
        }
        else if (digits.StartsWith("54"))
        {
            // "54" + local argentino, pero sin el 9 de celular → se lo insertamos (54 es solo Argentina)
            digits = "549" + digits.Substring(2);
        }
        else if (!yaInternacional)
        {
            // número LOCAL argentino sin país: sacamos ceros de discado nacional al inicio.
            digits = digits.TrimStart('0');
            // 2026-08-06 (fix chat duplicado): si ya trae el "9" de celular pero le falta el "54"
            // (ej "91122652222" = 9 + área + número, 11 díg), solo anteponemos "54" para no duplicar el 9.
            // India (+91) no cae acá porque siempre llega con "+", así que yaInternacional lo saca antes.
            if (digits.StartsWith("9") && digits.Length == 11)
                digits = "54" + digits;
            else
                // local sin el 9 (ej "1159945852", 10 díg) → "549".
                digits = "549" + digits;
        }
        // else: internacional NO argentino (ej +34 España, +55 Brasil) → se deja con su código de país.
        return $"whatsapp:+{digits}";
    }

    /// <summary>2026-08-22: traduce el motivo de rechazo que manda Meta (errors[0]) a algo que el
    /// operador entienda sin buscar en Google. Si el código no está en la lista, devolvemos el texto
    /// de Meta tal cual, que es mejor que nada. Códigos: developers.facebook.com/docs/whatsapp/cloud-api/support/error-codes
    /// </summary>
    public static string MotivoFallaEnCastellano(int? codigo, string? titulo, string? detalle)
    {
        var propio = codigo switch
        {
            131026 => "Ese número no tiene WhatsApp, o no puede recibir mensajes. Verificá el número con el celular.",
            131047 => "Pasaron más de 24 hs desde el último mensaje del cliente: hay que escribirle con una plantilla.",
            131049 => "WhatsApp frenó este envío de marketing para no saturar al cliente. Probá más tarde o con otro tipo de mensaje.",
            130472 => "El cliente no recibe mensajes de marketing (Meta lo tiene en una prueba). Un mensaje de otro tipo sí llega.",
            131042 => "Problema con el método de pago de la cuenta de WhatsApp. Hay que revisarlo en Meta Business.",
            131031 => "La cuenta de WhatsApp está bloqueada o inhabilitada por Meta.",
            131051 => "Ese tipo de mensaje no está soportado.",
            131053 => "Meta no pudo bajar el archivo adjunto. Probá mandarlo más chico o en otro formato.",
            131056 => "Demasiados mensajes seguidos a ese mismo número. Esperá un rato.",
            132000 => "La plantilla no coincide con los datos enviados (faltan o sobran variables).",
            132001 => "Esa plantilla no existe o no está aprobada para este idioma.",
            132005 => "El texto armado no entra en la plantilla aprobada.",
            132007 => "La plantilla fue rechazada por Meta.",
            132012 => "Un dato de la plantilla tiene un formato que Meta no acepta.",
            132015 => "La plantilla está pausada por mala calidad (muchos clientes la reportaron).",
            132016 => "La plantilla fue deshabilitada por mala calidad.",
            133010 => "El número de la línea no está registrado en WhatsApp.",
            135000 => "Meta rechazó el mensaje por un problema genérico. Revisá el número y la plantilla.",
            _ => null
        };
        if (propio != null) return propio;
        var texto = string.Join(" · ", new[] { titulo, detalle }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(texto)) texto = "WhatsApp no explicó el motivo.";
        return codigo.HasValue ? $"{texto} (código {codigo})" : texto;
    }

    private HttpClient NewClient()
    {
        var http = _httpFactory.CreateClient();
        http.BaseAddress = new Uri($"https://graph.facebook.com/{ApiVersion}/");
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        http.Timeout = TimeSpan.FromSeconds(30);
        return http;
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Meta WhatsApp Cloud API no configurado: faltan META_WA_TOKEN / META_WA_PHONE_ID en el entorno.");
    }

    /// <summary>Envía un mensaje de texto simple. Devuelve el wamid (id del mensaje en Meta) o null si falla.
    /// 2026-08-05: replyToWamid = si viene, el mensaje sale CITANDO ese wamid (responder citando).</summary>
    public async Task<string?> SendTextAsync(string to, string body, CancellationToken ct = default, string? lineaPhoneId = null, string? replyToWamid = null)
    {
        EnsureConfigured();
        var payload = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = NormalizeTo(to),
            ["type"] = "text",
            ["text"] = new { preview_url = false, body }
        };
        // OJO: Meta rechaza el JSON si "context" viene en null, por eso solo lo agregamos si hay wamid.
        if (!string.IsNullOrWhiteSpace(replyToWamid))
            payload["context"] = new { message_id = replyToWamid };
        return await PostMessageAsync(payload, to, ct, lineaPhoneId);
    }

    /// <summary>2026-07-23 (bot de bienvenida): mensaje con BOTONES de respuesta rápida.
    /// WhatsApp permite MÁXIMO 3 botones, títulos de hasta 20 caracteres. El id vuelve en el
    /// webhook (interactive.button_reply.id) para saber qué tocó el cliente.</summary>
    public async Task<string?> SendButtonsAsync(string to, string body, IEnumerable<(string Id, string Title)> botones, CancellationToken ct = default, string? lineaPhoneId = null)
    {
        EnsureConfigured();
        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = NormalizeTo(to),
            type = "interactive",
            interactive = new
            {
                type = "button",
                body = new { text = body },
                action = new
                {
                    buttons = botones.Take(3).Select(b => new
                    {
                        type = "reply",
                        reply = new { id = b.Id, title = b.Title.Length > 20 ? b.Title[..20] : b.Title }
                    }).ToArray()
                }
            }
        };
        return await PostMessageAsync(payload, to, ct, lineaPhoneId);
    }

    /// <summary>2026-07-23 (bot de bienvenida): mensaje con LISTA desplegable (hasta 10 opciones).
    /// El cliente toca el botón y se abre el menú. El id de la fila vuelve por el webhook
    /// (interactive.list_reply.id). Títulos hasta 24 chars, descripción hasta 72.</summary>
    public async Task<string?> SendListAsync(string to, string body, string botonLabel, IEnumerable<(string Id, string Title, string? Desc)> filas, CancellationToken ct = default, string? lineaPhoneId = null)
    {
        EnsureConfigured();
        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = NormalizeTo(to),
            type = "interactive",
            interactive = new
            {
                type = "list",
                body = new { text = body },
                action = new
                {
                    button = botonLabel.Length > 20 ? botonLabel[..20] : botonLabel,
                    sections = new[]
                    {
                        new
                        {
                            // OJO: Meta rechaza el JSON con campos en null → armamos cada fila
                            // solo con los campos que tienen valor (mismo truco que SendMediaAsync).
                            rows = filas.Take(10).Select(f =>
                            {
                                var row = new Dictionary<string, object>
                                {
                                    ["id"] = f.Id,
                                    ["title"] = f.Title.Length > 24 ? f.Title[..24] : f.Title
                                };
                                if (!string.IsNullOrWhiteSpace(f.Desc))
                                    row["description"] = f.Desc!.Length > 72 ? f.Desc[..72] : f.Desc;
                                return row;
                            }).ToArray()
                        }
                    }
                }
            }
        };
        return await PostMessageAsync(payload, to, ct, lineaPhoneId);
    }

    /// <summary>2026-07-23: envía una REACCIÓN real (el cliente la ve en su WhatsApp, como en el celu).
    /// messageId es el wamid del mensaje al que se reacciona. Emoji vacío = quitar la reacción.
    /// OJO: WhatsApp permite UNA sola reacción nuestra por mensaje — mandar otra la reemplaza.</summary>
    public async Task<string?> SendReactionAsync(string to, string messageId, string emoji, CancellationToken ct = default, string? lineaPhoneId = null)
    {
        EnsureConfigured();
        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = NormalizeTo(to),
            type = "reaction",
            reaction = new { message_id = messageId, emoji = emoji ?? "" }
        };
        return await PostMessageAsync(payload, to, ct, lineaPhoneId);
    }

    /// <summary>Envía un mensaje con un adjunto por LINK. mediaUrl debe ser URL HTTPS pública.
    /// isDocument=true para PDF/archivos; false para imágenes. filename es opcional (solo documentos).
    /// OJO: Meta rechaza el JSON si mandamos campos en null, por eso el objeto se arma sin ellos.</summary>
    public async Task<string?> SendMediaAsync(string to, string mediaUrl, string? caption = null, bool isDocument = false, string? filename = null, CancellationToken ct = default, string? lineaPhoneId = null)
    {
        EnsureConfigured();

        // Armamos el objeto media SOLO con los campos que tienen valor.
        var media = new Dictionary<string, object?> { ["link"] = mediaUrl };
        if (!string.IsNullOrWhiteSpace(caption)) media["caption"] = caption;
        if (isDocument && !string.IsNullOrWhiteSpace(filename)) media["filename"] = filename;

        var tipo = isDocument ? "document" : "image";
        var payload = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = NormalizeTo(to),
            ["type"] = tipo,
            [tipo] = media
        };
        return await PostMessageAsync(payload, to, ct, lineaPhoneId);
    }

    /// <summary>2026-08-01: envía un AUDIO como NOTA DE VOZ (type:audio). mediaUrl debe servir un
    /// OGG/OPUS (u otro formato de audio que acepte WhatsApp) por HTTPS público. Devuelve el wamid o null.</summary>
    public async Task<string?> SendAudioAsync(string to, string mediaUrl, CancellationToken ct = default, string? lineaPhoneId = null)
    {
        EnsureConfigured();
        var payload = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = NormalizeTo(to),
            ["type"] = "audio",
            ["audio"] = new { link = mediaUrl }
        };
        return await PostMessageAsync(payload, to, ct, lineaPhoneId);
    }

    /// <summary>2026-08-02: envía un CONTACTO (tarjeta de contacto, type:contacts). El cliente lo
    /// recibe como un contacto real de WhatsApp que puede guardar con un toque. Devuelve el wamid o null.</summary>
    public async Task<string?> SendContactAsync(string to, string nombre, string numero, CancellationToken ct = default, string? lineaPhoneId = null)
    {
        EnsureConfigured();
        var numeroLimpio = NormalizeTo(numero);                 // solo dígitos
        var payload = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = NormalizeTo(to),
            ["type"] = "contacts",
            ["contacts"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = new Dictionary<string, object?>
                    {
                        ["formatted_name"] = string.IsNullOrWhiteSpace(nombre) ? numeroLimpio : nombre,
                        ["first_name"] = string.IsNullOrWhiteSpace(nombre) ? numeroLimpio : nombre
                    },
                    ["phones"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["phone"] = "+" + numeroLimpio,
                            ["type"] = "CELL",
                            ["wa_id"] = numeroLimpio
                        }
                    }
                }
            }
        };
        return await PostMessageAsync(payload, to, ct, lineaPhoneId);
    }

    /// <summary>2026-08-01: envía una PLANTILLA aprobada (para INICIAR conversación fuera de la ventana de 24h).
    /// bodyParams son los valores de las variables {{1}}, {{2}}… del cuerpo (en orden). Devuelve el wamid o null.</summary>
    public async Task<string?> SendTemplateAsync(string to, string templateName, string languageCode, IList<string>? bodyParams, CancellationToken ct = default, string? lineaPhoneId = null)
    {
        EnsureConfigured();
        var template = new Dictionary<string, object?>
        {
            ["name"] = templateName,
            ["language"] = new { code = languageCode }
        };
        if (bodyParams != null && bodyParams.Count > 0)
        {
            template["components"] = new object[]
            {
                new
                {
                    type = "body",
                    parameters = bodyParams.Select(p => new { type = "text", text = p ?? "" }).ToArray()
                }
            };
        }
        var payload = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = NormalizeTo(to),
            ["type"] = "template",
            ["template"] = template
        };
        return await PostMessageAsync(payload, to, ct, lineaPhoneId);
    }

    /// <summary>2026-08-01: lista las plantillas de mensajes de la WABA (nombre, estado, categoría, idioma,
    /// texto del cuerpo y cuántas variables tiene). Vacío si no hay WABA configurada o falla.</summary>
    public async Task<List<PlantillaInfo>> GetTemplatesAsync(CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(WabaId)) return new();
        try
        {
            using var http = NewClient();
            var resp = await http.GetAsync($"{WabaId}/message_templates?fields=name,status,category,language,components&limit=200", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Meta templates GET falló: {Status} {Body}", (int)resp.StatusCode, body);
                return new();
            }
            using var doc = JsonDocument.Parse(body);
            var list = new List<PlantillaInfo>();
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var t in data.EnumerateArray())
                {
                    var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var status = t.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                    var category = t.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "";
                    var language = t.TryGetProperty("language", out var l) ? l.GetString() ?? "" : "";
                    var bodyText = "";
                    if (t.TryGetProperty("components", out var comps))
                    {
                        foreach (var comp in comps.EnumerateArray())
                        {
                            if (comp.TryGetProperty("type", out var ctype) && string.Equals(ctype.GetString(), "BODY", StringComparison.OrdinalIgnoreCase))
                            {
                                bodyText = comp.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "";
                                break;
                            }
                        }
                    }
                    list.Add(new PlantillaInfo(name, status, category, language, bodyText, CountVariables(bodyText)));
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listando plantillas de Meta");
            return new();
        }
    }

    /// <summary>Cuenta las variables {{1}}, {{2}}… de un texto de plantilla (devuelve el número más alto).</summary>
    private static int CountVariables(string body)
    {
        if (string.IsNullOrEmpty(body)) return 0;
        var max = 0;
        foreach (Match m in Regex.Matches(body, @"\{\{\s*(\d+)\s*\}\}"))
            if (int.TryParse(m.Groups[1].Value, out var num) && num > max) max = num;
        return max;
    }

    /// <summary>Reemplaza {{1}}, {{2}}… por los valores dados, para guardar en la bandeja lo que se mandó.</summary>
    public static string RenderTemplateBody(string? bodyText, IList<string>? vars)
    {
        if (string.IsNullOrEmpty(bodyText)) return bodyText ?? "";
        if (vars == null || vars.Count == 0) return bodyText;
        return Regex.Replace(bodyText, @"\{\{\s*(\d+)\s*\}\}", m =>
        {
            if (int.TryParse(m.Groups[1].Value, out var idx) && idx >= 1 && idx <= vars.Count)
                return vars[idx - 1] ?? "";
            return m.Value;
        });
    }

    // ═══════════════ LLAMADAS DE VOZ (Meta Business Calling API, 2026-08-02) ═══════════════
    // Para "contestar" una llamada entrante se le manda a Meta una acción sobre la llamada:
    //   POST /{phone_number_id}/calls
    //   { messaging_product:"whatsapp", call_id:"...", action:"accept|pre_accept|reject|terminate",
    //     session:{ sdp_type:"answer", sdp:"..." } }   (session solo en accept/pre_accept)
    // El SDP "answer" lo genera el softphone del navegador (WebRTC). El audio va aparte por WebRTC
    // (STUN/TURN), no por esta API — esto es solo la señalización.

    /// <summary>Manda una acción de llamada a Meta (accept/pre_accept/reject/terminate). Para accept y
    /// pre_accept hay que pasar el sdpAnswer que generó el navegador. Devuelve (ok, textoError).</summary>
    public async Task<(bool Ok, string? Error)> SendCallActionAsync(string callId, string action,
        string? sdpAnswer = null, string? lineaPhoneId = null, CancellationToken ct = default)
    {
        EnsureConfigured();
        var payload = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["call_id"] = callId,
            ["action"] = action
        };
        if ((action == "accept" || action == "pre_accept") && !string.IsNullOrWhiteSpace(sdpAnswer))
            payload["session"] = new { sdp_type = "answer", sdp = sdpAnswer };

        try
        {
            using var http = NewClient();
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"{lineaPhoneId ?? PhoneId}/calls", content, ct);
            var respBody = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Meta llamada acción {Action} FALLÓ para {CallId}: {Status} {Body}",
                    action, callId, (int)resp.StatusCode, respBody);
                return (false, respBody);
            }
            _logger.LogInformation("Meta llamada acción {Action} OK para {CallId}", action, callId);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error mandando acción {Action} de llamada {CallId} a Meta", action, callId);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Baja un archivo que mandó un cliente (foto, PDF, audio…).
    /// Meta NO manda el archivo en el webhook: manda un <c>media_id</c>. Hay que hacer dos pasos:
    ///   1) GET /{media_id}  -> devuelve una URL temporal + mime_type
    ///   2) GET esa URL (tambien con el Bearer token) -> los bytes del archivo
    /// Devuelve (null, null, null) si algo falla (nunca tira excepcion).
    /// </summary>
    public async Task<(byte[]? Bytes, string? ContentType, string? FileName)> DownloadMediaAsync(string mediaId, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(mediaId)) return (null, null, null);
        try
        {
            // 1) Datos del media (URL temporal + tipo)
            using var http = NewClient();
            var metaResp = await http.GetAsync(mediaId, ct);
            var metaBody = await metaResp.Content.ReadAsStringAsync(ct);
            if (!metaResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Meta media {Id}: no pude obtener la URL ({Status}) {Body}", mediaId, (int)metaResp.StatusCode, metaBody);
                return (null, null, null);
            }

            using var doc = JsonDocument.Parse(metaBody);
            var root = doc.RootElement;
            var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
            var mime = root.TryGetProperty("mime_type", out var m) ? m.GetString() : null;
            var fileName = root.TryGetProperty("file_name", out var f) ? f.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) return (null, null, null);

            // 2) Descargar el archivo. Ojo: la URL es de otro host (lookaside.fbsbx.com)
            //    y TAMBIEN pide el token, por eso usamos un cliente sin BaseAddress.
            using var dl = _httpFactory.CreateClient();
            dl.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
            dl.Timeout = TimeSpan.FromSeconds(90);

            var fileResp = await dl.GetAsync(url, ct);
            if (!fileResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Meta media {Id}: fallo la descarga ({Status})", mediaId, (int)fileResp.StatusCode);
                return (null, null, null);
            }

            var bytes = await fileResp.Content.ReadAsByteArrayAsync(ct);
            mime ??= fileResp.Content.Headers.ContentType?.MediaType;
            _logger.LogInformation("Meta media {Id} descargado: {Bytes} bytes, tipo {Mime}", mediaId, bytes.Length, mime);
            return (bytes, mime, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bajando el media {Id} de Meta", mediaId);
            return (null, null, null);
        }
    }

    /// <summary>Extension sugerida a partir del tipo de archivo (para guardarlo con nombre lindo).</summary>
    public static string ExtensionDesdeMime(string? mime) => (mime ?? "").ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "application/pdf" => ".pdf",
        "audio/ogg" or "audio/ogg; codecs=opus" => ".ogg",
        "audio/mpeg" => ".mp3",
        "audio/mp4" => ".m4a",
        "audio/amr" => ".amr",
        "video/mp4" => ".mp4",
        "video/3gpp" => ".3gp",
        "text/plain" => ".txt",
        "application/msword" => ".doc",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
        "application/vnd.ms-excel" => ".xls",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
        _ => ".bin"
    };

    // 2026-07-23 (multi-línea): lineaPhoneId permite mandar por OTRO número de la misma cuenta
    // (ej: responder por la línea donde el cliente escribió). Null = la línea default del .env.
    private async Task<string?> PostMessageAsync(object payload, string to, CancellationToken ct, string? lineaPhoneId = null)
    {
        try
        {
            using var http = NewClient();
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"{lineaPhoneId ?? PhoneId}/messages", content, ct);
            var respBody = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Meta WhatsApp send FALLÓ a {To}: {Status} {Body}", to, (int)resp.StatusCode, respBody);
                return null;
            }
            // Respuesta: { "messages": [ { "id": "wamid.XXX" } ] }
            using var doc = JsonDocument.Parse(respBody);
            var wamid = doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.GetArrayLength() > 0
                ? msgs[0].GetProperty("id").GetString()
                : null;
            _logger.LogInformation("Meta WhatsApp enviado a {To}: wamid={Wamid}", to, wamid);
            return wamid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando Meta WhatsApp a {To}", to);
            return null;
        }
    }
}

/// <summary>2026-08-01: info de una plantilla de mensajes de la WABA (para el selector de "Nueva conversación").</summary>
public record PlantillaInfo(string Name, string Status, string Category, string Language, string BodyText, int VariableCount);
