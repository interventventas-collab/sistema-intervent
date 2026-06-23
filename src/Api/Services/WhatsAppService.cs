using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Api.Services;

public class WhatsAppService
{
    private readonly HttpClient _http;
    private readonly ILogger<WhatsAppService> _logger;

    public WhatsAppService(IConfiguration config, ILogger<WhatsAppService> logger)
    {
        _logger = logger;
        var baseUrl = Environment.GetEnvironmentVariable("PLAYWRIGHT_URL")
                      ?? config["PlaywrightUrl"]
                      ?? "http://playwright:3001";
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    // Normaliza un teléfono dejando solo dígitos
    public static string NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "";
        return Regex.Replace(phone, "\\D", "");
    }

    public async Task<WhatsAppStatusDto> GetStatusAsync()
    {
        try
        {
            var resp = await _http.GetAsync("/whatsapp/status");
            resp.EnsureSuccessStatusCode();
            var dto = await resp.Content.ReadFromJsonAsync<WhatsAppStatusDto>();
            return dto ?? new WhatsAppStatusDto { Linked = false };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo obtener status de WhatsApp");
            return new WhatsAppStatusDto { Linked = false, Info = "Servicio no disponible" };
        }
    }

    public async Task StartLinkingAsync()
    {
        var resp = await _http.PostAsync("/whatsapp/link", null);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<byte[]?> GetQrScreenshotAsync()
    {
        try
        {
            var resp = await _http.GetAsync("/whatsapp/qr");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo obtener QR");
            return null;
        }
    }

    /// <summary>2026-06-23: Lista los chats del sidebar de WhatsApp Web del numero vinculado.
    /// Hace scraping en el container Playwright. Hasta `limit` chats ordenados como aparecen
    /// en la app (mas recientes arriba).</summary>
    public async Task<List<WhatsAppChatDto>> ListChatsAsync(int limit = 50)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/whatsapp/chats/list", new { limit });
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Playwright /whatsapp/chats/list devolvio {Status}", resp.StatusCode);
                return new List<WhatsAppChatDto>();
            }
            var wrap = await resp.Content.ReadFromJsonAsync<ChatsListResponse>();
            return wrap?.Chats ?? new List<WhatsAppChatDto>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo listar chats del WhatsApp");
            return new List<WhatsAppChatDto>();
        }
    }

    private class ChatsListResponse
    {
        public List<WhatsAppChatDto>? Chats { get; set; }
    }

    public async Task<bool> CheckLinkedAsync()
    {
        try
        {
            var resp = await _http.GetAsync("/whatsapp/check-linked");
            resp.EnsureSuccessStatusCode();
            var dto = await resp.Content.ReadFromJsonAsync<WhatsAppLinkedDto>();
            return dto?.Linked ?? false;
        }
        catch { return false; }
    }

    public async Task UnlinkAsync()
    {
        var resp = await _http.PostAsync("/whatsapp/unlink", null);
        resp.EnsureSuccessStatusCode();
    }

    public async Task CancelLinkAsync()
    {
        var resp = await _http.PostAsync("/whatsapp/cancel-link", null);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<WhatsAppSendResult> SendMessageAsync(string phone, string message)
    {
        var normalized = NormalizePhone(phone);
        if (normalized.Length < 8)
            return new WhatsAppSendResult { Phone = phone, Success = false, Message = "Numero invalido" };

        var results = await SendBulkAsync(
            new List<WhatsAppRecipient> { new() { Phone = normalized, Name = null } },
            message);
        if (results.Count > 0) return results[0];
        return new WhatsAppSendResult { Phone = phone, Success = false, Message = "Sin respuesta" };
    }

    public async Task<List<WhatsAppSendResult>> SendBulkAsync(
        List<WhatsAppRecipient> recipients, string message)
    {
        var body = new
        {
            recipients = recipients.Select(r => new
            {
                phone = NormalizePhone(r.Phone),
                name = r.Name,
                message = r.Message
            }),
            message
        };
        var resp = await _http.PostAsJsonAsync("/whatsapp/send-bulk", body);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            string errMsg = errBody;
            try
            {
                var doc = JsonDocument.Parse(errBody);
                if (doc.RootElement.TryGetProperty("error", out var e))
                    errMsg = e.GetString() ?? errBody;
            }
            catch { }
            // Devolver un resultado por destinatario con el error
            return recipients.Select(r => new WhatsAppSendResult
            {
                Phone = r.Phone, Name = r.Name, Success = false, Message = errMsg
            }).ToList();
        }
        var results = await resp.Content.ReadFromJsonAsync<List<WhatsAppSendResult>>();
        return results ?? new List<WhatsAppSendResult>();
    }

    public async Task<List<WhatsAppSendResult>> SendBulkPersonalizedAsync(
        List<WhatsAppRecipient> recipients)
    {
        return await SendBulkAsync(recipients, "");
    }

    /// <summary>
    /// Manda UN mensaje a UN destinatario con un PDF adjunto + caption opcional.
    /// Lo hace via el contenedor Playwright (endpoint /whatsapp/send-with-pdf).
    /// </summary>
    public async Task<WhatsAppSendResult> SendMessageWithPdfAsync(
        string phone, string? caption, byte[] pdfBytes, string filename)
    {
        var normalized = NormalizePhone(phone);
        if (normalized.Length < 8)
            return new WhatsAppSendResult { Phone = phone, Success = false, Message = "Numero invalido" };

        var body = new
        {
            phone = normalized,
            caption = caption ?? "",
            pdfBase64 = Convert.ToBase64String(pdfBytes),
            pdfFilename = filename ?? "comprobante.pdf"
        };
        try
        {
            var resp = await _http.PostAsJsonAsync("/whatsapp/send-with-pdf", body);
            var content = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                string errMsg = content;
                try
                {
                    var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("error", out var e)) errMsg = e.GetString() ?? content;
                }
                catch { }
                return new WhatsAppSendResult { Phone = phone, Success = false, Message = errMsg };
            }
            try
            {
                var doc = JsonDocument.Parse(content);
                var success = doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean();
                var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "";
                return new WhatsAppSendResult { Phone = phone, Success = success, Message = msg ?? "" };
            }
            catch
            {
                return new WhatsAppSendResult { Phone = phone, Success = true, Message = "Enviado" };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando WhatsApp con PDF");
            return new WhatsAppSendResult { Phone = phone, Success = false, Message = ex.Message };
        }
    }

    // --- HTML -> WhatsApp ---
    public static string BuildWhatsAppMessage(string htmlTemplate, Dictionary<string, string>? vars = null)
    {
        if (string.IsNullOrEmpty(htmlTemplate)) return "";
        var s = htmlTemplate;

        // <br>, </p>, </div> -> newline
        s = Regex.Replace(s, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "</p\\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "</div\\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<p[^>]*>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<div[^>]*>", "", RegexOptions.IgnoreCase);

        // <b>/<strong> -> *..*
        s = Regex.Replace(s, "<(b|strong)[^>]*>", "*", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "</(b|strong)\\s*>", "*", RegexOptions.IgnoreCase);

        // <i>/<em> -> _.._
        s = Regex.Replace(s, "<(i|em)[^>]*>", "_", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "</(i|em)\\s*>", "_", RegexOptions.IgnoreCase);

        // <a href="url">texto</a> -> texto (url)
        s = Regex.Replace(
            s,
            "<a[^>]*href=[\"']([^\"']+)[\"'][^>]*>(.*?)</a\\s*>",
            "$2 ($1)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // <img ...> -> eliminar
        s = Regex.Replace(s, "<img[^>]*>", "", RegexOptions.IgnoreCase);

        // Cualquier otro tag
        s = Regex.Replace(s, "<[^>]+>", "");

        // Entidades
        s = s.Replace("&nbsp;", " ")
             .Replace("&amp;", "&")
             .Replace("&lt;", "<")
             .Replace("&gt;", ">")
             .Replace("&quot;", "\"")
             .Replace("&#39;", "'");

        // Reemplazo de variables {{var}}
        if (vars != null)
        {
            foreach (var kv in vars)
            {
                s = s.Replace("{{" + kv.Key + "}}", kv.Value ?? "");
            }
        }

        // Normalizar saltos múltiples
        s = Regex.Replace(s, "\n{3,}", "\n\n").Trim();
        return s;
    }
}

public class WhatsAppStatusDto
{
    public bool Linked { get; set; }
    public bool IsLinking { get; set; }
    public string? Info { get; set; }
    /// <summary>2026-06-23: timestamp ISO del ultimo heartbeat exitoso del Playwright.
    /// Si esta vieja (>5min) sugiere que el container esta caido aunque "linked" diga true.</summary>
    public string? LastHeartbeatAt { get; set; }
    /// <summary>2026-06-23: timestamp ISO de la ultima vez que paso de linked=true a linked=false.
    /// Util para mostrar "Se desvinculo hace X" en la UI.</summary>
    public string? LastDisconnectedAt { get; set; }
}

public class WhatsAppLinkedDto
{
    public bool Linked { get; set; }
}

/// <summary>2026-06-23: Cada chat del sidebar de WhatsApp Web. No incluye el ID del chat
/// porque WhatsApp Web no lo expone publicamente — se identifica por el nombre que el dueño le tiene guardado.</summary>
public class WhatsAppChatDto
{
    /// <summary>Nombre que figura en la lista (contacto guardado o numero crudo si no lo tiene en agenda).</summary>
    public string Name { get; set; } = "";
    /// <summary>Texto preview del ultimo mensaje (puede tener emojis o "Foto", "Audio", etc.).</summary>
    public string LastMsg { get; set; } = "";
    /// <summary>Hora del ultimo mensaje tal como la muestra WhatsApp Web ("12:34", "ayer", "lun.", etc.).</summary>
    public string LastMsgAt { get; set; } = "";
    /// <summary>Cantidad de mensajes sin leer (badge verde). 0 si no hay.</summary>
    public int Unread { get; set; }
}

public class WhatsAppRecipient
{
    public string Phone { get; set; } = "";
    public string? Name { get; set; }
    public string? Message { get; set; }
}

public class WhatsAppSendResult
{
    public string Phone { get; set; } = "";
    public string? Name { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}
