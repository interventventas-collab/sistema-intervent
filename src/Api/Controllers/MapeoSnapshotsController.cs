using System.Security.Claims;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/mapeo/snapshots")]
[Authorize]
public class MapeoSnapshotsController : ControllerBase
{
    private readonly AppDbContext _db;
    public MapeoSnapshotsController(AppDbContext db) { _db = db; }

    public record SnapshotListItemDto(int Id, string Title, int StopsCount, int VehiclesCount, int DriversCount,
        int DeliveredCount, DateTime CreatedAt, string? CreatedByUsername, string? Notes);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int days = 30)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        // Traemos también el StopsJson para contar cuántas paradas quedaron ENTREGADAS en cada foto.
        // Ese número es el que permite marcar sola la ruta "definitiva" del día (la más entregada)
        // y distinguirla de las pruebas (0 entregadas).
        var raw = await _db.MapeoRouteSnapshots
            .Where(s => s.CreatedAt >= since)
            .OrderByDescending(s => s.CreatedAt)
            .Take(200)
            .Select(s => new { s.Id, s.Title, s.StopsCount, s.VehiclesCount, s.DriversCount,
                s.StopsJson, s.CreatedAt, s.CreatedByUsername, s.Notes })
            .ToListAsync();

        var list = raw.Select(s => new SnapshotListItemDto(s.Id, s.Title, s.StopsCount, s.VehiclesCount,
            s.DriversCount, CountDelivered(s.StopsJson), s.CreatedAt, s.CreatedByUsername, s.Notes)).ToList();
        return Ok(list);
    }

    /// <summary>Cuenta cuántas paradas del snapshot figuran como entregadas. Usa el flag "delivered"
    /// (snapshots nuevos) y, si no está, cae al estado interno "entregado" (compatibilidad con fotos viejas).</summary>
    private static int CountDelivered(string? stopsJson)
    {
        if (string.IsNullOrWhiteSpace(stopsJson)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(stopsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return 0;
            int n = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("delivered", out var d) && d.ValueKind == JsonValueKind.True) { n++; continue; }
                if (el.TryGetProperty("internalStatus", out var st) && st.ValueKind == JsonValueKind.String
                    && string.Equals(st.GetString(), "entregado", System.StringComparison.OrdinalIgnoreCase)) n++;
            }
            return n;
        }
        catch { return 0; }
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var s = await _db.MapeoRouteSnapshots.FindAsync(id);
        if (s is null) return NotFound();
        return Ok(new
        {
            id = s.Id,
            title = s.Title,
            stopsCount = s.StopsCount,
            vehiclesCount = s.VehiclesCount,
            driversCount = s.DriversCount,
            createdAt = s.CreatedAt,
            createdByUsername = s.CreatedByUsername,
            notes = s.Notes,
            stops = JsonDocument.Parse(s.StopsJson).RootElement
        });
    }

    public record CreateSnapshotRequest(string? Notes);

    /// <summary>Crea un snapshot del estado actual del Mapeo (todos los stops con sus asignaciones).</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSnapshotRequest? req)
    {
        var snapshot = await BuildSnapshotAsync(req?.Notes,
            User.Identity?.IsAuthenticated == true ? User.Identity?.Name : null);
        if (snapshot is null) return BadRequest(new { error = "No hay paradas para guardar" });
        _db.MapeoRouteSnapshots.Add(snapshot);
        await _db.SaveChangesAsync();
        return Ok(new { id = snapshot.Id, title = snapshot.Title });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var s = await _db.MapeoRouteSnapshots.FindAsync(id);
        if (s is null) return NotFound();
        _db.MapeoRouteSnapshots.Remove(s);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>
    /// Helper estático que arma un snapshot con el estado actual de MapeoStops + drivers.
    /// Pensado para ser llamado desde otros controllers (ej: antes de "Empezar desde cero").
    /// Devuelve null si no hay paradas.
    /// </summary>
    public static async Task<MapeoRouteSnapshot?> BuildSnapshotAsync(AppDbContext db, string? notes, string? username)
    {
        var stops = await db.MapeoStops
            .Include(s => s.AssignedDriver)
            .ToListAsync();
        if (stops.Count == 0) return null;

        // Para saber cuáles se ENTREGARON de verdad (y así distinguir la ruta "definitiva" del día
        // de las pruebas), combinamos dos señales: el estado interno marcado por el repartidor
        // (InternalStatus == "entregado") Y lo que confirmó MercadoLibre en el envío Flex/ME1.
        var refs = stops.Where(s => (s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId != null)
                        .Select(s => long.TryParse(s.OriginRefId, out var v) ? v : 0L)
                        .Where(v => v != 0L).Distinct().ToList();
        var deliveredShipmentIds = refs.Count == 0
            ? new HashSet<long>()
            : (await db.MeliShipments
                    .Where(m => refs.Contains(m.MeliShipmentId) && m.Status == "delivered")
                    .Select(m => m.MeliShipmentId).ToListAsync())
              .ToHashSet();

        bool IsDelivered(MapeoStop s)
        {
            if (string.Equals(s.InternalStatus, "entregado", System.StringComparison.OrdinalIgnoreCase)) return true;
            if ((s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId != null
                && long.TryParse(s.OriginRefId, out var sid) && deliveredShipmentIds.Contains(sid)) return true;
            return false;
        }

        var stopsArr = stops.Select(s => new
        {
            id = s.Id,
            origin = s.Origin,
            originRefId = s.OriginRefId,
            alias = s.Alias,
            direccion = s.Direccion,
            latitude = s.Latitude,
            longitude = s.Longitude,
            contactName = s.ContactName,
            telefono = s.Telefono,
            notas = s.Notas,
            internalStatus = s.InternalStatus,
            delivered = IsDelivered(s),
            assignedDriverId = s.AssignedDriverId,
            assignedDriverName = s.AssignedDriver?.Nombre,
            assignedDriverColor = s.AssignedDriver?.Color,
            assignedVehicleSlot = s.AssignedVehicleSlot,
            orderInRoute = s.OrderInRoute
        }).ToList();

        int vehicles = stops.Where(s => s.AssignedVehicleSlot.HasValue)
            .Select(s => s.AssignedVehicleSlot).Distinct().Count();
        int drivers = stops.Where(s => s.AssignedDriverId.HasValue)
            .Select(s => s.AssignedDriverId).Distinct().Count();

        var local = DateTime.UtcNow.AddHours(-3);
        var title = $"Ruta {local:dd/MM/yyyy HH:mm} · {vehicles} V · {stops.Count} paradas";

        return new MapeoRouteSnapshot
        {
            Title = title,
            StopsCount = stops.Count,
            VehiclesCount = vehicles,
            DriversCount = drivers,
            StopsJson = JsonSerializer.Serialize(stopsArr),
            CreatedAt = DateTime.UtcNow,
            CreatedByUsername = username,
            Notes = notes
        };
    }

    private async Task<MapeoRouteSnapshot?> BuildSnapshotAsync(string? notes, string? username)
        => await BuildSnapshotAsync(_db, notes, username);
}
