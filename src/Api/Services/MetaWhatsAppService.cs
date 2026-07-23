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
    private string ApiVersion => _config["META_WA_API_VERSION"] ?? Environment.GetEnvironmentVariable("META_WA_API_VERSION") ?? "v21.0";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Token) && !string.IsNullOrWhiteSpace(PhoneId);

    /// <summary>Deja el número como dígitos puros E.164 (sin "+" ni "whatsapp:"), que es lo que espera Cloud API.</summary>
    public static string NormalizeTo(string? to)
    {
        if (string.IsNullOrWhiteSpace(to)) return "";
        return Regex.Replace(to, "\\D", "");
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

    /// <summary>Envía un mensaje de texto simple. Devuelve el wamid (id del mensaje en Meta) o null si falla.</summary>
    public async Task<string?> SendTextAsync(string to, string body, CancellationToken ct = default)
    {
        EnsureConfigured();
        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = NormalizeTo(to),
            type = "text",
            text = new { preview_url = false, body }
        };
        return await PostMessageAsync(payload, to, ct);
    }

    /// <summary>2026-07-23 (bot de bienvenida): mensaje con BOTONES de respuesta rápida.
    /// WhatsApp permite MÁXIMO 3 botones, títulos de hasta 20 caracteres. El id vuelve en el
    /// webhook (interactive.button_reply.id) para saber qué tocó el cliente.</summary>
    public async Task<string?> SendButtonsAsync(string to, string body, IEnumerable<(string Id, string Title)> botones, CancellationToken ct = default)
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
        return await PostMessageAsync(payload, to, ct);
    }

    /// <summary>2026-07-23 (bot de bienvenida): mensaje con LISTA desplegable (hasta 10 opciones).
    /// El cliente toca el botón y se abre el menú. El id de la fila vuelve por el webhook
    /// (interactive.list_reply.id). Títulos hasta 24 chars, descripción hasta 72.</summary>
    public async Task<string?> SendListAsync(string to, string body, string botonLabel, IEnumerable<(string Id, string Title, string? Desc)> filas, CancellationToken ct = default)
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
        return await PostMessageAsync(payload, to, ct);
    }

    /// <summary>2026-07-23: envía una REACCIÓN real (el cliente la ve en su WhatsApp, como en el celu).
    /// messageId es el wamid del mensaje al que se reacciona. Emoji vacío = quitar la reacción.
    /// OJO: WhatsApp permite UNA sola reacción nuestra por mensaje — mandar otra la reemplaza.</summary>
    public async Task<string?> SendReactionAsync(string to, string messageId, string emoji, CancellationToken ct = default)
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
        return await PostMessageAsync(payload, to, ct);
    }

    /// <summary>Envía un mensaje con un adjunto por LINK. mediaUrl debe ser URL HTTPS pública.
    /// isDocument=true para PDF/archivos; false para imágenes. filename es opcional (solo documentos).
    /// OJO: Meta rechaza el JSON si mandamos campos en null, por eso el objeto se arma sin ellos.</summary>
    public async Task<string?> SendMediaAsync(string to, string mediaUrl, string? caption = null, bool isDocument = false, string? filename = null, CancellationToken ct = default)
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
        return await PostMessageAsync(payload, to, ct);
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

    private async Task<string?> PostMessageAsync(object payload, string to, CancellationToken ct)
    {
        try
        {
            using var http = NewClient();
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"{PhoneId}/messages", content, ct);
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
