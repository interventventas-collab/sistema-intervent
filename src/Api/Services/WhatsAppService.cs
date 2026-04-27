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
}

public class WhatsAppLinkedDto
{
    public bool Linked { get; set; }
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
