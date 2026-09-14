using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 14/09/2026 — Lee con IA la foto o el PDF de un comprobante de pago que el cliente mandó por
/// WhatsApp y saca el importe y a quién se le pagó. Sirve para la cobranza que se carga desde el
/// chat: el importe entra ya puesto y, si la plata fue a un empleado o proveedor (cobro
/// redirigido), se sugiere a quién.
///
/// Es SOLO una sugerencia: el que carga la cobranza lo ve y lo corrige antes de guardar. Si la IA
/// no está configurada o no entiende la foto, devuelve vacío y la cobranza se carga a mano igual.
/// </summary>
public class ComprobantePagoLectorService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IntegrationService _integrations;
    private readonly ILogger<ComprobantePagoLectorService> _logger;

    private const string MODEL = "claude-opus-5";
    private const string ANTHROPIC_URL = "https://api.anthropic.com/v1/messages";

    /// <summary>Mismo directorio donde el webhook guarda lo que manda el cliente.</summary>
    public const string UploadsDir = "/data/whatsapp-uploads";

    public ComprobantePagoLectorService(AppDbContext db, IHttpClientFactory httpFactory,
        IntegrationService integrations, ILogger<ComprobantePagoLectorService> logger)
    {
        _db = db; _httpFactory = httpFactory; _integrations = integrations; _logger = logger;
    }

    public record Lectura(bool EsPago, decimal? Importe, string? Fecha, string? PagadoA,
        string? Referencia, int? EmpleadoId, int? ProveedorId);

    private static readonly Lectura Vacia = new(false, null, null, null, null, null, null);

    /// <summary>
    /// Busca el archivo del chat a partir de su URL (".../api/whatsapp/twilio/files/{token}.jpg").
    /// Sólo se acepta un token de la tabla de adjuntos: nunca se arma una ruta con lo que manda el
    /// navegador.
    /// </summary>
    public async Task<(WhatsAppTwilioUpload? up, string? path)> ResolverAdjuntoAsync(string? mediaUrl)
    {
        if (string.IsNullOrWhiteSpace(mediaUrl)) return (null, null);
        var i = mediaUrl.IndexOf("/files/", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return (null, null);
        var seg = mediaUrl[(i + "/files/".Length)..].Split('?', '#')[0];
        var token = Path.GetFileNameWithoutExtension(seg);
        if (string.IsNullOrWhiteSpace(token)) return (null, null);

        var up = await _db.WhatsAppTwilioUploads.AsNoTracking().FirstOrDefaultAsync(u => u.Token == token);
        if (up is null) return (null, null);
        var path = Path.Combine(UploadsDir, Path.GetFileName(up.StoredFilename));
        return System.IO.File.Exists(path) ? (up, path) : (up, null);
    }

    public async Task<Lectura> LeerAsync(string? mediaUrl)
    {
        var (up, path) = await ResolverAdjuntoAsync(mediaUrl);
        if (up is null || path is null) return Vacia;

        var mime = (up.ContentType ?? "").ToLowerInvariant();
        var ext = Path.GetExtension(up.StoredFilename).ToLowerInvariant();
        if (mime is "" or "application/octet-stream")
            mime = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp",
                ".pdf" => "application/pdf", _ => mime
            };
        var esPdf = mime == "application/pdf";
        if (!esPdf && mime is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif")) return Vacia;

        var bytes = await System.IO.File.ReadAllBytesAsync(path);
        if (bytes.Length == 0 || bytes.Length > 10L * 1024 * 1024) return Vacia;

        var apiKey = await _integrations.GetSecretAsync("anthropic");
        if (string.IsNullOrEmpty(apiKey)) apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrEmpty(apiKey)) return Vacia;

        // A quién se le puede haber pagado: los mismos que ofrece la cobranza redirigida.
        var empleados = await _db.NomEmpleados.Where(e => e.IsActive)
            .Select(e => new { e.Id, e.Nombre }).ToListAsync();
        var proveedores = await _db.CafeProveedores.Where(p => p.IsActive && p.AceptaRedirigido)
            .Select(p => new { p.Id, p.Nombre }).ToListAsync();
        var candidatos = new StringBuilder();
        foreach (var e in empleados) candidatos.AppendLine($"E{e.Id}: {e.Nombre} (empleado)");
        foreach (var p in proveedores)
            candidatos.AppendLine($"P{p.Id}: {p.Nombre} (proveedor)");

        var prompt =
            "Esta imagen o PDF la mandó un cliente por WhatsApp a un negocio de Argentina. " +
            "Decí si es un comprobante de un pago ya hecho (transferencia, depósito, Mercado Pago, recibo de pago). " +
            "Una factura, un presupuesto o una lista de precios NO son un pago.\n" +
            "Si es un pago, sacá: el importe pagado (número, sin signo $; en Argentina el punto separa miles y la coma decimales), " +
            "la fecha (dd/mm/aaaa), el nombre de quien RECIBIÓ la plata (destinatario, no el que pagó) " +
            "y el número de operación o comprobante.\n" +
            "Estas son las personas y empresas a las que el negocio a veces les hace llegar cobros. " +
            "Si el destinatario es claramente una de ellas, poné su código (por ejemplo E3 o P12); si no, dejalo vacío:\n" +
            candidatos +
            "Si un dato no se lee con claridad, dejalo vacío en vez de adivinar.";

        var archivo = new JsonObject
        {
            ["type"] = esPdf ? "document" : "image",
            ["source"] = new JsonObject
            {
                ["type"] = "base64",
                ["media_type"] = mime,
                ["data"] = Convert.ToBase64String(bytes)
            }
        };

        JsonObject Prop(string type) => new() { ["type"] = type };
        var body = new JsonObject
        {
            ["model"] = MODEL,
            ["max_tokens"] = 4000,
            ["fallbacks"] = "default",
            ["output_config"] = new JsonObject
            {
                ["effort"] = "low",
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["es_pago"] = Prop("boolean"),
                            ["importe"] = Prop("string"),
                            ["fecha"] = Prop("string"),
                            ["pagado_a"] = Prop("string"),
                            ["referencia"] = Prop("string"),
                            ["codigo_destinatario"] = Prop("string")
                        },
                        ["required"] = new JsonArray("es_pago", "importe", "fecha", "pagado_a", "referencia", "codigo_destinatario"),
                        ["additionalProperties"] = false
                    }
                }
            },
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray { archivo, new JsonObject { ["type"] = "text", ["text"] = prompt } }
                }
            }
        };

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(60);
            http.DefaultRequestHeaders.Add("x-api-key", apiKey);
            http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            http.DefaultRequestHeaders.Add("anthropic-beta", "server-side-fallback-2026-07-01");
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(ANTHROPIC_URL, content);
            var respBody = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Anthropic (comprobante de pago) respondió {Status}: {Body}", resp.StatusCode,
                    respBody.Length > 300 ? respBody[..300] : respBody);
                return Vacia;
            }

            var doc = JsonNode.Parse(respBody)?.AsObject();
            if (doc?["stop_reason"]?.GetValue<string>() is "refusal") return Vacia;
            var text = doc?["content"]?.AsArray()
                .FirstOrDefault(b => b?["type"]?.GetValue<string>() == "text")?["text"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(text)) return Vacia;

            var o = JsonNode.Parse(text)?.AsObject();
            if (o is null || o["es_pago"]?.GetValue<bool>() != true) return Vacia;

            string? Txt(string k) { var v = o[k]?.GetValue<string>()?.Trim(); return string.IsNullOrEmpty(v) ? null : v; }

            int? empId = null, provId = null;
            var cod = Txt("codigo_destinatario")?.ToUpperInvariant();
            if (cod is { Length: > 1 } && int.TryParse(cod[1..], out var cid))
            {
                // Sólo si el código es uno de los que le pasamos: la IA no inventa destinatarios.
                if (cod[0] == 'E' && empleados.Any(e => e.Id == cid)) empId = cid;
                if (cod[0] == 'P' && proveedores.Any(p => p.Id == cid)) provId = cid;
            }

            return new Lectura(true, ParseImporte(Txt("importe")), Txt("fecha"), Txt("pagado_a"),
                Txt("referencia"), empId, provId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lectura con IA del comprobante de pago falló ({Token})", up.Token);
            return Vacia;
        }
    }

    /// <summary>"$ 250.000,50" / "250000.50" / "250.000" → 250000.50. Null si no se entiende.</summary>
    public static decimal? ParseImporte(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = new string(s.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray());
        if (t.Length == 0) return null;
        var lastDot = t.LastIndexOf('.');
        var lastComma = t.LastIndexOf(',');
        if (lastComma > lastDot)
            t = t.Replace(".", "").Replace(',', '.');           // 250.000,50
        else if (lastDot >= 0 && lastComma >= 0)
            t = t.Replace(",", "");                               // 250,000.50
        else if (lastDot >= 0 && t.Length - lastDot - 1 == 3)
            t = t.Replace(".", "");                               // 250.000 (miles)
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
    }
}
