using System.Net;
using System.Text;
using System.Text.Json;
using HtmlAgilityPack;

namespace Api.Services;

/// <summary>
/// 2026-06-11: scrapea la pagina del proveedor del OEM para extraer
/// imagen miniatura, descripcion y ficha tecnica.
/// Hoy soporta colombraro.com.ar. Se puede extender a otros dominios.
/// </summary>
public class OemWebScrapingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OemWebScrapingService> _logger;

    public OemWebScrapingService(IHttpClientFactory httpClientFactory, ILogger<OemWebScrapingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public record ScrapeResult(
        string? ImagenUrl,
        string? Descripcion,
        Dictionary<string, string>? Especificaciones,
        string? Error
    );

    public async Task<ScrapeResult> ScrapeAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new ScrapeResult(null, null, null, "URL vacia");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new ScrapeResult(null, null, null, "URL invalida");

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");

            var html = await client.GetStringAsync(uri, ct);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Detectar dominio y parsear con el handler adecuado
            var host = uri.Host.ToLowerInvariant().Replace("www.", "");

            return host switch
            {
                "colombraro.com.ar" => ParseColombraro(doc),
                _ => ParseGeneric(doc) // fallback con og:image + og:description
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Error scrapeando URL {Url}", url);
            return new ScrapeResult(null, null, null, $"No se pudo acceder a la URL: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado scrapeando URL {Url}", url);
            return new ScrapeResult(null, null, null, $"Error: {ex.Message}");
        }
    }

    /// <summary>Parser especifico para colombraro.com.ar (WooCommerce).</summary>
    private ScrapeResult ParseColombraro(HtmlDocument doc)
    {
        // Imagen principal del producto WooCommerce
        var imgNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'woocommerce-product-gallery')]//img[contains(@class,'wp-post-image')]")
                   ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
        string? imagenUrl = null;
        if (imgNode != null)
        {
            imagenUrl = imgNode.GetAttributeValue("data-large_image", null)
                     ?? imgNode.GetAttributeValue("src", null)
                     ?? imgNode.GetAttributeValue("content", null);
        }

        // Descripcion corta (debajo del titulo) - WooCommerce la pone en div.woocommerce-product-details__short-description
        var descNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'woocommerce-product-details__short-description')]")
                    ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:description']");
        string? descripcion = null;
        if (descNode != null)
        {
            descripcion = descNode.Name == "meta"
                ? descNode.GetAttributeValue("content", null)
                : descNode.InnerText;
            descripcion = WebUtility.HtmlDecode(descripcion?.Trim());
            if (!string.IsNullOrEmpty(descripcion) && descripcion.Length > 1900) descripcion = descripcion[..1900];
        }

        // Tabla de "Información adicional" - WooCommerce: <table class="woocommerce-product-attributes">
        var especificaciones = new Dictionary<string, string>();
        var attrRows = doc.DocumentNode.SelectNodes("//table[contains(@class,'woocommerce-product-attributes')]//tr");
        if (attrRows != null)
        {
            foreach (var tr in attrRows)
            {
                var th = tr.SelectSingleNode(".//th");
                var td = tr.SelectSingleNode(".//td");
                if (th != null && td != null)
                {
                    var key = WebUtility.HtmlDecode(th.InnerText?.Trim() ?? "");
                    var val = WebUtility.HtmlDecode(td.InnerText?.Trim() ?? "");
                    if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(val) && !especificaciones.ContainsKey(key))
                        especificaciones[key] = val;
                }
            }
        }

        return new ScrapeResult(imagenUrl, descripcion, especificaciones.Count > 0 ? especificaciones : null, null);
    }

    /// <summary>Fallback generico: usa Open Graph tags.</summary>
    private ScrapeResult ParseGeneric(HtmlDocument doc)
    {
        var ogImage = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']")?.GetAttributeValue("content", null);
        var ogDesc = doc.DocumentNode.SelectSingleNode("//meta[@property='og:description']")?.GetAttributeValue("content", null);
        ogDesc = WebUtility.HtmlDecode(ogDesc);
        return new ScrapeResult(ogImage, ogDesc, null, null);
    }

    public string? SerializeEspecificaciones(Dictionary<string, string>? especificaciones)
    {
        if (especificaciones is null || especificaciones.Count == 0) return null;
        return JsonSerializer.Serialize(especificaciones, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }
}

/// <summary>
/// 2026-06-11: estado compartido del job masivo de scraping. Permite saber desde la UI cuanto falta.
/// Solo permitimos UN job corriendo a la vez.
/// </summary>
public class OemMassiveScrapeState
{
    private readonly object _lock = new();
    public bool Running { get; private set; }
    public int Total { get; private set; }
    public int Procesados { get; private set; }
    public int Exitosos { get; private set; }
    public int Errores { get; private set; }
    public string? CurrentCodigo { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? FinishedAt { get; private set; }
    public string? LastError { get; private set; }

    public bool TryStart(int total)
    {
        lock (_lock)
        {
            if (Running) return false;
            Running = true;
            Total = total;
            Procesados = 0;
            Exitosos = 0;
            Errores = 0;
            CurrentCodigo = null;
            StartedAt = DateTime.UtcNow;
            FinishedAt = null;
            LastError = null;
            return true;
        }
    }

    public void Tick(string codigo, bool ok, string? err = null)
    {
        lock (_lock)
        {
            Procesados++;
            CurrentCodigo = codigo;
            if (ok) Exitosos++; else { Errores++; LastError = err; }
        }
    }

    public void Finish()
    {
        lock (_lock)
        {
            Running = false;
            FinishedAt = DateTime.UtcNow;
            CurrentCodigo = null;
        }
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                running = Running,
                total = Total,
                procesados = Procesados,
                exitosos = Exitosos,
                errores = Errores,
                currentCodigo = CurrentCodigo,
                startedAt = StartedAt,
                finishedAt = FinishedAt,
                lastError = LastError,
                porcentaje = Total > 0 ? Math.Round((decimal)Procesados / Total * 100m, 1) : 0m
            };
        }
    }
}
