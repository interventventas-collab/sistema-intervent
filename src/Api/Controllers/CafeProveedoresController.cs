using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/proveedores")]
[Authorize]
public class CafeProveedoresController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafeProveedoresController(AppDbContext db) { _db = db; }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] bool? activos = null)
    {
        var q = _db.CafeProveedores.AsQueryable();
        if (activos == true) q = q.Where(p => p.IsActive);
        var list = await q.OrderBy(p => p.Nombre).ToListAsync();

        var ids = list.Select(p => p.Id).ToList();
        var stats = await _db.CafeCompras
            .Where(c => c.ProveedorId.HasValue && ids.Contains(c.ProveedorId.Value) && c.Estado != "ANULADA")
            .GroupBy(c => c.ProveedorId!.Value)
            .Select(g => new { ProveedorId = g.Key, N = g.Count(), Total = g.Sum(x => x.Total) })
            .ToDictionaryAsync(x => x.ProveedorId);

        return Ok(list.Select(p => Map(p,
            stats.TryGetValue(p.Id, out var s) ? s.N : 0,
            stats.TryGetValue(p.Id, out var s2) ? s2.Total : 0m)).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var p = await _db.CafeProveedores.FindAsync(id);
        if (p is null) return NotFound(new { error = "Proveedor no encontrado" });
        var n = await _db.CafeCompras.CountAsync(c => c.ProveedorId == id && c.Estado != "ANULADA");
        var total = await _db.CafeCompras.Where(c => c.ProveedorId == id && c.Estado != "ANULADA").SumAsync(c => (decimal?)c.Total) ?? 0m;
        return Ok(Map(p, n, total));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCafeProveedorRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "El nombre es obligatorio" });
        var p = new CafeProveedor
        {
            Nombre = req.Nombre.Trim(),
            Contacto = NullIfEmpty(req.Contacto),
            Telefono = NullIfEmpty(req.Telefono),
            Email = NullIfEmpty(req.Email),
            Notas = NullIfEmpty(req.Notas),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeProveedores.Add(p);
        await _db.SaveChangesAsync();
        return Ok(Map(p, 0, 0m));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCafeProveedorRequest req)
    {
        var p = await _db.CafeProveedores.FindAsync(id);
        if (p is null) return NotFound(new { error = "Proveedor no encontrado" });
        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "El nombre no puede estar vacio" });
            p.Nombre = req.Nombre.Trim();
        }
        if (req.Contacto is not null) p.Contacto = NullIfEmpty(req.Contacto);
        if (req.Telefono is not null) p.Telefono = NullIfEmpty(req.Telefono);
        if (req.Email is not null) p.Email = NullIfEmpty(req.Email);
        if (req.Notas is not null) p.Notas = NullIfEmpty(req.Notas);
        if (req.IsActive.HasValue) p.IsActive = req.IsActive.Value;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        var n = await _db.CafeCompras.CountAsync(c => c.ProveedorId == id && c.Estado != "ANULADA");
        var total = await _db.CafeCompras.Where(c => c.ProveedorId == id && c.Estado != "ANULADA").SumAsync(c => (decimal?)c.Total) ?? 0m;
        return Ok(Map(p, n, total));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var p = await _db.CafeProveedores.FindAsync(id);
        if (p is null) return NotFound(new { error = "Proveedor no encontrado" });
        // Si tiene compras asociadas, no se borra: se desactiva. Asi se preserva el historial.
        var hasCompras = await _db.CafeCompras.AnyAsync(c => c.ProveedorId == id);
        if (hasCompras)
        {
            p.IsActive = false;
            p.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(new { deleted = false, deactivated = true, message = "Proveedor con compras: se desactivo en lugar de eliminar" });
        }
        _db.CafeProveedores.Remove(p);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    private static CafeProveedorDto Map(CafeProveedor p, int comprasCount, decimal totalComprado) => new(
        p.Id, p.Nombre, p.Contacto, p.Telefono, p.Email, p.Notas,
        p.IsActive, p.CreatedAt, p.UpdatedAt, comprasCount, totalComprado);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
