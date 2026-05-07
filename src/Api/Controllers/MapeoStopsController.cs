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
