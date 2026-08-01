using System.Text;
using System.Text.Json;

namespace Api.Services;

/// <summary>
/// Envío/recepción de Mensajes Directos (DM) de Instagram por la API oficial de Meta
/// (Instagram API con Instagram Login, base graph.instagram.com). Es el equivalente de
/// <see cref="MetaWhatsAppService"/> pero para Instagram: cada cuenta de IG tiene su propio
/// token (generado desde la app de Meta) y su propio "IG User ID".
///
/// Los mensajes de IG caen en la MISMA bandeja que WhatsApp (tabla WhatsApp_TwilioMensajes)
/// con Canal="INSTAGRAM", así se leen y responden desde la misma pantalla del chat.
///
/// Credenciales (del entorno .env), una terna por cuenta:
///   META_IG_FRIKAF_CAFE_ID / _TOKEN          -> cuenta @frikaf_cafe
///   META_IG_INTERVENT_FRIKAF_ID / _TOKEN     -> cuenta @intervent_frikaf
///   META_IG_ALQUILERES_ID / _TOKEN           -> cuenta @alquileres_intereventos
/// Opcionales:
///   META_IG_API_BASE (default https://graph.instagram.com)
///   META_IG_API_VERSION (default v21.0)
///
/// OJO ventana de 24h: Instagram solo deja RESPONDER dentro de las 24hs desde el último
/// mensaje del usuario. Fuera de eso la API rechaza el envío (devolvemos null y lo logueamos).
/// </summary>
public class InstagramDmService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<InstagramDmService> _logger;

    /// <summary>Una cuenta de Instagram conectada: su token de acceso y su etiqueta visible.</summary>
    public record Cuenta(string Id, string Token, string Label);

    public InstagramDmService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<InstagramDmService> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    private string Cfg(string key) => _config[key] ?? Environment.GetEnvironmentVariable(key) ?? "";
    private string ApiBase => (Cfg("META_IG_API_BASE") is var b && !string.IsNullOrWhiteSpace(b) ? b : "https://graph.instagram.com").TrimEnd('/');
    private string ApiVersion => Cfg("META_IG_API_VERSION") is var v && !string.IsNullOrWhiteSpace(v) ? v : "v21.0";

    /// <summary>Cuentas de Instagram configuradas (las que tienen ID y token en el .env).</summary>
    public IReadOnlyList<Cuenta> Cuentas
    {
        get
        {
            var known = new (string IdKey, string TokenKey, string Label)[]
            {
                ("META_IG_FRIKAF_CAFE_ID",      "META_IG_FRIKAF_CAFE_TOKEN",      "frikaf_cafe"),
                ("META_IG_INTERVENT_FRIKAF_ID", "META_IG_INTERVENT_FRIKAF_TOKEN", "intervent_frikaf"),
                ("META_IG_ALQUILERES_ID",       "META_IG_ALQUILERES_TOKEN",       "alquileres_intereventos"),
            };
            var list = new List<Cuenta>();
            foreach (var (idKey, tokenKey, label) in known)
            {
                var id = Cfg(idKey);
                var token = Cfg(tokenKey);
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(token))
                    list.Add(new Cuenta(id, token, label));
            }
            return list;
        }
    }

    public bool IsConfigured => Cuentas.Count > 0;

    /// <summary>Devuelve la cuenta con ese IG User ID (o null si no está configurada).</summary>
    public Cuenta? CuentaPorId(string? igUserId)
        => string.IsNullOrWhiteSpace(igUserId) ? null : Cuentas.FirstOrDefault(c => c.Id == igUserId);

    private HttpClient NewClient(string token)
    {
        var http = _httpFactory.CreateClient();
        http.BaseAddress = new Uri($"{ApiBase}/{ApiVersion}/");
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        http.Timeout = TimeSpan.FromSeconds(30);
        return http;
    }

    /// <summary>Envía un DM de texto desde <paramref name="igUserId"/> al usuario <paramref name="destinatarioIgsid"/>.
    /// Devuelve el message_id de Instagram o null si falla (ej. fuera de la ventana de 24h).</summary>
    public async Task<string?> SendTextAsync(string igUserId, string destinatarioIgsid, string texto, CancellationToken ct = default)
    {
        var payload = new
        {
            recipient = new { id = destinatarioIgsid },
            message = new { text = texto }
        };
        return await PostMessageAsync(igUserId, payload, destinatarioIgsid, ct);
    }

    /// <summary>Envía un DM con una imagen por link (URL HTTPS pública).</summary>
    public async Task<string?> SendImageAsync(string igUserId, string destinatarioIgsid, string imageUrl, CancellationToken ct = default)
    {
        var payload = new
        {
            recipient = new { id = destinatarioIgsid },
            message = new { attachment = new { type = "image", payload = new { url = imageUrl } } }
        };
        return await PostMessageAsync(igUserId, payload, destinatarioIgsid, ct);
    }

    private async Task<string?> PostMessageAsync(string igUserId, object payload, string destinatario, CancellationToken ct)
    {
        var cuenta = CuentaPorId(igUserId);
        if (cuenta is null)
        {
            _logger.LogWarning("[Instagram DM] No hay cuenta configurada con IG User ID {Id}, no puedo enviar", igUserId);
            return null;
        }
        try
        {
            using var http = NewClient(cuenta.Token);
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"{igUserId}/messages", content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Instagram DM] envío FALLÓ a {Dest} por @{Label}: {Status} {Body}",
                    destinatario, cuenta.Label, (int)resp.StatusCode, body);
                return null;
            }
            // Respuesta esperada: { "recipient_id": "...", "message_id": "..." }
            using var doc = JsonDocument.Parse(body);
            var mid = doc.RootElement.TryGetProperty("message_id", out var m) ? m.GetString() : null;
            _logger.LogInformation("[Instagram DM] enviado a {Dest} por @{Label}: message_id={Mid}", destinatario, cuenta.Label, mid);
            return mid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Instagram DM] Error enviando a {Dest} por IG {Id}", destinatario, igUserId);
            return null;
        }
    }

    /// <summary>Trae el @usuario y nombre del que escribió (para mostrarlo lindo en el chat).
    /// Solo funciona para usuarios que ya le escribieron a esa cuenta. Devuelve (null,null) si falla.</summary>
    public async Task<(string? Username, string? Name)> GetPerfilAsync(string igUserId, string igsid, CancellationToken ct = default)
    {
        var cuenta = CuentaPorId(igUserId);
        if (cuenta is null) return (null, null);
        try
        {
            using var http = NewClient(cuenta.Token);
            var resp = await http.GetAsync($"{igsid}?fields=username,name", ct);
            if (!resp.IsSuccessStatusCode) return (null, null);
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var username = root.TryGetProperty("username", out var u) ? u.GetString() : null;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            return (username, name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Instagram DM] no pude traer el perfil de {Igsid}", igsid);
            return (null, null);
        }
    }

    /// <summary>Baja un adjunto que mandó el usuario (la URL viene directa en el webhook de IG,
    /// a diferencia de WhatsApp que manda un media_id). Devuelve (bytes, contentType) o (null,null).</summary>
    public async Task<(byte[]? Bytes, string? ContentType)> DownloadAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return (null, null);
        try
        {
            using var dl = _httpFactory.CreateClient();
            dl.Timeout = TimeSpan.FromSeconds(90);
            var resp = await dl.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Instagram DM] fallo la descarga del adjunto ({Status})", (int)resp.StatusCode);
                return (null, null);
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            var mime = resp.Content.Headers.ContentType?.MediaType;
            return (bytes, mime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Instagram DM] Error bajando adjunto de {Url}", url);
            return (null, null);
        }
    }
}
