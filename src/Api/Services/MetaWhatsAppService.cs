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

    /// <summary>Envía un mensaje con un adjunto por LINK (imagen/documento). mediaUrl debe ser URL HTTPS pública.
    /// isDocument=true para PDF/archivos; false para imágenes. filename es opcional (solo documentos).</summary>
    public async Task<string?> SendMediaAsync(string to, string mediaUrl, string? caption = null, bool isDocument = false, string? filename = null, CancellationToken ct = default)
    {
        EnsureConfigured();
        object media = isDocument
            ? new { link = mediaUrl, caption, filename }
            : new { link = mediaUrl, caption };
        var payload = new Dictionary<string, object?>
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = NormalizeTo(to),
            ["type"] = isDocument ? "document" : "image",
            [isDocument ? "document" : "image"] = media
        };
        return await PostMessageAsync(payload, to, ct);
    }

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
