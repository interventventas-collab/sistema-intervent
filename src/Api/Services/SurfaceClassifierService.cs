using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Deduce el TIPO DE CALLE (asfalto / tierra / empedrado) de un domicilio mirando la foto de
/// Street View con IA (Claude Haiku vision). El resultado se cachea por punto en la tabla
/// MapeoSurfaceCache, así se calcula UNA sola vez por domicilio y no se vuelve a pagar.
///
/// Flujo: metadata de Street View (¿hay foto?) → imagen estática apuntando a la calzada
/// (fov ancho + pitch hacia abajo) → IA clasifica → se guarda.
///
/// La imagen la baja el SERVIDOR con la clave GOOGLE_MAPS_API_KEY (tiene Street View Static
/// habilitado y no está restringida por dominio, así que sirve desde acá).
/// </summary>
public class SurfaceClassifierService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly IntegrationService _integrations;
    private readonly ILogger<SurfaceClassifierService> _logger;

    private const string MODEL = "claude-haiku-4-5-20251001";
    private const string ANTHROPIC_URL = "https://api.anthropic.com/v1/messages";

    public SurfaceClassifierService(AppDbContext db, IHttpClientFactory httpFactory,
        IConfiguration config, IntegrationService integrations, ILogger<SurfaceClassifierService> logger)
    {
        _db = db; _httpFactory = httpFactory; _config = config; _integrations = integrations; _logger = logger;
    }

    private string GoogleKey =>
        _config["GOOGLE_MAPS_API_KEY"] ?? Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY") ?? "";

    public record SurfaceResult(string Tipo, string? Conf);

    /// <summary>Devuelve el tipo de calle del punto (de caché si ya existe, si no lo calcula y guarda).</summary>
    public async Task<SurfaceResult> ClassifyAsync(decimal lat, decimal lng)
    {
        var pointKey = $"{lat.ToString("0.#####", CultureInfo.InvariantCulture)},{lng.ToString("0.#####", CultureInfo.InvariantCulture)}";

        var cached = await _db.MapeoSurfaceCache.AsNoTracking().FirstOrDefaultAsync(x => x.PointKey == pointKey);
        if (cached != null) return new SurfaceResult(cached.Tipo, cached.Confianza);

        var result = await ComputeAsync(lat, lng);

        // Guardar en caché. Si dos globitos se abren a la vez, uno puede chocar con el índice
        // único; lo ignoramos (el otro ya lo guardó).
        try
        {
            _db.MapeoSurfaceCache.Add(new MapeoSurfaceCache { PointKey = pointKey, Tipo = result.Tipo, Confianza = result.Conf });
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo cachear el tipo de calle para {Key}", pointKey);
        }
        return result;
    }

    private async Task<SurfaceResult> ComputeAsync(decimal lat, decimal lng)
    {
        var gkey = GoogleKey;
        if (string.IsNullOrEmpty(gkey)) return new("no_seguro", null);

        var loc = $"{lat.ToString(CultureInfo.InvariantCulture)},{lng.ToString(CultureInfo.InvariantCulture)}";
        var http = _httpFactory.CreateClient();

        // 1. ¿Hay Street View en ese punto?
        try
        {
            var metaUrl = $"https://maps.googleapis.com/maps/api/streetview/metadata?location={Uri.EscapeDataString(loc)}&source=outdoor&key={gkey}";
            var metaResp = await http.GetStringAsync(metaUrl);
            if (metaResp.IndexOf("\"OK\"", StringComparison.Ordinal) < 0 ||
                metaResp.IndexOf("\"status\"", StringComparison.Ordinal) < 0)
                return new("sin_foto", null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Street View metadata falló para {Loc}", loc);
            return new("no_seguro", null);
        }

        // 2. Foto apuntando a la CALZADA: fov ancho + pitch hacia abajo capta el piso de la calle
        //    en el primer plano, aunque el encuadre por defecto mire a la fachada.
        byte[] imgBytes;
        try
        {
            var imgUrl = $"https://maps.googleapis.com/maps/api/streetview?size=480x300&location={Uri.EscapeDataString(loc)}&fov=100&pitch=-15&source=outdoor&key={gkey}";
            imgBytes = await http.GetByteArrayAsync(imgUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Street View imagen falló para {Loc}", loc);
            return new("no_seguro", null);
        }
        if (imgBytes.Length < 3000) return new("no_seguro", null); // gris "sin imagen"
        var b64 = Convert.ToBase64String(imgBytes);

        // 3. Clave de la IA (integraciones o env).
        var apiKey = await _integrations.GetSecretAsync("anthropic");
        if (string.IsNullOrEmpty(apiKey)) apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrEmpty(apiKey)) return new("no_seguro", null);

        // 4. La IA mira la foto y clasifica la calle.
        var body = new JsonObject
        {
            ["model"] = MODEL,
            ["max_tokens"] = 60,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = "image/jpeg",
                                ["data"] = b64
                            }
                        },
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "Mirá la CALZADA (el piso por donde pasan los autos, no la vereda ni el techo) en esta foto. " +
                                       "Respondé SOLO un JSON compacto, sin explicaciones: " +
                                       "{\"tipo\":\"asfalto|tierra|empedrado|no_seguro\",\"conf\":\"alta|media|baja\"}. " +
                                       "Si la calle no se ve o no estás seguro, poné no_seguro."
                        }
                    }
                }
            }
        };

        try
        {
            var httpAi = _httpFactory.CreateClient();
            httpAi.DefaultRequestHeaders.Add("x-api-key", apiKey);
            httpAi.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await httpAi.PostAsync(ANTHROPIC_URL, content);
            var respBody = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Anthropic (surface) respondió {Status}: {Body}", resp.StatusCode,
                    respBody.Length > 300 ? respBody.Substring(0, 300) : respBody);
                return new("no_seguro", null);
            }
            var text = ExtractText(respBody);
            return ParseSurface(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Consulta de tipo de calle a la IA falló para {Loc}", loc);
            return new("no_seguro", null);
        }
    }

    /// <summary>Saca el texto del primer bloque de la respuesta de Anthropic.</summary>
    private static string ExtractText(string respBody)
    {
        try
        {
            var doc = JsonNode.Parse(respBody)?.AsObject();
            var blocks = doc?["content"]?.AsArray();
            if (blocks == null) return "";
            foreach (var b in blocks)
            {
                if (b?["type"]?.GetValue<string>() == "text")
                    return b["text"]?.GetValue<string>() ?? "";
            }
        }
        catch { }
        return "";
    }

    private static readonly string[] Tipos = { "asfalto", "tierra", "empedrado", "no_seguro" };
    private static readonly string[] Confs = { "alta", "media", "baja" };

    /// <summary>Extrae {tipo, conf} del texto de la IA (aunque venga con ```json o texto alrededor).</summary>
    private static SurfaceResult ParseSurface(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new("no_seguro", null);
        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        if (open >= 0 && close > open)
        {
            var json = text.Substring(open, close - open + 1);
            try
            {
                var o = JsonNode.Parse(json)?.AsObject();
                var tipo = o?["tipo"]?.GetValue<string>()?.Trim().ToLowerInvariant();
                var conf = o?["conf"]?.GetValue<string>()?.Trim().ToLowerInvariant();
                if (tipo != null && Array.IndexOf(Tipos, tipo) >= 0)
                    return new(tipo, (conf != null && Array.IndexOf(Confs, conf) >= 0) ? conf : null);
            }
            catch { }
        }
        // Fallback: buscar la palabra suelta.
        var low = text.ToLowerInvariant();
        foreach (var t in Tipos)
            if (low.Contains(t)) return new(t, null);
        return new("no_seguro", null);
    }
}
