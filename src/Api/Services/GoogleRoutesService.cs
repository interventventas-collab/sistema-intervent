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

    public record RouteResult(int DurationSeconds, int DistanceMeters, string? EncodedPolyline);

    /// <summary>
    /// Calcula la ruta que pasa por los waypoints EN EL ORDEN DADO (no reordena) y devuelve el tiempo
    /// estimado (con tránsito), los metros y la línea codificada para dibujarla en el mapa.
    /// Retorna null si no hay clave o la API falla.
    /// </summary>
    public async Task<RouteResult?> ComputeRouteAsync(
        (double lat, double lng) origin,
        (double lat, double lng) destination,
        IReadOnlyList<(double lat, double lng)> waypoints,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) return null;
        if (waypoints.Count > MaxWaypoints) return null; // fuera del límite de la API

        var body = new
        {
            origin = new { location = new { latLng = new { latitude = origin.lat, longitude = origin.lng } } },
            destination = new { location = new { latLng = new { latitude = destination.lat, longitude = destination.lng } } },
            intermediates = waypoints
                .Select(p => new { location = new { latLng = new { latitude = p.lat, longitude = p.lng } } })
                .ToArray(),
            travelMode = "DRIVE",
            routingPreference = "TRAFFIC_AWARE",
            departureTime = DateTimeOffset.UtcNow.AddMinutes(3).ToString("yyyy-MM-ddTHH:mm:ssZ")
            // sin optimizeWaypointOrder: respetamos el orden que ya calculamos
        };

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://routes.googleapis.com/directions/v2:computeRoutes");
            req.Headers.Add("X-Goog-Api-Key", ApiKey);
            req.Headers.Add("X-Goog-FieldMask", "routes.duration,routes.distanceMeters,routes.polyline.encodedPolyline");
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Routes API compute devolvio {Status}: {Body}", (int)resp.StatusCode, json);
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0)
                return null;
            var r0 = routes[0];

            int dur = 0;
            if (r0.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.String)
            {
                var s = durEl.GetString() ?? "0s";
                int.TryParse(s.TrimEnd('s'), out dur);
            }
            int dist = r0.TryGetProperty("distanceMeters", out var distEl) && distEl.ValueKind == JsonValueKind.Number
                ? distEl.GetInt32() : 0;
            string? poly = r0.TryGetProperty("polyline", out var pl) && pl.TryGetProperty("encodedPolyline", out var enc)
                ? enc.GetString() : null;

            return new RouteResult(dur, dist, poly);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Routes API compute falló");
            return null;
        }
    }

    public record RouteLeg(int DurationSeconds, int DistanceMeters, string? EncodedPolyline = null);
    public record RouteFullResult(int DurationSeconds, int DistanceMeters, List<string> Segments, List<RouteLeg> Legs);

    /// <summary>
    /// Calcula la ruta COMPLETA que pasa por todos los puntos EN EL ORDEN DADO (no reordena), sin el límite
    /// de 25 paradas: parte el recorrido en tramos de a lo sumo 25 paradas intermedias, los consulta uno por
    /// uno y los pega. Devuelve el tiempo total (con tránsito), los metros totales, la lista de líneas
    /// codificadas (una por tramo, se dibujan pegadas) y el tiempo/metros de cada segmento entre parada y parada.
    /// El primer punto de <paramref name="pointsInOrder"/> es el arranque y el último es la parada final.
    /// Retorna null si no hay clave o algún tramo falla (para que el llamador use su respaldo).
    /// </summary>
    public async Task<RouteFullResult?> ComputeRouteFullAsync(
        IReadOnlyList<(double lat, double lng)> pointsInOrder,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) return null;
        if (pointsInOrder.Count < 2) return null;

        // Cada consulta admite origen + 25 intermedias + destino = 27 puntos. Los tramos comparten el punto
        // de unión (el último de un tramo es el primero del siguiente) para que la línea quede continua y los
        // tiempos por tramo no se dupliquen.
        const int maxPorConsulta = 27;
        int totalDur = 0, totalDist = 0;
        var segments = new List<string>();
        var legs = new List<RouteLeg>();

        int start = 0;
        while (start < pointsInOrder.Count - 1)
        {
            int end = Math.Min(start + maxPorConsulta - 1, pointsInOrder.Count - 1); // índice inclusivo del último punto del tramo
            var origin = pointsInOrder[start];
            var dest = pointsInOrder[end];
            var inter = new List<(double lat, double lng)>();
            for (int i = start + 1; i < end; i++) inter.Add(pointsInOrder[i]);

            var chunk = await ComputeChunkAsync(origin, dest, inter, ct);
            if (chunk is null) return null; // si un tramo falla, no dibujamos a medias

            totalDur += chunk.Value.dur;
            totalDist += chunk.Value.dist;
            if (!string.IsNullOrEmpty(chunk.Value.poly)) segments.Add(chunk.Value.poly!);
            legs.AddRange(chunk.Value.legs);

            start = end; // el próximo tramo arranca donde terminó éste
        }

        if (segments.Count == 0) return null;
        return new RouteFullResult(totalDur, totalDist, segments, legs);
    }

    /// <summary>Una consulta a la Routes API para un tramo (≤27 puntos). Devuelve tiempo/metros totales del tramo,
    /// la línea codificada y el tiempo/metros de cada sub-segmento (leg) entre punto y punto.</summary>
    private async Task<(int dur, int dist, string? poly, List<RouteLeg> legs)?> ComputeChunkAsync(
        (double lat, double lng) origin,
        (double lat, double lng) destination,
        IReadOnlyList<(double lat, double lng)> waypoints,
        CancellationToken ct)
    {
        var body = new
        {
            origin = new { location = new { latLng = new { latitude = origin.lat, longitude = origin.lng } } },
            destination = new { location = new { latLng = new { latitude = destination.lat, longitude = destination.lng } } },
            intermediates = waypoints
                .Select(p => new { location = new { latLng = new { latitude = p.lat, longitude = p.lng } } })
                .ToArray(),
            travelMode = "DRIVE",
            routingPreference = "TRAFFIC_AWARE",
            departureTime = DateTimeOffset.UtcNow.AddMinutes(3).ToString("yyyy-MM-ddTHH:mm:ssZ")
        };

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://routes.googleapis.com/directions/v2:computeRoutes");
            req.Headers.Add("X-Goog-Api-Key", ApiKey);
            req.Headers.Add("X-Goog-FieldMask", "routes.duration,routes.distanceMeters,routes.polyline.encodedPolyline,routes.legs.duration,routes.legs.distanceMeters,routes.legs.polyline.encodedPolyline");
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Routes API compute (tramo) devolvio {Status}: {Body}", (int)resp.StatusCode, json);
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0)
                return null;
            var r0 = routes[0];

            int SegundosDe(JsonElement el) { int.TryParse((el.GetString() ?? "0s").TrimEnd('s'), out var v); return v; }

            int dur = 0;
            if (r0.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.String) dur = SegundosDe(durEl);
            int dist = r0.TryGetProperty("distanceMeters", out var distEl) && distEl.ValueKind == JsonValueKind.Number
                ? distEl.GetInt32() : 0;
            string? poly = r0.TryGetProperty("polyline", out var pl) && pl.TryGetProperty("encodedPolyline", out var enc)
                ? enc.GetString() : null;

            var legs = new List<RouteLeg>();
            if (r0.TryGetProperty("legs", out var legsEl) && legsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var leg in legsEl.EnumerateArray())
                {
                    int ld = leg.TryGetProperty("duration", out var lde) && lde.ValueKind == JsonValueKind.String ? SegundosDe(lde) : 0;
                    int lm = leg.TryGetProperty("distanceMeters", out var lme) && lme.ValueKind == JsonValueKind.Number ? lme.GetInt32() : 0;
                    string? lpoly = leg.TryGetProperty("polyline", out var lpl) && lpl.TryGetProperty("encodedPolyline", out var lenc)
                        ? lenc.GetString() : null;
                    legs.Add(new RouteLeg(ld, lm, lpoly));
                }
            }

            return (dur, dist, poly, legs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Routes API compute (tramo) falló");
            return null;
        }
    }
}
