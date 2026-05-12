using System.Text.RegularExpressions;

namespace Api.Services;

/// <summary>
/// Resuelve enlaces cortos de Google Maps (https://maps.app.goo.gl/...) y extrae
/// las coordenadas (lat, lng) de la URL larga a la que redirigen.
///
/// Sirve para tener los clientes "mapeados" con sus coordenadas reales sin tener
/// que pagar la API de Geocoding de Google ni usar Google Maps en el frontend.
/// El mapa sigue siendo Leaflet + OpenStreetMap (gratis), solo aprovechamos los
/// links que el operador comparte desde su celu.
/// </summary>
public class GoogleMapsLinkResolverService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GoogleMapsLinkResolverService> _logger;

    public GoogleMapsLinkResolverService(IHttpClientFactory httpFactory, ILogger<GoogleMapsLinkResolverService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>Sigue el redirect del link corto y extrae lat/lng. Devuelve null si no pudo.</summary>
    public async Task<(decimal lat, decimal lng)?> TryResolverCoordenadasAsync(string? linkOriginal)
    {
        if (string.IsNullOrWhiteSpace(linkOriginal)) return null;
        var link = linkOriginal.Trim();
        if (!link.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            // Si ya es una URL larga de Google Maps (no un link corto), intentamos extraer directo.
            var directExtract = ExtraerCoordsDeUrl(link);
            if (directExtract.HasValue) return directExtract;

            // Si es link corto (maps.app.goo.gl, goo.gl/maps), hacemos request y seguimos el redirect.
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(8);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");

            // Primer request: capturar el redirect 302 sin auto-seguirlo. Si no hay redirect, leer el body.
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var clientNoRedirect = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            clientNoRedirect.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");

            var resp = await clientNoRedirect.GetAsync(link);
            string? urlFinal = null;
            if ((int)resp.StatusCode >= 300 && (int)resp.StatusCode < 400)
            {
                urlFinal = resp.Headers.Location?.ToString();
            }

            // Si no hubo redirect 30x, leemos el body y buscamos coords en el HTML
            if (string.IsNullOrEmpty(urlFinal))
            {
                var bodyResp = await http.GetAsync(link);
                if (bodyResp.IsSuccessStatusCode)
                {
                    var html = await bodyResp.Content.ReadAsStringAsync();
                    // Buscar coords en el HTML directamente
                    var fromBody = ExtraerCoordsDeUrl(html);
                    if (fromBody.HasValue) return fromBody;
                    return null;
                }
            }

            // Tenemos la URL final del redirect, extraemos las coords
            return ExtraerCoordsDeUrl(urlFinal!);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo resolver el link de Google Maps: {Link}", linkOriginal);
            return null;
        }
    }

    /// <summary>
    /// Extrae lat/lng de una URL de Google Maps. Los formatos típicos:
    ///   .../@-34.6037,-58.3816,17z/...
    ///   .../?q=-34.6037,-58.3816...
    ///   .../!3d-34.6037!4d-58.3816...
    /// </summary>
    private static (decimal lat, decimal lng)? ExtraerCoordsDeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        // Patrón 1: @lat,lng (formato URL principal de Google Maps)
        var m = Regex.Match(url, @"@(-?\d+\.\d+),(-?\d+\.\d+)");
        if (m.Success) return ParseTuple(m.Groups[1].Value, m.Groups[2].Value);

        // Patrón 2: !3dLAT!4dLNG (formato Google Maps embedded)
        m = Regex.Match(url, @"!3d(-?\d+\.\d+)!4d(-?\d+\.\d+)");
        if (m.Success) return ParseTuple(m.Groups[1].Value, m.Groups[2].Value);

        // Patrón 3: ?q=LAT,LNG (formato directions API o share simple)
        m = Regex.Match(url, @"[?&]q=(-?\d+\.\d+),(-?\d+\.\d+)");
        if (m.Success) return ParseTuple(m.Groups[1].Value, m.Groups[2].Value);

        // Patrón 4: ll=LAT,LNG (formato Google Maps embed)
        m = Regex.Match(url, @"[?&]ll=(-?\d+\.\d+),(-?\d+\.\d+)");
        if (m.Success) return ParseTuple(m.Groups[1].Value, m.Groups[2].Value);

        // Patrón 5: center=LAT,LNG
        m = Regex.Match(url, @"[?&]center=(-?\d+\.\d+),(-?\d+\.\d+)");
        if (m.Success) return ParseTuple(m.Groups[1].Value, m.Groups[2].Value);

        return null;
    }

    private static (decimal, decimal)? ParseTuple(string latStr, string lngStr)
    {
        if (decimal.TryParse(latStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var lat) &&
            decimal.TryParse(lngStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var lng) &&
            Math.Abs(lat) <= 90m && Math.Abs(lng) <= 180m)
        {
            return (lat, lng);
        }
        return null;
    }
}
