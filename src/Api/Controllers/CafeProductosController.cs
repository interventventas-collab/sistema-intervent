using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/productos")]
[Authorize]
public class CafeProductosController : ControllerBase
{
    private readonly AppDbContext _db;
    private static readonly string[] CategoriasValidas = { "CAFE", "OTROS" };

    public CafeProductosController(AppDbContext db) { _db = db; }

    private static CafeProductoDto Map(CafeProducto p) => new(
        p.Id, p.Sku, p.Barcode,
        p.Nombre, p.Categoria, p.Marca,
        p.Costo, p.PrecioPorKg,
        p.Pvp1, p.Pvp2,
        p.BarPctSobreCosto, p.UxB,
        p.OemId, p.OemNav?.Codigo,
        p.StockGramos, p.StockUnidades,
        p.Notas, p.IsActive, p.CreatedAt, p.UpdatedAt);

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? categoria = null)
    {
        var q = _db.CafeProductos.Include(p => p.OemNav).AsQueryable();
        if (!string.IsNullOrWhiteSpace(categoria))
        {
            var c = NormCat(categoria);
            q = q.Where(p => p.Categoria == c);
        }
        var list = await q.OrderBy(p => p.Categoria).ThenBy(p => p.Nombre).ToListAsync();
        return Ok(list.Select(Map).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var p = await _db.CafeProductos.Include(x => x.OemNav).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound(new { error = "Producto no encontrado" });
        return Ok(Map(p));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCafeProductoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { error = "El nombre es obligatorio" });
        if (req.Costo < 0) return BadRequest(new { error = "El costo no puede ser negativo" });
        var cat = NormCat(req.Categoria);

        // OTROS exige PVP (Pvp2) cargado a mano
        if (cat == "OTROS" && (!req.Pvp2.HasValue || req.Pvp2.Value < 0))
            return BadRequest(new { error = "Para productos OTROS el PVP es obligatorio" });

        var p = new CafeProducto
        {
            Sku = string.IsNullOrWhiteSpace(req.Sku) ? null : req.Sku.Trim().ToUpperInvariant(),
            Barcode = string.IsNullOrWhiteSpace(req.Barcode) ? null : req.Barcode.Trim(),
            Nombre = req.Nombre.Trim(),
            Categoria = cat,
            Marca = string.IsNullOrWhiteSpace(req.Marca) ? null : req.Marca.Trim(),
            Costo = req.Costo,
            PrecioPorKg = req.PrecioPorKg,
            Pvp1 = req.Pvp1,
            Pvp2 = req.Pvp2,
            BarPctSobreCosto = cat == "OTROS" ? req.BarPctSobreCosto : null,
            UxB = cat == "OTROS" ? req.UxB : null,
            OemId = cat == "OTROS" ? req.OemId : null,
            StockGramos = Math.Max(0m, req.StockGramos ?? 0m),
            StockUnidades = Math.Max(0, req.StockUnidades ?? 0),
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeProductos.Add(p);
        await _db.SaveChangesAsync();
        return Ok(Map(p));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCafeProductoRequest req)
    {
        var p = await _db.CafeProductos.FindAsync(id);
        if (p is null) return NotFound(new { error = "Producto no encontrado" });
        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "El nombre no puede ser vacio" });
            p.Nombre = req.Nombre.Trim();
        }
        if (req.Sku is not null) p.Sku = string.IsNullOrWhiteSpace(req.Sku) ? null : req.Sku.Trim().ToUpperInvariant();
        if (req.Barcode is not null) p.Barcode = string.IsNullOrWhiteSpace(req.Barcode) ? null : req.Barcode.Trim();
        if (req.Categoria is not null) p.Categoria = NormCat(req.Categoria);
        if (req.Marca is not null) p.Marca = string.IsNullOrWhiteSpace(req.Marca) ? null : req.Marca.Trim();
        if (req.Costo.HasValue)
        {
            if (req.Costo.Value < 0) return BadRequest(new { error = "El costo no puede ser negativo" });
            p.Costo = req.Costo.Value;
        }
        if (req.PrecioPorKg.HasValue) p.PrecioPorKg = req.PrecioPorKg.Value;
        if (req.Pvp1.HasValue) p.Pvp1 = req.Pvp1.Value;
        if (req.Pvp2.HasValue) p.Pvp2 = req.Pvp2.Value;
        if (req.BarPctSobreCosto.HasValue) p.BarPctSobreCosto = req.BarPctSobreCosto.Value;
        else if (req.ClearBarPctSobreCosto) p.BarPctSobreCosto = null;
        if (req.UxB.HasValue) p.UxB = req.UxB.Value;
        else if (req.ClearUxB) p.UxB = null;
        if (req.OemId.HasValue) p.OemId = req.OemId.Value;
        else if (req.ClearOemId) p.OemId = null;
        if (req.StockGramos.HasValue) p.StockGramos = Math.Max(0m, req.StockGramos.Value);
        if (req.StockUnidades.HasValue) p.StockUnidades = Math.Max(0, req.StockUnidades.Value);
        if (req.Notas is not null) p.Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim();
        if (req.IsActive.HasValue) p.IsActive = req.IsActive.Value;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(p));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var p = await _db.CafeProductos.FindAsync(id);
        if (p is null) return NotFound(new { error = "Producto no encontrado" });
        _db.CafeProductos.Remove(p);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    private static string NormCat(string? c)
    {
        if (string.IsNullOrWhiteSpace(c)) return "CAFE";
        var v = c.Trim().ToUpperInvariant();
        return CategoriasValidas.Contains(v) ? v : "CAFE";
    }
}
