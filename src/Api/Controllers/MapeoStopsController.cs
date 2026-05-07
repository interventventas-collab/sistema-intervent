using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/mapeo/stops")]
[Authorize]
public class MapeoStopsController : ControllerBase
{
    private readonly AppDbContext _db;
    public MapeoStopsController(AppDbContext db) { _db = db; }

    public record StopDto(int Id, string Origin, string? OriginRefId, string? Alias, string Direccion,
        decimal Latitude, decimal Longitude, string? ContactName, string? Telefono, string? Notas,
        string InternalStatus, int? AssignedDriverId, string? AssignedDriverName, string? AssignedDriverColor,
        int? OrderInRoute, DateTime CreatedAt);

    private static StopDto Map(MapeoStop s) => new(
        s.Id, s.Origin, s.OriginRefId, s.Alias, s.Direccion, s.Latitude, s.Longitude,
        s.ContactName, s.Telefono, s.Notas, s.InternalStatus,
        s.AssignedDriverId, s.AssignedDriver?.Nombre, s.AssignedDriver?.Color,
        s.OrderInRoute, s.CreatedAt);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? driverId = null, [FromQuery] string? internalStatus = null)
    {
        var q = _db.MapeoStops.Include(s => s.AssignedDriver).AsQueryable();
        if (driverId.HasValue) q = q.Where(s => s.AssignedDriverId == driverId.Value);
        if (!string.IsNullOrWhiteSpace(internalStatus)) q = q.Where(s => s.InternalStatus == internalStatus);
        var list = await q.OrderBy(s => s.AssignedDriverId).ThenBy(s => s.OrderInRoute ?? int.MaxValue).ThenBy(s => s.Id).ToListAsync();
        return Ok(list.Select(Map));
    }

    public record CreateStopRequest(string Origin, string? OriginRefId, string? Alias, string Direccion,
        decimal Latitude, decimal Longitude, string? ContactName, string? Telefono, string? Notas);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateStopRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Direccion)) return BadRequest(new { error = "Dirección obligatoria" });
        // Si ya existe una parada con mismo origin+ref (ej: 2 veces el mismo favorito), permitimos duplicar — el usuario sabrá.
        var s = new MapeoStop
        {
            Origin = string.IsNullOrWhiteSpace(req.Origin) ? "manual" : req.Origin.ToLower(),
            OriginRefId = req.OriginRefId,
            Alias = string.IsNullOrWhiteSpace(req.Alias) ? null : req.Alias.Trim(),
            Direccion = req.Direccion.Trim(),
            Latitude = req.Latitude,
            Longitude = req.Longitude,
            ContactName = string.IsNullOrWhiteSpace(req.ContactName) ? null : req.ContactName.Trim(),
            Telefono = string.IsNullOrWhiteSpace(req.Telefono) ? null : req.Telefono.Trim(),
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            InternalStatus = "pending",
            CreatedAt = DateTime.UtcNow
        };
        _db.MapeoStops.Add(s);
        await _db.SaveChangesAsync();
        await _db.Entry(s).Reference(x => x.AssignedDriver).LoadAsync();
        return Ok(Map(s));
    }

    public record UpdateStopRequest(string? Alias, string? ContactName, string? Telefono, string? Notas,
        string? InternalStatus, int? AssignedDriverId, int? OrderInRoute);

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateStopRequest req)
    {
        var s = await _db.MapeoStops.Include(x => x.AssignedDriver).FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound(new { error = "Parada no encontrada" });
        if (req.Alias is not null) s.Alias = string.IsNullOrWhiteSpace(req.Alias) ? null : req.Alias.Trim();
        if (req.ContactName is not null) s.ContactName = string.IsNullOrWhiteSpace(req.ContactName) ? null : req.ContactName.Trim();
        if (req.Telefono is not null) s.Telefono = string.IsNullOrWhiteSpace(req.Telefono) ? null : req.Telefono.Trim();
        if (req.Notas is not null) s.Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim();
        if (req.InternalStatus is not null) s.InternalStatus = req.InternalStatus.Trim().ToLower();
        if (req.AssignedDriverId.HasValue)
        {
            s.AssignedDriverId = req.AssignedDriverId.Value > 0 ? req.AssignedDriverId.Value : null;
            await _db.Entry(s).Reference(x => x.AssignedDriver).LoadAsync();
        }
        if (req.OrderInRoute.HasValue) s.OrderInRoute = req.OrderInRoute.Value > 0 ? req.OrderInRoute.Value : null;
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(s));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var s = await _db.MapeoStops.FindAsync(id);
        if (s is null) return NotFound(new { error = "Parada no encontrada" });
        _db.MapeoStops.Remove(s);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpDelete]
    public async Task<IActionResult> ClearAll()
    {
        await _db.MapeoStops.ExecuteDeleteAsync();
        return Ok(new { ok = true });
    }

    public record AssignBulkRequest(List<int> StopIds, int? DriverId);

    /// <summary>Asigna varios stops al mismo driver (o desasigna si DriverId es null/0).</summary>
    [HttpPost("assign-bulk")]
    public async Task<IActionResult> AssignBulk([FromBody] AssignBulkRequest req)
    {
        if (req.StopIds is null || req.StopIds.Count == 0) return BadRequest(new { error = "Sin stops" });
        var ids = req.StopIds;
        int? did = req.DriverId.HasValue && req.DriverId.Value > 0 ? req.DriverId.Value : null;
        await _db.MapeoStops.Where(s => ids.Contains(s.Id))
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.AssignedDriverId, did)
                .SetProperty(s => s.UpdatedAt, DateTime.UtcNow));
        return Ok(new { updated = ids.Count });
    }

    /// <summary>
    /// Reparte automaticamente todos los stops sin driver entre los drivers activos via k-means
    /// usando la distancia geografica (haversine simplificado).
    /// </summary>
    [HttpPost("auto-assign")]
    public async Task<IActionResult> AutoAssign([FromQuery] bool reassignAll = false)
    {
        var drivers = await _db.MapeoDrivers.Where(d => d.IsActive).OrderBy(d => d.Id).ToListAsync();
        if (drivers.Count == 0) return BadRequest(new { error = "No hay drivers activos" });

        var stopsQ = _db.MapeoStops.AsQueryable();
        if (!reassignAll) stopsQ = stopsQ.Where(s => s.AssignedDriverId == null);
        var stops = await stopsQ.ToListAsync();
        if (stops.Count == 0) return Ok(new { assigned = 0 });

        // K-means simple. Centroides iniciales: tomamos N stops espaciados.
        int K = drivers.Count;
        var centroids = new List<(double lat, double lng)>();
        for (int i = 0; i < K; i++)
        {
            var idx = (int)Math.Floor((double)i * stops.Count / K);
            var s = stops[idx];
            centroids.Add(((double)s.Latitude, (double)s.Longitude));
        }
        var assignment = new int[stops.Count];
        for (int iter = 0; iter < 20; iter++)
        {
            // Asignar cada stop al centroide mas cercano
            for (int i = 0; i < stops.Count; i++)
            {
                double bestD = double.MaxValue; int bestC = 0;
                for (int c = 0; c < K; c++)
                {
                    var d = Hav((double)stops[i].Latitude, (double)stops[i].Longitude, centroids[c].lat, centroids[c].lng);
                    if (d < bestD) { bestD = d; bestC = c; }
                }
                assignment[i] = bestC;
            }
            // Recalcular centroides como promedio
            var newCentroids = new List<(double lat, double lng)>();
            bool moved = false;
            for (int c = 0; c < K; c++)
            {
                var members = Enumerable.Range(0, stops.Count).Where(i => assignment[i] == c).ToList();
                if (members.Count == 0) { newCentroids.Add(centroids[c]); continue; }
                var avgLat = members.Average(i => (double)stops[i].Latitude);
                var avgLng = members.Average(i => (double)stops[i].Longitude);
                if (Math.Abs(avgLat - centroids[c].lat) > 0.0001 || Math.Abs(avgLng - centroids[c].lng) > 0.0001) moved = true;
                newCentroids.Add((avgLat, avgLng));
            }
            centroids = newCentroids;
            if (!moved) break;
        }

        for (int i = 0; i < stops.Count; i++) stops[i].AssignedDriverId = drivers[assignment[i]].Id;
        await _db.SaveChangesAsync();
        return Ok(new { assigned = stops.Count, drivers = drivers.Count });
    }

    /// <summary>
    /// Optimiza el orden de las paradas de un driver (o de todos) usando nearest-neighbor desde el punto de partida.
    /// </summary>
    [HttpPost("optimize-order")]
    public async Task<IActionResult> OptimizeOrder([FromQuery] int? driverId = null)
    {
        // Punto de partida (de AppSettings)
        double? startLat = null, startLng = null;
        var latStr = (await _db.AppSettings.FindAsync("mapeo.start.lat"))?.Value;
        var lngStr = (await _db.AppSettings.FindAsync("mapeo.start.lng"))?.Value;
        if (double.TryParse(latStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var la)) startLat = la;
        if (double.TryParse(lngStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lo)) startLng = lo;

        // Si especificaron driver, optimizamos solo ese; si no, optimizamos todos los que tienen stops asignados.
        IEnumerable<int?> driverIds;
        if (driverId.HasValue && driverId.Value > 0) driverIds = new int?[] { driverId.Value };
        else driverIds = await _db.MapeoStops.Where(s => s.AssignedDriverId != null)
            .Select(s => s.AssignedDriverId).Distinct().ToListAsync();

        int optimized = 0;
        foreach (var did in driverIds)
        {
            var stopsD = await _db.MapeoStops.Where(s => s.AssignedDriverId == did).ToListAsync();
            if (stopsD.Count == 0) continue;
            // Punto inicial: si no hay startPoint, tomamos el primer stop como inicio.
            double curLat = startLat ?? (double)stopsD[0].Latitude;
            double curLng = startLng ?? (double)stopsD[0].Longitude;
            var remaining = new List<MapeoStop>(stopsD);
            int order = 1;
            while (remaining.Count > 0)
            {
                var next = remaining.OrderBy(s => Hav(curLat, curLng, (double)s.Latitude, (double)s.Longitude)).First();
                next.OrderInRoute = order++;
                next.UpdatedAt = DateTime.UtcNow;
                curLat = (double)next.Latitude; curLng = (double)next.Longitude;
                remaining.Remove(next);
                optimized++;
            }
        }
        await _db.SaveChangesAsync();
        return Ok(new { optimized });
    }

    /// <summary>Distancia haversine en km (aproximada).</summary>
    private static double Hav(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371.0;
        double toRad(double d) => d * Math.PI / 180.0;
        var dLat = toRad(lat2 - lat1);
        var dLng = toRad(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(toRad(lat1)) * Math.Cos(toRad(lat2)) *
                Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>Cuenta cuantos Flex pendientes hay para importar (dado un rango de dias). Sirve para el preview antes de confirmar.</summary>
    [HttpGet("import-flex-preview")]
    public async Task<IActionResult> ImportFlexPreview([FromQuery] int days = 1)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var ships = await _db.MeliShipments
            .Where(s => s.LogisticType == "self_service"
                     && s.Status != "delivered" && s.Status != "cancelled"
                     && s.Latitude != null && s.Longitude != null
                     && (s.DateCreated == null || s.DateCreated >= since))
            .Select(s => new { s.MeliShipmentId, s.ReceiverName, s.City, s.AddressLine })
            .ToListAsync();
        var existingRefs = await _db.MapeoStops
            .Where(s => s.Origin == "flex")
            .Select(s => s.OriginRefId)
            .ToListAsync();
        var existingSet = new HashSet<string?>(existingRefs);
        var nuevos = ships.Where(x => !existingSet.Contains(x.MeliShipmentId.ToString())).ToList();
        return Ok(new
        {
            total = ships.Count,
            yaCargados = ships.Count - nuevos.Count,
            aImportar = nuevos.Count,
            sample = nuevos.Take(5).Select(x => new { x.ReceiverName, x.City, x.AddressLine })
        });
    }

    /// <summary>Importa todos los shipments Flex pendientes como paradas (si todavía no existen).</summary>
    [HttpPost("import-flex")]
    public async Task<IActionResult> ImportFlex([FromQuery] int days = 7)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var ships = await _db.MeliShipments
            .Where(s => s.LogisticType == "self_service"
                     && s.Status != "delivered" && s.Status != "cancelled"
                     && s.Latitude != null && s.Longitude != null
                     && (s.DateCreated == null || s.DateCreated >= since))
            .ToListAsync();
        // Excluir las que ya están como stops
        var existingRefs = await _db.MapeoStops
            .Where(s => s.Origin == "flex")
            .Select(s => s.OriginRefId)
            .ToListAsync();
        var existingSet = new HashSet<string?>(existingRefs);
        int created = 0;
        foreach (var sh in ships)
        {
            var refId = sh.MeliShipmentId.ToString();
            if (existingSet.Contains(refId)) continue;
            _db.MapeoStops.Add(new MapeoStop
            {
                Origin = "flex",
                OriginRefId = refId,
                Alias = sh.ReceiverName,
                Direccion = sh.AddressLine ?? $"{sh.City} CP {sh.ZipCode}",
                Latitude = sh.Latitude!.Value,
                Longitude = sh.Longitude!.Value,
                ContactName = sh.ReceiverName,
                Telefono = sh.ReceiverPhone,
                Notas = sh.Comment,
                InternalStatus = "pending",
                CreatedAt = DateTime.UtcNow
            });
            created++;
        }
        await _db.SaveChangesAsync();
        return Ok(new { created, total = ships.Count });
    }
}
