using System.Text;
using System.Text.Json;

namespace Api.Services;

/// <summary>
/// Consulta la Routes API de Google Maps Platform para ordenar las paradas de un reparto
/// por el camino real que se maneja (calles, sentidos, etc.), en vez de la distancia en linea recta.
///
/// Usa "optimizeWaypointOrder": Google devuelve el mejor orden de las paradas intermedias en
/// UNA sola consulta por chofer (barato). Requiere GOOGLE_MAPS_API_KEY en el entorno y la
/// Routes API habilitada en el proyecto de Google Cloud.
/// </summary>
public class GoogleRoutesService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<GoogleRoutesService> _logger;

    // Limite de la API para optimizar el orden de waypoints en una sola llamada.
    public const int MaxWaypoints = 25;

    public GoogleRoutesService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<GoogleRoutesService> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    private string ApiKey => _config["GOOGLE_MAPS_API_KEY"] ?? Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY") ?? "";

    /// <summary>True si hay clave configurada (si no, el que llama debe usar su fallback).</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// Devuelve el orden optimo (por calles reales) de las paradas intermedias entre <paramref name="origin"/>
    /// y <paramref name="destination"/>. El resultado es un arreglo de indices sobre <paramref name="intermediates"/>
    /// (ej: [1,0,2] = primero la parada 1, despues la 0, despues la 2).
    /// Retorna null si no se puede resolver (sin clave, demasiadas paradas, o error de la API) para que
    /// el llamador use su calculo de respaldo.
    /// </summary>
    public async Task<int[]?> OptimizeWaypointOrderAsync(
        (double lat, double lng) origin,
        (double lat, double lng) destination,
        IReadOnlyList<(double lat, double lng)> intermediates,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) return null;
        if (intermediates.Count == 0) return Array.Empty<int>();
        if (intermediates.Count > MaxWaypoints) return null; // fuera del limite de la API

        var body = new
        {
            origin = new { location = new { latLng = new { latitude = origin.lat, longitude = origin.lng } } },
            destination = new { location = new { latLng = new { latitude = destination.lat, longitude = destination.lng } } },
            intermediates = intermediates
                .Select(p => new { location = new { latLng = new { latitude = p.lat, longitude = p.lng } } })
                .ToArray(),
            travelMode = "DRIVE",
            // TRAFFIC_AWARE = optimiza teniendo en cuenta el tránsito actual. departureTime debe ser FUTURO
            // (Google rechaza "ahora exacto"), por eso le sumamos unos minutos.
            routingPreference = "TRAFFIC_AWARE",
            departureTime = DateTimeOffset.UtcNow.AddMinutes(3).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            optimizeWaypointOrder = true
        };

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://routes.googleapis.com/directions/v2:computeRoutes");
            req.Headers.Add("X-Goog-Api-Key", ApiKey);
            req.Headers.Add("X-Goog-FieldMask", "routes.optimizedIntermediateWaypointIndex");
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Routes API optimize devolvio {Status}: {Body}", (int)resp.StatusCode, json);
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0)
                return null;
            if (!routes[0].TryGetProperty("optimizedIntermediateWaypointIndex", out var idxEl))
                return null;

            var order = new int[idxEl.GetArrayLength()];
            for (int i = 0; i < order.Length; i++) order[i] = idxEl[i].GetInt32();
            return order;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Routes API optimize fallo (se usara el orden de respaldo)");
            return null;
        }
    }
}
