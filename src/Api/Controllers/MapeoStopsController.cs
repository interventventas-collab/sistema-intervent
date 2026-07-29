using Api.Data;
using Api.Models;
using Api.Services;
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
    private readonly GoogleRoutesService _routes;
    private readonly VentaMapeoService _ventaMapeo;
    private readonly AlqMapeoService _alqMapeo;
    private readonly GoogleMapsLinkResolverService _mapsResolver;
    private readonly MeliShipmentService _shipmentSvc;
    private readonly ILogger<MapeoStopsController> _logger;
    public MapeoStopsController(AppDbContext db, GoogleRoutesService routes, VentaMapeoService ventaMapeo, AlqMapeoService alqMapeo, GoogleMapsLinkResolverService mapsResolver, MeliShipmentService shipmentSvc, ILogger<MapeoStopsController> logger)
    { _db = db; _routes = routes; _ventaMapeo = ventaMapeo; _alqMapeo = alqMapeo; _mapsResolver = mapsResolver; _shipmentSvc = shipmentSvc; _logger = logger; }

    public record StopDto(int Id, string Origin, string? OriginRefId, string? Alias, string Direccion,
        decimal Latitude, decimal Longitude, string? ContactName, string? Telefono, string? Notas,
        string InternalStatus, int? AssignedDriverId, string? AssignedDriverName, string? AssignedDriverColor,
        int? AssignedVehicleSlot, int? OrderInRoute, DateTime CreatedAt,
        string? Localidad = null,
        // Datos del envío de MeLi enlazado (para paradas Flex/ME1 escaneadas): usuario, nº venta, entregado.
        long? MeliOrderId = null, string? BuyerNickname = null, string? MeliStatus = null,
        DateTime? DateDelivered = null, string? ReceiverName = null);

    private static StopDto Map(MapeoStop s) => new(
        s.Id, s.Origin, s.OriginRefId, s.Alias, s.Direccion, s.Latitude, s.Longitude,
        s.ContactName, s.Telefono, s.Notas, s.InternalStatus,
        s.AssignedDriverId, s.AssignedDriver?.Nombre, s.AssignedDriver?.Color,
        s.AssignedVehicleSlot, s.OrderInRoute, s.CreatedAt, s.Localidad);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? driverId = null, [FromQuery] string? internalStatus = null)
    {
        var q = _db.MapeoStops.Include(s => s.AssignedDriver).AsQueryable();
        if (driverId.HasValue) q = q.Where(s => s.AssignedDriverId == driverId.Value);
        if (!string.IsNullOrWhiteSpace(internalStatus)) q = q.Where(s => s.InternalStatus == internalStatus);
        var list = await q.OrderBy(s => s.AssignedDriverId).ThenBy(s => s.OrderInRoute ?? int.MaxValue).ThenBy(s => s.Id).ToListAsync();
        // Enriquecemos las paradas Flex/ME1 con datos del envío de MeLi (usuario, nº venta, entregado).
        var refs = list.Where(s => (s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId != null)
                       .Select(s => long.TryParse(s.OriginRefId, out var v) ? v : 0L)
                       .Where(v => v != 0L).Distinct().ToList();
        var ships = refs.Count == 0
            ? new Dictionary<long, MeliShipment>()
            : await _db.MeliShipments.Where(m => refs.Contains(m.MeliShipmentId)).ToDictionaryAsync(m => m.MeliShipmentId);
        return Ok(list.Select(s =>
        {
            var dto = Map(s);
            if ((s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId != null
                && long.TryParse(s.OriginRefId, out var sid) && ships.TryGetValue(sid, out var m))
            {
                dto = dto with
                {
                    MeliOrderId = m.MeliOrderId,
                    BuyerNickname = m.BuyerNickname,
                    MeliStatus = m.Status,
                    DateDelivered = m.DateDelivered,
                    ReceiverName = m.ReceiverName
                };
            }
            return dto;
        }));
    }

    public record CreateStopRequest(string Origin, string? OriginRefId, string? Alias, string Direccion,
        decimal Latitude, decimal Longitude, string? ContactName, string? Telefono, string? Notas,
        string? Localidad = null);

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
            Localidad = string.IsNullOrWhiteSpace(req.Localidad) ? null : req.Localidad.Trim(),
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
        // Si es una VENTA y se asignó a un repartidor real, la "cargamos" también en el circuito
        // normal (Cafe_QrEscaneos) para que aparezca operable (entregar/cobrar) en Mis Pedidos.
        if (req.AssignedDriverId.HasValue && s.AssignedDriverId.HasValue)
            await PropagarVentaAlRepartidorAsync(s);
        return Ok(Map(s));
    }

    /// <summary>
    /// Cuando una parada de VENTA (Origin=venta_cafe) se asigna a un repartidor en el Mapeo, la
    /// "carga" también en el circuito normal (Cafe_QrEscaneos) para ese repartidor REAL, así aparece
    /// en Mis Pedidos como venta OPERABLE (entregar/cobrar), no solo en la ruta del mapa.
    /// Mismo criterio que el escaneo del repartidor: "el último que la agarra se la queda" (AGREGA un
    /// registro 'cargado', no borra nada). No toca ventas ya entregadas ni el circuito de plata.
    /// </summary>
    private async Task PropagarVentaAlRepartidorAsync(MapeoStop stop)
    {
        if (stop.Origin != "venta_cafe" || stop.OriginRefId == null) return;
        if (!stop.AssignedDriverId.HasValue) return;
        if (!int.TryParse(stop.OriginRefId, out var ventaId)) return;

        var driver = await _db.MapeoDrivers.FindAsync(stop.AssignedDriverId.Value);
        if (driver?.CafeRepartidorId is not int repId) return; // el chofer del mapa no está vinculado a un repartidor real

        var venta = await _db.CafeVentas.FirstOrDefaultAsync(v => v.Id == ventaId);
        if (venta is null || venta.EntregadoPorRepartidorId.HasValue) return; // no re-cargar ventas ya entregadas

        var ultimoCargado = await _db.CafeQrEscaneos
            .Where(e => e.VentaId == ventaId && e.Accion == "cargado")
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync();
        if (ultimoCargado?.RepartidorId == repId) return; // ya es de este repartidor: nada que hacer

        _db.CafeQrEscaneos.Add(new CafeQrEscaneo
        {
            VentaId = ventaId,
            RepartidorId = repId,
            Accion = "cargado",
            CreatedAt = DateTime.UtcNow,
            Ip = "mapeo-asignar"
        });
        await _db.SaveChangesAsync();
    }

    public record SetUbicacionRequest(double Lat, double Lng, string? Direccion);

    /// <summary>Fija la ubicación de una parada (elegida en el buscador del mapa) y, si es una VENTA,
    /// la guarda también en la ficha del cliente (coords + link) para la próxima. Corrige domicilios
    /// sin salir del mapa — mejor que el geocoder que a veces adivina mal.</summary>
    [HttpPost("{id:int}/ubicacion")]
    public async Task<IActionResult> SetUbicacion(int id, [FromBody] SetUbicacionRequest req)
    {
        var s = await _db.MapeoStops.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound(new { error = "Parada no encontrada" });
        s.Latitude = (decimal)req.Lat;
        s.Longitude = (decimal)req.Lng;
        if (!string.IsNullOrWhiteSpace(req.Direccion)) s.Direccion = req.Direccion.Trim();
        s.UpdatedAt = DateTime.UtcNow;

        // Si es una venta, guardamos la ubicación en la ficha del cliente para la próxima.
        if (s.Origin == "venta_cafe" && int.TryParse(s.OriginRefId, out var ventaId))
        {
            var venta = await _db.CafeVentas.Include(v => v.ClienteNav).FirstOrDefaultAsync(v => v.Id == ventaId);
            var cli = venta?.ClienteNav;
            if (cli is not null)
            {
                cli.MapeoLat = (decimal)req.Lat;
                cli.MapeoLng = (decimal)req.Lng;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                cli.MapeoLink = $"https://www.google.com/maps/search/?api=1&query={req.Lat.ToString(ci)},{req.Lng.ToString(ci)}";
                if (!string.IsNullOrWhiteSpace(req.Direccion) && string.IsNullOrWhiteSpace(cli.DomicilioEntrega) && string.IsNullOrWhiteSpace(cli.Direccion))
                    cli.DomicilioEntrega = req.Direccion.Trim();
            }
        }
        await _db.SaveChangesAsync();
        return Ok(Map(s));
    }

    public record ReorderRequest(int[] StopIds);

    /// <summary>
    /// Reordena la ruta: recibe los IDs de las paradas en el orden deseado y les escribe
    /// OrderInRoute = 1, 2, 3, ... en ese orden. Se usa para arrastrar/subir/bajar desde el listado.
    /// </summary>
    [HttpPost("reorder")]
    public async Task<IActionResult> Reorder([FromBody] ReorderRequest req)
    {
        if (req?.StopIds is null || req.StopIds.Length == 0) return Ok(new { updated = 0 });
        var ids = req.StopIds.ToList();
        var stops = await _db.MapeoStops.Where(s => ids.Contains(s.Id)).ToListAsync();
        var now = DateTime.UtcNow;
        int order = 1;
        foreach (var id in ids) // respetamos el orden recibido
        {
            var s = stops.FirstOrDefault(x => x.Id == id);
            if (s is null) continue;
            s.OrderInRoute = order++;
            s.UpdatedAt = now;
        }
        await _db.SaveChangesAsync();
        return Ok(new { updated = order - 1 });
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
        // Snapshot automático antes de borrar — para no perder lo que armó el usuario
        try
        {
            var snap = await MapeoSnapshotsController.BuildSnapshotAsync(_db, "Auto-guardado antes de Empezar desde cero",
                User.Identity?.IsAuthenticated == true ? User.Identity?.Name : null);
            if (snap is not null)
            {
                _db.MapeoRouteSnapshots.Add(snap);
                await _db.SaveChangesAsync();
            }
        }
        catch { /* tolerar — preferimos limpiar igual */ }

        await _db.MapeoStops.ExecuteDeleteAsync();
        return Ok(new { ok = true });
    }

    /// <summary>
    /// Limpia las paradas que quedaron de dias ANTERIORES (creadas antes de hoy, hora Argentina),
    /// para que el mapa arranque limpio cada dia. Lo de HOY se mantiene (no se pierde lo que se esta
    /// cargando ahora aunque se recargue la pagina). Hace un respaldo (snapshot) antes de borrar.
    /// Se llama automaticamente al abrir el mapa.
    /// </summary>
    [HttpPost("clear-stale")]
    public async Task<IActionResult> ClearStale()
    {
        var nowLocal = DateTime.UtcNow.AddHours(-3);         // Argentina
        var hoyInicioUtc = nowLocal.Date.AddHours(3);        // 00:00 ART expresado en UTC

        var staleCount = await _db.MapeoStops.CountAsync(s => s.CreatedAt < hoyInicioUtc);
        if (staleCount == 0) return Ok(new { cleared = 0 });

        // Respaldo automatico antes de borrar (tolerante a fallo: preferimos limpiar igual).
        try
        {
            var snap = await MapeoSnapshotsController.BuildSnapshotAsync(_db, "Auto-respaldo antes de limpiar dia anterior",
                User.Identity?.IsAuthenticated == true ? User.Identity?.Name : null);
            if (snap is not null)
            {
                _db.MapeoRouteSnapshots.Add(snap);
                await _db.SaveChangesAsync();
            }
        }
        catch { }

        var deleted = await _db.MapeoStops.Where(s => s.CreatedAt < hoyInicioUtc).ExecuteDeleteAsync();
        return Ok(new { cleared = deleted });
    }

    // ===== Asignacion visual a vehiculos (slot) =====
    public record AssignSlotRequest(int? Slot);

    [HttpPut("{id:int}/vehicle-slot")]
    public async Task<IActionResult> AssignSlot(int id, [FromBody] AssignSlotRequest req)
    {
        var s = await _db.MapeoStops.FindAsync(id);
        if (s is null) return NotFound(new { error = "Parada no encontrada" });
        s.AssignedVehicleSlot = req.Slot.HasValue && req.Slot.Value > 0 ? req.Slot.Value : null;
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, slot = s.AssignedVehicleSlot });
    }

    [HttpPost("clear-vehicle-assignments")]
    public async Task<IActionResult> ClearVehicleAssignments()
    {
        await _db.MapeoStops.ExecuteUpdateAsync(set => set
            .SetProperty(s => s.AssignedVehicleSlot, (int?)null)
            .SetProperty(s => s.UpdatedAt, DateTime.UtcNow));
        return Ok(new { ok = true });
    }

    public record AssignDriverToSlotRequest(int Slot, int? DriverId);

    /// <summary>Asigna un chofer a todas las paradas de un slot (vehículo del día).</summary>
    [HttpPost("assign-driver-to-slot")]
    public async Task<IActionResult> AssignDriverToSlot([FromBody] AssignDriverToSlotRequest req)
    {
        if (req.Slot <= 0) return BadRequest(new { error = "Slot inválido" });
        int? did = req.DriverId.HasValue && req.DriverId.Value > 0 ? req.DriverId.Value : null;
        var n = await _db.MapeoStops
            .Where(s => s.AssignedVehicleSlot == req.Slot)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.AssignedDriverId, did)
                .SetProperty(s => s.UpdatedAt, DateTime.UtcNow));
        return Ok(new { ok = true, updated = n });
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
    public async Task<IActionResult> OptimizeOrder([FromQuery] int? driverId = null, [FromQuery] int? vehicleSlot = null, [FromQuery] bool all = false)
    {
        // Punto de partida (de AppSettings)
        double? startLat = null, startLng = null;
        var latStr = (await _db.AppSettings.FindAsync("mapeo.start.lat"))?.Value;
        var lngStr = (await _db.AppSettings.FindAsync("mapeo.start.lng"))?.Value;
        if (double.TryParse(latStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var la)) startLat = la;
        if (double.TryParse(lngStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lo)) startLng = lo;

        // Determinar grupos a optimizar: TODO junto (all), por VEHICULO (slot), por DRIVER, o todos los drivers.
        var grupos = new List<List<MapeoStop>>();
        if (all)
        {
            // "Armar ruta óptima" de TODAS las paradas cargadas como una sola ruta (aunque no tengan chofer).
            var todas = await _db.MapeoStops.ToListAsync();
            if (todas.Count > 0) grupos.Add(todas);
        }
        else if (vehicleSlot.HasValue && vehicleSlot.Value > 0)
        {
            var stopsV = await _db.MapeoStops.Where(s => s.AssignedVehicleSlot == vehicleSlot.Value).ToListAsync();
            if (stopsV.Count > 0) grupos.Add(stopsV);
        }
        else
        {
            IEnumerable<int?> driverIds;
            if (driverId.HasValue && driverId.Value > 0) driverIds = new int?[] { driverId.Value };
            else driverIds = await _db.MapeoStops.Where(s => s.AssignedDriverId != null)
                .Select(s => s.AssignedDriverId).Distinct().ToListAsync();
            foreach (var did in driverIds)
            {
                var stopsD = await _db.MapeoStops.Where(s => s.AssignedDriverId == did).ToListAsync();
                if (stopsD.Count > 0) grupos.Add(stopsD);
            }
        }

        int optimized = 0;
        int optimizedByGoogle = 0;
        foreach (var grupo in grupos)
        {
            // 1) Intentar ordenar por CALLES REALES con Google (barato: 1 consulta por grupo).
            //    Si no hay clave, hay demasiadas paradas (>25) o falla la API, se usa el respaldo (linea recta).
            if (await TryOptimizeWithGoogleAsync(grupo, startLat, startLng))
            {
                optimized += grupo.Count;
                optimizedByGoogle += grupo.Count;
                continue;
            }

            // 2) Respaldo: nearest-neighbor con distancia en linea recta (haversine), desde el punto de partida.
            double curLat = startLat ?? (double)grupo[0].Latitude;
            double curLng = startLng ?? (double)grupo[0].Longitude;
            var remaining = new List<MapeoStop>(grupo);
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
        return Ok(new { optimized, optimizedByGoogle });
    }

    public record RutaOverviewDto(string Key, string Label, string Color, int? DriverId,
        int DurationSeconds, int DistanceMeters, string? EncodedPolyline, int StopCount);

    /// <summary>
    /// Devuelve, por repartidor (o como ruta única), el tiempo estimado + km + la línea dibujable de la ruta.
    /// Usa el orden ya calculado (OrderInRoute) y el punto de partida configurado.
    /// </summary>
    [HttpGet("routes-overview")]
    public async Task<IActionResult> RoutesOverview([FromQuery] bool single = false)
    {
        double? startLat = null, startLng = null;
        var latStr = (await _db.AppSettings.FindAsync("mapeo.start.lat"))?.Value;
        var lngStr = (await _db.AppSettings.FindAsync("mapeo.start.lng"))?.Value;
        if (double.TryParse(latStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var la)) startLat = la;
        if (double.TryParse(lngStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lo)) startLng = lo;

        var conOrden = await _db.MapeoStops.Include(x => x.AssignedDriver)
            .Where(s => s.OrderInRoute != null).ToListAsync();
        if (conOrden.Count == 0) return Ok(new List<RutaOverviewDto>());

        // single = todas las paradas como UNA ruta; si no, agrupadas por repartidor.
        var grupos = single
            ? new List<(int? did, MapeoDriver? drv, List<MapeoStop> ss)> { (null, null, conOrden) }
            : conOrden.GroupBy(s => s.AssignedDriverId)
                      .Select(g => (g.Key, g.First().AssignedDriver, g.ToList()))
                      .ToList();

        var result = new List<RutaOverviewDto>();
        foreach (var (did, drv, ss) in grupos)
        {
            var ordered = ss.OrderBy(s => s.OrderInRoute).ToList();
            var pts = ordered.Select(s => ((double)s.Latitude, (double)s.Longitude)).ToList();

            (double lat, double lng) origin, dest;
            List<(double lat, double lng)> inter;
            if (startLat.HasValue && startLng.HasValue)
            {
                origin = (startLat.Value, startLng.Value);
                dest = pts[^1];
                inter = pts.Take(pts.Count - 1).ToList();
            }
            else
            {
                origin = pts[0];
                dest = pts[^1];
                inter = pts.Count > 2 ? pts.Skip(1).Take(pts.Count - 2).ToList() : new();
            }

            var rr = await _routes.ComputeRouteAsync(origin, dest, inter);
            string label = single ? "Ruta única" : (drv?.Nombre ?? "Sin repartidor");
            string color = single ? "#1d4ed8" : (string.IsNullOrEmpty(drv?.Color) ? "#6b7280" : drv!.Color!);
            result.Add(new RutaOverviewDto(
                did?.ToString() ?? "none", label, color, did,
                rr?.DurationSeconds ?? 0, rr?.DistanceMeters ?? 0, rr?.EncodedPolyline, ordered.Count));
        }
        return Ok(result);
    }

    /// <summary>
    /// Aplica el filtro por modo de entrega (today / tomorrow / overdue / all) usando EstimatedDeliveryLimit.
    /// Refleja la misma logica que MeliShipmentsController.ListFlex para mantener coherencia entre vistas.
    /// </summary>
    private IQueryable<MeliShipment> ApplyDeliveryModeFilter(IQueryable<MeliShipment> q, string mode)
    {
        var nowLocal = DateTime.UtcNow.AddHours(-3); // Argentina
        var todayLocal = nowLocal.Date;
        switch ((mode ?? "today").ToLowerInvariant())
        {
            case "today":
                {
                    var t1 = todayLocal.AddHours(3);
                    var t2 = todayLocal.AddDays(1).AddHours(3);
                    return q.Where(s => (s.EstimatedDeliveryLimit ?? s.DateCreated) >= t1
                                     && (s.EstimatedDeliveryLimit ?? s.DateCreated) < t2);
                }
            case "tomorrow":
                {
                    var t1 = todayLocal.AddDays(1).AddHours(3);
                    var t2 = todayLocal.AddDays(2).AddHours(3);
                    return q.Where(s => (s.EstimatedDeliveryLimit ?? s.DateCreated) >= t1
                                     && (s.EstimatedDeliveryLimit ?? s.DateCreated) < t2);
                }
            case "overdue":
                {
                    var nowUtc = DateTime.UtcNow;
                    return q.Where(s => s.EstimatedDeliveryLimit != null && s.EstimatedDeliveryLimit < nowUtc);
                }
            case "all":
            default:
                return q;
        }
    }

    /// <summary>
    /// Ordena las paradas del grupo por CALLES REALES usando Google Routes API. Si hay punto de partida
    /// configurado, arranca desde ahi; si no, deja como primera la primer parada del grupo (igual que el respaldo).
    /// Escribe OrderInRoute en cada parada. Devuelve true si Google resolvio el orden; false para que el
    /// llamador use el respaldo en linea recta (sin clave, grupo > 25 paradas, o error de la API).
    /// </summary>
    private async Task<bool> TryOptimizeWithGoogleAsync(List<MapeoStop> grupo, double? startLat, double? startLng)
    {
        if (!_routes.IsConfigured || grupo.Count == 0) return false;
        var now = DateTime.UtcNow;
        try
        {
            if (startLat.HasValue && startLng.HasValue)
            {
                var start = (startLat.Value, startLng.Value);
                var inter = grupo.Select(s => ((double)s.Latitude, (double)s.Longitude)).ToList();
                var order = await _routes.OptimizeWaypointOrderAsync(start, start, inter);
                if (!EsPermutacionValida(order, grupo.Count)) return false;
                int ord = 1;
                foreach (var idx in order!) { grupo[idx].OrderInRoute = ord++; grupo[idx].UpdatedAt = now; }
                return true;
            }
            else
            {
                // Sin punto de partida: la primer parada del grupo queda como arranque fijo.
                var first = grupo[0];
                var origin = ((double)first.Latitude, (double)first.Longitude);
                var rest = grupo.Skip(1).ToList();
                var inter = rest.Select(s => ((double)s.Latitude, (double)s.Longitude)).ToList();
                var order = await _routes.OptimizeWaypointOrderAsync(origin, origin, inter);
                if (!EsPermutacionValida(order, rest.Count)) return false;
                int ord = 1;
                first.OrderInRoute = ord++; first.UpdatedAt = now;
                foreach (var idx in order!) { rest[idx].OrderInRoute = ord++; rest[idx].UpdatedAt = now; }
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Optimización con Google falló para un grupo; se usa el respaldo en línea recta.");
            return false;
        }
    }

    /// <summary>Valida que 'order' sea una permutación EXACTA de 0..count-1 (evita índices fuera de rango de Google).</summary>
    private static bool EsPermutacionValida(int[]? order, int count)
    {
        if (order is null || order.Length != count) return false;
        if (count == 0) return true;
        var visto = new bool[count];
        foreach (var i in order)
        {
            if (i < 0 || i >= count || visto[i]) return false;
            visto[i] = true;
        }
        return true;
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
    public async Task<IActionResult> ImportFlexPreview([FromQuery] string mode = "today")
    {
        // Mismo modo que el filtro principal del Mapeo: today / tomorrow / overdue / all.
        var q = _db.MeliShipments
            .Where(s => s.LogisticType == "self_service"
                     && s.Status != "delivered" && s.Status != "cancelled"
                     && s.Latitude != null && s.Longitude != null);
        q = ApplyDeliveryModeFilter(q, mode);
        var ships = await q
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
    public async Task<IActionResult> ImportFlex([FromQuery] string mode = "today")
    {
        var q = _db.MeliShipments
            .Where(s => s.LogisticType == "self_service"
                     && s.Status != "delivered" && s.Status != "cancelled"
                     && s.Latitude != null && s.Longitude != null);
        q = ApplyDeliveryModeFilter(q, mode);
        var ships = await q
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
                Localidad = string.IsNullOrWhiteSpace(sh.City) ? null : sh.City,
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

    public record ScanFlexRequest(string Code);

    /// <summary>
    /// Suma UNA parada al mapa a partir del QR de una etiqueta Flex escaneada con el celular.
    /// El QR trae un JSON tipo {"id":"47599650926",...}; extraemos el id y buscamos ese envio
    /// (que ya debe estar sincronizado con su ubicacion). Idempotente: si ya estaba, avisa y no duplica.
    /// </summary>
    [HttpPost("scan-flex")]
    public async Task<IActionResult> ScanFlex([FromBody] ScanFlexRequest req)
    {
        // ¿Es el QR de una factura/cotización NUESTRA? (URL .../repartidor/{token}).
        // Si sí, buscamos la venta por ese token y la sumamos al mapa (misma base que el botón del listado).
        var ventaToken = ExtractRepartidorToken(req?.Code);
        if (ventaToken is not null)
        {
            var venta = await _db.CafeVentas.Include(x => x.ClienteNav).FirstOrDefaultAsync(x => x.PublicToken == ventaToken);
            if (venta is null)
                return Ok(new { ok = false, motivo = "no_encontrado", mensaje = "No reconozco ese comprobante (puede ser de un alquiler o de otra cuenta)." });
            var rv = await _ventaMapeo.SumarVentaAsync(venta);
            return Ok(new { ok = rv.Ok, yaEstaba = rv.YaEstaba, motivo = rv.Motivo, mensaje = rv.Mensaje, nombre = rv.Nombre, localidad = rv.Localidad, stopId = rv.StopId });
        }

        // ¿Es el QR de una reserva de ALQUILER? (URL .../alquiler/{token}).
        var alqToken = ExtractAlquilerToken(req?.Code);
        if (alqToken is not null)
        {
            var reserva = await _db.AlqReservas.Include(x => x.ClienteNav).FirstOrDefaultAsync(x => x.PublicToken == alqToken);
            if (reserva is null)
                return Ok(new { ok = false, motivo = "no_encontrado", mensaje = "No reconozco ese comprobante de alquiler." });
            var ra = await _alqMapeo.SumarReservaAsync(reserva);
            return Ok(new { ok = ra.Ok, yaEstaba = ra.YaEstaba, motivo = ra.Motivo, mensaje = ra.Mensaje, nombre = ra.Nombre, localidad = ra.Localidad, stopId = ra.StopId });
        }

        var id = ExtractShipmentId(req?.Code);
        if (id is null)
            return Ok(new { ok = false, motivo = "sin_id", mensaje = "No pude leer el numero de envio de ese codigo." });

        var sh = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == id.Value);
        if (sh is null)
        {
            // No estaba sincronizado: lo traemos de MeLi en el momento (reemplaza al viejo paso "Traer Flex").
            try { await _shipmentSvc.SyncSingleShipmentAsync(id.Value); }
            catch (Exception ex) { _logger.LogWarning(ex, "scan-flex: no se pudo traer el envio {Id} de MeLi", id.Value); }
            sh = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == id.Value);
        }
        if (sh is null)
            return Ok(new { ok = false, motivo = "no_encontrado", id = id.Value, mensaje = $"No pude traer el envio {id.Value} de MercadoLibre. Puede ser de otra cuenta, o muy nuevo (esperá unos minutos)." });
        return Ok(await AddFlexStopFromShipmentAsync(sh));
    }

    /// <summary>Crea (o reconoce) la parada de un envío MeLi Flex/ME1 ya sincronizado. Idempotente por
    /// OriginRefId = MeliShipmentId. Devuelve el mismo shape que scan-flex.</summary>
    private async Task<object> AddFlexStopFromShipmentAsync(MeliShipment sh)
    {
        if (sh.Latitude is null || sh.Longitude is null)
            return new { ok = false, motivo = "sin_ubicacion", id = sh.MeliShipmentId, nombre = sh.ReceiverName, mensaje = "Ese envio no tiene ubicacion cargada, no lo puedo poner en el mapa." };

        var refId = sh.MeliShipmentId.ToString();
        var existente = await _db.MapeoStops.FirstOrDefaultAsync(s => (s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId == refId);
        if (existente is not null)
            return new { ok = true, yaEstaba = true, id = sh.MeliShipmentId, nombre = sh.ReceiverName, localidad = sh.City, stopId = existente.Id, mensaje = "Ya estaba en el mapa." };

        var stop = new MapeoStop
        {
            Origin = string.Equals(sh.Mode, "me1", System.StringComparison.OrdinalIgnoreCase) ? "me1" : "flex",
            OriginRefId = refId,
            Alias = sh.ReceiverName,
            Direccion = sh.AddressLine ?? $"{sh.City} CP {sh.ZipCode}",
            Localidad = string.IsNullOrWhiteSpace(sh.City) ? null : sh.City,
            Latitude = sh.Latitude!.Value,
            Longitude = sh.Longitude!.Value,
            ContactName = sh.ReceiverName,
            Telefono = sh.ReceiverPhone,
            Notas = sh.Comment,
            InternalStatus = "pending",
            CreatedAt = DateTime.UtcNow
        };
        _db.MapeoStops.Add(stop);
        await _db.SaveChangesAsync();
        return new { ok = true, yaEstaba = false, id = sh.MeliShipmentId, nombre = sh.ReceiverName, localidad = sh.City, stopId = stop.Id, mensaje = "Agregado al mapa." };
    }

    public record ByNumberRequest(string Number);

    /// <summary>
    /// Trae UNA parada al mapa a partir de un NÚMERO tipeado a mano (plan B si falla el escáner/impresora).
    /// Reconoce, en este orden: (1) nº de VENTA propia (Cafe_Ventas), (2) nº de ALQUILER (AlqReservas),
    /// (3) nº de ENVÍO o nº de VENTA de MercadoLibre (Flex/ME1). Si el envío MeLi no estaba sincronizado,
    /// lo trae de MeLi en el momento. Mismo shape de respuesta que scan-flex.
    /// </summary>
    [HttpPost("by-number")]
    public async Task<IActionResult> ByNumber([FromBody] ByNumberRequest req)
    {
        var raw = (req?.Number ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return Ok(new { ok = false, motivo = "vacio", mensaje = "Escribí un número." });

        // (1) Venta propia (café) por número exacto.
        var venta = await _db.CafeVentas.Include(x => x.ClienteNav).FirstOrDefaultAsync(x => x.Numero == raw);
        if (venta is not null)
        {
            var rv = await _ventaMapeo.SumarVentaAsync(venta);
            return Ok(new { ok = rv.Ok, yaEstaba = rv.YaEstaba, motivo = rv.Motivo, mensaje = rv.Mensaje, nombre = rv.Nombre, localidad = rv.Localidad, stopId = rv.StopId, tipo = "venta" });
        }

        // (2) Alquiler por número exacto.
        var reserva = await _db.AlqReservas.Include(x => x.ClienteNav).FirstOrDefaultAsync(x => x.Numero == raw);
        if (reserva is not null)
        {
            var ra = await _alqMapeo.SumarReservaAsync(reserva);
            return Ok(new { ok = ra.Ok, yaEstaba = ra.YaEstaba, motivo = ra.Motivo, mensaje = ra.Mensaje, nombre = ra.Nombre, localidad = ra.Localidad, stopId = ra.StopId, tipo = "alquiler" });
        }

        // (3) MercadoLibre: nº de envío o nº de venta (order). Nos quedamos solo con los dígitos.
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length >= 6 && long.TryParse(digits, out var num))
        {
            // 3a) directo como nº de envío (shipment id)
            var sh = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == num);
            // 3b) como nº de venta (order id) ya guardado en el envío
            if (sh is null) sh = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliOrderId == num);
            // 3c) como nº de venta buscando la orden → su envío (y sincronizándolo si hace falta)
            if (sh is null)
            {
                var ord = await _db.MeliOrders.FirstOrDefaultAsync(o => o.MeliOrderId == num);
                if (ord?.ShippingId is not null)
                {
                    sh = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == ord.ShippingId.Value);
                    if (sh is null)
                    {
                        try { await _shipmentSvc.SyncSingleShipmentAsync(ord.ShippingId.Value); }
                        catch (Exception ex) { _logger.LogWarning(ex, "by-number: no se pudo traer el envio {Id} de MeLi", ord.ShippingId.Value); }
                        sh = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == ord.ShippingId.Value);
                    }
                }
            }
            // 3d) último recurso: tratar el número como nº de envío y traerlo de MeLi en el momento
            if (sh is null)
            {
                try { await _shipmentSvc.SyncSingleShipmentAsync(num); }
                catch (Exception ex) { _logger.LogWarning(ex, "by-number: no se pudo traer el envio {Id} de MeLi", num); }
                sh = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == num);
            }
            if (sh is not null)
            {
                var res = await AddFlexStopFromShipmentAsync(sh);
                return Ok(res);
            }
        }

        return Ok(new { ok = false, motivo = "no_encontrado", mensaje = $"No encontré ninguna venta, alquiler ni envío con el número {raw}. Revisá que esté bien escrito." });
    }

    /// <summary>Saca el numero de envio de lo que trae el QR (JSON con "id", o si no la corrida de digitos mas larga).</summary>
    private static long? ExtractShipmentId(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        // Preferimos el campo "id" del JSON del QR: {"id":"47599650926",...}
        var m = System.Text.RegularExpressions.Regex.Match(code, "\"id\"\\s*:\\s*\"?(\\d+)\"?");
        string digits;
        if (m.Success) digits = m.Groups[1].Value;
        else
        {
            // Respaldo: la corrida de digitos mas larga del texto (por si viene un codigo distinto).
            var runs = System.Text.RegularExpressions.Regex.Matches(code, "\\d+");
            digits = runs.Count == 0 ? "" : runs.Cast<System.Text.RegularExpressions.Match>()
                .Select(x => x.Value).OrderByDescending(x => x.Length).First();
        }
        return digits.Length >= 6 && long.TryParse(digits, out var val) ? val : (long?)null;
    }

    /// <summary>Saca el token de una URL de comprobante de VENTA (.../repartidor/{token}). null si no aplica.</summary>
    private static string? ExtractRepartidorToken(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(code, @"/repartidor/([A-Za-z0-9_\-]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Saca el token de una URL de comprobante de ALQUILER (.../alquiler/{token}). null si no aplica.</summary>
    private static string? ExtractAlquilerToken(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(code, @"/alquiler/([A-Za-z0-9_\-]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    public record FromShipmentRequest(int ShipmentId, string? Direccion, string? Link);

    /// <summary>
    /// Suma un envío de MercadoLibre (ME1 o Flex) al mapa desde su pantalla (botón "Al mapa").
    /// Usa las coords del envío; si no tiene, geocodifica la dirección. Si tampoco se puede, devuelve
    /// sin_domicilio para que el front pida la dirección. Idempotente por OriginRefId=MeliShipmentId.
    /// </summary>
    [HttpPost("from-shipment")]
    public async Task<IActionResult> FromShipment([FromBody] FromShipmentRequest req)
    {
        var sh = await _db.MeliShipments.FirstOrDefaultAsync(x => x.Id == req.ShipmentId);
        if (sh is null) return NotFound(new { ok = false, mensaje = "Envío no encontrado." });

        var nombre = string.IsNullOrWhiteSpace(sh.ReceiverName) ? "Cliente" : sh.ReceiverName!;
        var direccion = !string.IsNullOrWhiteSpace(sh.AddressLine) ? sh.AddressLine : sh.City;
        var localidad = sh.City;
        var telefono = sh.ReceiverPhone;
        var origin = sh.Mode == "me1" ? "me1" : (sh.LogisticType == "self_service" ? "flex" : "me1");

        decimal? lat = sh.Latitude, lng = sh.Longitude;

        if (req is not null && (!string.IsNullOrWhiteSpace(req.Link) || !string.IsNullOrWhiteSpace(req.Direccion)))
        {
            lat = null; lng = null;
            if (!string.IsNullOrWhiteSpace(req.Link))
            { var r = await _mapsResolver.TryResolverCoordenadasAsync(req.Link); if (r.HasValue) { lat = r.Value.lat; lng = r.Value.lng; } }
            if (lat is null && !string.IsNullOrWhiteSpace(req.Direccion))
            { var q = req.Direccion + (string.IsNullOrWhiteSpace(localidad) ? "" : ", " + localidad) + ", Argentina";
              var r = await _mapsResolver.TryGeocodeAddressAsync(q); if (r.HasValue) { lat = r.Value.lat; lng = r.Value.lng; } }
            if (lat is null)
                return Ok(new { ok = false, motivo = "no_resuelto", mensaje = "No pude encontrar esa dirección. Probá con calle + número + localidad, o pegá un link de Google Maps." });
            if (!string.IsNullOrWhiteSpace(req.Direccion)) direccion = req.Direccion.Trim();
        }
        else if (lat is null || lng is null)
        {
            // ME1 sin coords: geocodificar la dirección del envío.
            if (!string.IsNullOrWhiteSpace(direccion))
            { var q = direccion + (string.IsNullOrWhiteSpace(localidad) ? "" : ", " + localidad) + ", Argentina";
              var r = await _mapsResolver.TryGeocodeAddressAsync(q); if (r.HasValue) { lat = r.Value.lat; lng = r.Value.lng; } }
            if (lat is null)
                return Ok(new { ok = false, motivo = "sin_domicilio", mensaje = "Este envío no tiene ubicación. Cargá la dirección.",
                    nombre, direccionSugerida = direccion, localidad });
        }

        var refId = sh.MeliShipmentId.ToString();
        var existente = await _db.MapeoStops.FirstOrDefaultAsync(s => (s.Origin == "me1" || s.Origin == "flex") && s.OriginRefId == refId);
        if (existente is not null)
        {
            existente.Latitude = lat!.Value;
            existente.Longitude = lng!.Value;
            existente.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(new { ok = true, yaEstaba = true, mensaje = "Ya estaba en el mapa (actualicé la ubicación).", nombre, localidad });
        }

        _db.MapeoStops.Add(new MapeoStop
        {
            Origin = origin,
            OriginRefId = refId,
            Alias = nombre,
            Direccion = string.IsNullOrWhiteSpace(direccion) ? nombre : direccion!,
            Localidad = localidad,
            Latitude = lat!.Value,
            Longitude = lng!.Value,
            ContactName = nombre,
            Telefono = telefono,
            InternalStatus = "pending",
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, yaEstaba = false, mensaje = "Agregado al mapa.", nombre, localidad });
    }

    /// <summary>
    /// Devuelve la clave del navegador (Maps JavaScript API) para dibujar el mapa de Google.
    /// Es una clave pública restringida por dominio; el front la usa para cargar el mapa.
    /// </summary>
    [HttpGet("map-key")]
    public IActionResult MapKey([FromServices] IConfiguration config)
    {
        var key = config["GOOGLE_MAPS_BROWSER_KEY"] ?? Environment.GetEnvironmentVariable("GOOGLE_MAPS_BROWSER_KEY") ?? "";
        return Ok(new { key });
    }

    /// <summary>
    /// Devuelve un PNG con el QR de una URL (para mostrar en la compu y abrir el escáner en el celular).
    /// Se genera en el servidor (QRCoder) para no depender de librerías del navegador ni del caché.
    /// </summary>
    [HttpGet("escanear-qr")]
    public IActionResult EscanearQr([FromQuery] string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return BadRequest();
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
        return File(png, "image/png");
    }
}
