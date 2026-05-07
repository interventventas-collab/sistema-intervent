using Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Endpoints publicos (sin autenticacion) para que los choferes accedan a su ruta.
/// Acceso por ShareToken — pensado para abrir desde el celular del repartidor.
/// </summary>
[ApiController]
[Route("api/public/route")]
[AllowAnonymous]
public class MapeoPublicController : ControllerBase
{
    private readonly AppDbContext _db;
    public MapeoPublicController(AppDbContext db) { _db = db; }

    public record PublicStopDto(int Id, int? OrderInRoute, string? Alias, string Direccion,
        decimal Latitude, decimal Longitude,
        string? ContactName, string? Telefono, string? Notas,
        string InternalStatus, string? Comprador, string? NumeroVenta);

    public record PublicRouteDto(string DriverNombre, string DriverColor, string? DriverTelefono,
        DateTime? Now, string? StartAddress, decimal? StartLat, decimal? StartLng,
        List<PublicStopDto> Stops);

    [HttpGet("{token}")]
    public async Task<IActionResult> Get(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return NotFound();
        var driver = await _db.MapeoDrivers.FirstOrDefaultAsync(d => d.ShareToken == token);
        if (driver is null) return NotFound(new { error = "Token invalido" });

        var stops = await _db.MapeoStops
            .Where(s => s.AssignedDriverId == driver.Id)
            .OrderBy(s => s.OrderInRoute ?? int.MaxValue).ThenBy(s => s.Id)
            .ToListAsync();

        // Datos extra de los Flex (Comprador / numero de venta) que están en MeliShipments.
        var flexRefs = stops.Where(s => s.Origin == "flex" && s.OriginRefId != null)
            .Select(s => long.TryParse(s.OriginRefId, out var v) ? v : 0L).Where(x => x > 0).ToList();
        var flexInfo = await _db.MeliShipments
            .Where(x => flexRefs.Contains(x.MeliShipmentId))
            .Select(x => new { x.MeliShipmentId, x.BuyerNickname, x.MeliOrderId })
            .ToListAsync();
        var flexMap = flexInfo.ToDictionary(x => x.MeliShipmentId, x => (Buyer: x.BuyerNickname, Order: x.MeliOrderId));

        var publicStops = stops.Select(s =>
        {
            string? buyer = null, order = null;
            if (s.Origin == "flex" && long.TryParse(s.OriginRefId, out var sid) && flexMap.TryGetValue(sid, out var info))
            {
                buyer = info.Buyer;
                order = info.Order?.ToString();
            }
            return new PublicStopDto(s.Id, s.OrderInRoute, s.Alias, s.Direccion,
                s.Latitude, s.Longitude, s.ContactName, s.Telefono, s.Notas, s.InternalStatus,
                buyer, order);
        }).ToList();

        // Punto de partida (común para todos los choferes)
        string? startAddr = (await _db.AppSettings.FindAsync("mapeo.start.address"))?.Value;
        var latStr = (await _db.AppSettings.FindAsync("mapeo.start.lat"))?.Value;
        var lngStr = (await _db.AppSettings.FindAsync("mapeo.start.lng"))?.Value;
        decimal? sLat = decimal.TryParse(latStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var la) ? la : null;
        decimal? sLng = decimal.TryParse(lngStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lo) ? lo : null;

        return Ok(new PublicRouteDto(driver.Nombre, driver.Color, driver.Telefono,
            DateTime.UtcNow, startAddr, sLat, sLng, publicStops));
    }

    public record UpdateStopStatusRequest(string InternalStatus, string? Notes);

    [HttpPost("{token}/stop/{stopId:int}")]
    public async Task<IActionResult> UpdateStopStatus(string token, int stopId, [FromBody] UpdateStopStatusRequest req)
    {
        if (string.IsNullOrWhiteSpace(token)) return NotFound();
        var driver = await _db.MapeoDrivers.FirstOrDefaultAsync(d => d.ShareToken == token);
        if (driver is null) return NotFound(new { error = "Token invalido" });

        var stop = await _db.MapeoStops.FirstOrDefaultAsync(s => s.Id == stopId && s.AssignedDriverId == driver.Id);
        if (stop is null) return NotFound(new { error = "Parada no pertenece a este chofer" });

        stop.InternalStatus = string.IsNullOrWhiteSpace(req.InternalStatus) ? "pending" : req.InternalStatus.Trim().ToLower();
        if (req.Notes is not null) stop.Notas = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
        stop.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, internalStatus = stop.InternalStatus });
    }
}
