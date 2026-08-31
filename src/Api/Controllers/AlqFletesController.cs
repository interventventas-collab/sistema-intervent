using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// ABM de precios de flete por zona. Los usa la calculadora de alquileres
/// para cotizar sin tener que acordarse el valor de cada localidad. 2026-08-31.
/// </summary>
[ApiController]
[Route("api/alquileres/fletes")]
[Authorize]
public class AlqFletesController : ControllerBase
{
    private readonly AppDbContext _db;

    public AlqFletesController(AppDbContext db) { _db = db; }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] bool soloActivos = false)
    {
        var q = _db.AlqFletes.AsNoTracking().AsQueryable();
        if (soloActivos) q = q.Where(f => f.IsActive);
        var list = await q
            .OrderBy(f => f.Zona)
            .Select(f => new AlqFleteDto(f.Id, f.Zona, f.Precio, f.Notas, f.IsActive, f.CreatedAt, f.UpdatedAt))
            .ToListAsync();
        return Ok(list);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAlqFleteRequest req)
    {
        var zona = (req.Zona ?? "").Trim();
        if (string.IsNullOrWhiteSpace(zona))
            return BadRequest(new { error = "Poné la zona o localidad" });
        if (await _db.AlqFletes.AnyAsync(x => x.Zona == zona))
            return BadRequest(new { error = $"Ya tenés un flete cargado para '{zona}'" });
        if (req.Precio < 0)
            return BadRequest(new { error = "El precio no puede ser negativo" });

        var f = new AlqFlete
        {
            Zona = zona,
            Precio = req.Precio,
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.AlqFletes.Add(f);
        await _db.SaveChangesAsync();
        return Ok(new AlqFleteDto(f.Id, f.Zona, f.Precio, f.Notas, f.IsActive, f.CreatedAt, f.UpdatedAt));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateAlqFleteRequest req)
    {
        var f = await _db.AlqFletes.FindAsync(id);
        if (f is null) return NotFound(new { error = "Flete no encontrado" });

        if (req.Zona is not null)
        {
            var zona = req.Zona.Trim();
            if (string.IsNullOrWhiteSpace(zona)) return BadRequest(new { error = "La zona no puede quedar vacia" });
            if (zona != f.Zona && await _db.AlqFletes.AnyAsync(x => x.Zona == zona && x.Id != id))
                return BadRequest(new { error = $"Ya tenés un flete cargado para '{zona}'" });
            f.Zona = zona;
        }
        if (req.Precio.HasValue)
        {
            if (req.Precio.Value < 0) return BadRequest(new { error = "El precio no puede ser negativo" });
            f.Precio = req.Precio.Value;
        }
        if (req.Notas is not null) f.Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim();
        if (req.IsActive.HasValue) f.IsActive = req.IsActive.Value;
        f.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new AlqFleteDto(f.Id, f.Zona, f.Precio, f.Notas, f.IsActive, f.CreatedAt, f.UpdatedAt));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var f = await _db.AlqFletes.FindAsync(id);
        if (f is null) return NotFound(new { error = "Flete no encontrado" });
        _db.AlqFletes.Remove(f);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }
}
