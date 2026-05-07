using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/mapeo")]
[Authorize]
public class MapeoDriversController : ControllerBase
{
    private readonly AppDbContext _db;
    public MapeoDriversController(AppDbContext db) { _db = db; }

    public record DriverDto(int Id, string Nombre, string? Telefono, string Color, bool IsActive);
    public record CreateDriverRequest(string Nombre, string? Telefono, string? Color);
    public record UpdateDriverRequest(string? Nombre, string? Telefono, string? Color, bool? IsActive);

    [HttpGet("drivers")]
    public async Task<IActionResult> ListDrivers()
    {
        var list = await _db.MapeoDrivers.OrderBy(d => d.Nombre).ToListAsync();
        return Ok(list.Select(d => new DriverDto(d.Id, d.Nombre, d.Telefono, d.Color, d.IsActive)));
    }

    [HttpPost("drivers")]
    public async Task<IActionResult> CreateDriver([FromBody] CreateDriverRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre obligatorio" });
        var d = new MapeoDriver
        {
            Nombre = req.Nombre.Trim(),
            Telefono = string.IsNullOrWhiteSpace(req.Telefono) ? null : req.Telefono.Trim(),
            Color = string.IsNullOrWhiteSpace(req.Color) ? "#1d4ed8" : req.Color.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.MapeoDrivers.Add(d);
        await _db.SaveChangesAsync();
        return Ok(new DriverDto(d.Id, d.Nombre, d.Telefono, d.Color, d.IsActive));
    }

    [HttpPut("drivers/{id:int}")]
    public async Task<IActionResult> UpdateDriver(int id, [FromBody] UpdateDriverRequest req)
    {
        var d = await _db.MapeoDrivers.FindAsync(id);
        if (d is null) return NotFound(new { error = "Repartidor no encontrado" });
        if (req.Nombre is not null) d.Nombre = req.Nombre.Trim();
        if (req.Telefono is not null) d.Telefono = string.IsNullOrWhiteSpace(req.Telefono) ? null : req.Telefono.Trim();
        if (req.Color is not null) d.Color = string.IsNullOrWhiteSpace(req.Color) ? "#1d4ed8" : req.Color.Trim();
        if (req.IsActive.HasValue) d.IsActive = req.IsActive.Value;
        d.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new DriverDto(d.Id, d.Nombre, d.Telefono, d.Color, d.IsActive));
    }

    [HttpDelete("drivers/{id:int}")]
    public async Task<IActionResult> DeleteDriver(int id)
    {
        var d = await _db.MapeoDrivers.FindAsync(id);
        if (d is null) return NotFound(new { error = "Repartidor no encontrado" });
        _db.MapeoDrivers.Remove(d);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ===== Favoritos =====
    public record FavoritoDto(int Id, string Alias, string Direccion, decimal Latitude, decimal Longitude,
        string? ContactName, string? Telefono, string? Notas, bool IsActive);
    public record CreateFavoritoRequest(string Alias, string Direccion, decimal Latitude, decimal Longitude,
        string? ContactName, string? Telefono, string? Notas);
    public record UpdateFavoritoRequest(string? Alias, string? Direccion, decimal? Latitude, decimal? Longitude,
        string? ContactName, string? Telefono, string? Notas, bool? IsActive);

    [HttpGet("favoritos")]
    public async Task<IActionResult> ListFavoritos([FromQuery] string? q = null)
    {
        var query = _db.MapeoFavoritos.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var n = q.Trim().ToLower();
            query = query.Where(f => f.Alias.ToLower().Contains(n) || f.Direccion.ToLower().Contains(n)
                                  || (f.ContactName != null && f.ContactName.ToLower().Contains(n)));
        }
        var list = await query.OrderBy(f => f.Alias).ToListAsync();
        return Ok(list.Select(f => new FavoritoDto(f.Id, f.Alias, f.Direccion, f.Latitude, f.Longitude,
            f.ContactName, f.Telefono, f.Notas, f.IsActive)));
    }

    [HttpPost("favoritos")]
    public async Task<IActionResult> CreateFavorito([FromBody] CreateFavoritoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Alias)) return BadRequest(new { error = "Alias obligatorio" });
        if (string.IsNullOrWhiteSpace(req.Direccion)) return BadRequest(new { error = "Dirección obligatoria" });
        var f = new MapeoFavorito
        {
            Alias = req.Alias.Trim(),
            Direccion = req.Direccion.Trim(),
            Latitude = req.Latitude,
            Longitude = req.Longitude,
            ContactName = string.IsNullOrWhiteSpace(req.ContactName) ? null : req.ContactName.Trim(),
            Telefono = string.IsNullOrWhiteSpace(req.Telefono) ? null : req.Telefono.Trim(),
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.MapeoFavoritos.Add(f);
        await _db.SaveChangesAsync();
        return Ok(new FavoritoDto(f.Id, f.Alias, f.Direccion, f.Latitude, f.Longitude,
            f.ContactName, f.Telefono, f.Notas, f.IsActive));
    }

    [HttpPut("favoritos/{id:int}")]
    public async Task<IActionResult> UpdateFavorito(int id, [FromBody] UpdateFavoritoRequest req)
    {
        var f = await _db.MapeoFavoritos.FindAsync(id);
        if (f is null) return NotFound(new { error = "Favorito no encontrado" });
        if (req.Alias is not null) f.Alias = req.Alias.Trim();
        if (req.Direccion is not null) f.Direccion = req.Direccion.Trim();
        if (req.Latitude.HasValue) f.Latitude = req.Latitude.Value;
        if (req.Longitude.HasValue) f.Longitude = req.Longitude.Value;
        if (req.ContactName is not null) f.ContactName = string.IsNullOrWhiteSpace(req.ContactName) ? null : req.ContactName.Trim();
        if (req.Telefono is not null) f.Telefono = string.IsNullOrWhiteSpace(req.Telefono) ? null : req.Telefono.Trim();
        if (req.Notas is not null) f.Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim();
        if (req.IsActive.HasValue) f.IsActive = req.IsActive.Value;
        f.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new FavoritoDto(f.Id, f.Alias, f.Direccion, f.Latitude, f.Longitude,
            f.ContactName, f.Telefono, f.Notas, f.IsActive));
    }

    [HttpDelete("favoritos/{id:int}")]
    public async Task<IActionResult> DeleteFavorito(int id)
    {
        var f = await _db.MapeoFavoritos.FindAsync(id);
        if (f is null) return NotFound(new { error = "Favorito no encontrado" });
        _db.MapeoFavoritos.Remove(f);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
