using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/alquileres/equipos")]
[Authorize]
public class AlqEquiposController : ControllerBase
{
    private readonly AppDbContext _db;

    public AlqEquiposController(AppDbContext db) { _db = db; }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        // 2026-08-31: manda el orden a mano (Alquileres → Equipos, flechitas ↑↓).
        // Los que nunca se ordenaron (Orden = 0) van al final, alfabéticos.
        var list = await _db.AlqEquipos
            .OrderBy(e => e.Orden == 0 ? 1 : 0)
            .ThenBy(e => e.Orden)
            .ThenBy(e => e.Nombre)
            .Select(e => new AlqEquipoDto(
                e.Id, e.Sku, e.Nombre, e.Categoria, e.Descripcion,
                e.StockTotal, e.PrecioDiario, e.PrecioReposicion,
                e.IsActive, e.CreatedAt, e.UpdatedAt, e.Orden))
            .ToListAsync();
        return Ok(list);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var e = await _db.AlqEquipos.FindAsync(id);
        if (e is null) return NotFound(new { error = "Equipo no encontrado" });
        return Ok(new AlqEquipoDto(e.Id, e.Sku, e.Nombre, e.Categoria, e.Descripcion,
            e.StockTotal, e.PrecioDiario, e.PrecioReposicion, e.IsActive, e.CreatedAt, e.UpdatedAt, e.Orden));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAlqEquipoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { error = "El nombre es obligatorio" });

        var sku = string.IsNullOrWhiteSpace(req.Sku)
            ? await GenerateNextSkuAsync()
            : req.Sku.Trim().ToUpperInvariant();

        if (await _db.AlqEquipos.AnyAsync(x => x.Sku == sku))
            return BadRequest(new { error = $"Ya existe un equipo con SKU '{sku}'" });

        var e = new AlqEquipo
        {
            Sku = sku,
            Nombre = req.Nombre.Trim(),
            Categoria = string.IsNullOrWhiteSpace(req.Categoria) ? null : req.Categoria.Trim(),
            Descripcion = string.IsNullOrWhiteSpace(req.Descripcion) ? null : req.Descripcion.Trim(),
            StockTotal = Math.Max(0, req.StockTotal),
            PrecioDiario = req.PrecioDiario,
            PrecioReposicion = req.PrecioReposicion,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.AlqEquipos.Add(e);
        await _db.SaveChangesAsync();
        return Ok(new AlqEquipoDto(e.Id, e.Sku, e.Nombre, e.Categoria, e.Descripcion,
            e.StockTotal, e.PrecioDiario, e.PrecioReposicion, e.IsActive, e.CreatedAt, e.UpdatedAt, e.Orden));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateAlqEquipoRequest req)
    {
        var e = await _db.AlqEquipos.FindAsync(id);
        if (e is null) return NotFound(new { error = "Equipo no encontrado" });

        if (req.Sku is not null)
        {
            var newSku = req.Sku.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(newSku)) return BadRequest(new { error = "El SKU no puede ser vacio" });
            if (newSku != e.Sku && await _db.AlqEquipos.AnyAsync(x => x.Sku == newSku && x.Id != id))
                return BadRequest(new { error = $"Ya existe un equipo con SKU '{newSku}'" });
            e.Sku = newSku;
        }
        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "El nombre no puede ser vacio" });
            e.Nombre = req.Nombre.Trim();
        }
        if (req.Categoria is not null) e.Categoria = string.IsNullOrWhiteSpace(req.Categoria) ? null : req.Categoria.Trim();
        if (req.Descripcion is not null) e.Descripcion = string.IsNullOrWhiteSpace(req.Descripcion) ? null : req.Descripcion.Trim();
        if (req.StockTotal.HasValue) e.StockTotal = Math.Max(0, req.StockTotal.Value);
        if (req.PrecioDiario.HasValue) e.PrecioDiario = req.PrecioDiario.Value;
        if (req.PrecioReposicion.HasValue) e.PrecioReposicion = req.PrecioReposicion.Value;
        if (req.IsActive.HasValue) e.IsActive = req.IsActive.Value;
        if (req.Orden.HasValue) e.Orden = Math.Max(0, req.Orden.Value);
        e.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new AlqEquipoDto(e.Id, e.Sku, e.Nombre, e.Categoria, e.Descripcion,
            e.StockTotal, e.PrecioDiario, e.PrecioReposicion, e.IsActive, e.CreatedAt, e.UpdatedAt, e.Orden));
    }

    /// <summary>2026-08-31: guarda el orden de la lista de una sola vez (los ids en el orden final).
    /// Lo usa el botón de las flechitas de Alquileres → Equipos.</summary>
    [HttpPut("orden")]
    public async Task<IActionResult> GuardarOrden([FromBody] GuardarOrdenEquiposRequest req)
    {
        if (req.Ids is null || req.Ids.Count == 0) return BadRequest(new { error = "No mandaste ningún equipo" });
        var equipos = await _db.AlqEquipos.Where(e => req.Ids.Contains(e.Id)).ToListAsync();
        for (int i = 0; i < req.Ids.Count; i++)
        {
            var eq = equipos.FirstOrDefault(x => x.Id == req.Ids[i]);
            if (eq is null) continue;
            eq.Orden = i + 1;          // 1, 2, 3… (0 queda reservado para "sin ordenar")
            eq.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, ordenados = equipos.Count });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var e = await _db.AlqEquipos.FindAsync(id);
        if (e is null) return NotFound(new { error = "Equipo no encontrado" });
        _db.AlqEquipos.Remove(e);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    private async Task<string> GenerateNextSkuAsync()
    {
        // Genera ALQ-001, ALQ-002, ...
        var existing = await _db.AlqEquipos
            .Where(x => x.Sku.StartsWith("ALQ-"))
            .Select(x => x.Sku)
            .ToListAsync();
        int max = 0;
        foreach (var s in existing)
        {
            if (int.TryParse(s.Substring(4), out var n) && n > max) max = n;
        }
        return $"ALQ-{(max + 1):D3}";
    }
}
