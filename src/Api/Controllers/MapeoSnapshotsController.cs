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
        DateTime CreatedAt, string? CreatedByUsername, string? Notes);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int days = 30)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var list = await _db.MapeoRouteSnapshots
            .Where(s => s.CreatedAt >= since)
            .OrderByDescending(s => s.CreatedAt)
            .Take(200)
            .Select(s => new SnapshotListItemDto(s.Id, s.Title, s.StopsCount, s.VehiclesCount, s.DriversCount,
                s.CreatedAt, s.CreatedByUsername, s.Notes))
            .ToListAsync();
        return Ok(list);
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
