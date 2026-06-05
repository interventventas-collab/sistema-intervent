using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>2026-06-05: CRUD admin del catalogo de Servicios (envio, mano de obra, etc).
/// Se cargan en /cafe/servicios y aparecen en el botón "Servicio" de Nueva Venta.</summary>
[ApiController]
[Route("api/cafe/servicios")]
[Authorize]
public class CafeServiciosController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafeServiciosController(AppDbContext db) { _db = db; }

    public record ServicioDto(int Id, string Nombre, string? Descripcion, decimal Precio, decimal IvaPct, bool IsActive);

    public class UpsertRequest
    {
        public string Nombre { get; set; } = "";
        public string? Descripcion { get; set; }
        public decimal Precio { get; set; }
        public decimal? IvaPct { get; set; }
        public bool IsActive { get; set; } = true;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool incluirInactivos = false)
    {
        var q = _db.CafeServicios.AsQueryable();
        if (!incluirInactivos) q = q.Where(s => s.IsActive);
        var l = await q.OrderBy(s => s.Nombre)
            .Select(s => new ServicioDto(s.Id, s.Nombre, s.Descripcion, s.Precio, s.IvaPct, s.IsActive))
            .ToListAsync();
        return Ok(l);
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] UpsertRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { error = "Nombre requerido" });
        if (req.Precio < 0) return BadRequest(new { error = "Precio invalido" });
        var s = new CafeServicio
        {
            Nombre = req.Nombre.Trim(),
            Descripcion = string.IsNullOrWhiteSpace(req.Descripcion) ? null : req.Descripcion.Trim(),
            Precio = req.Precio,
            IvaPct = req.IvaPct ?? 21m,
            IsActive = req.IsActive,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeServicios.Add(s);
        await _db.SaveChangesAsync();
        return Ok(new ServicioDto(s.Id, s.Nombre, s.Descripcion, s.Precio, s.IvaPct, s.IsActive));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Editar(int id, [FromBody] UpsertRequest req)
    {
        var s = await _db.CafeServicios.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(req.Nombre)) s.Nombre = req.Nombre.Trim();
        s.Descripcion = string.IsNullOrWhiteSpace(req.Descripcion) ? null : req.Descripcion.Trim();
        if (req.Precio >= 0) s.Precio = req.Precio;
        if (req.IvaPct.HasValue) s.IvaPct = req.IvaPct.Value;
        s.IsActive = req.IsActive;
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ServicioDto(s.Id, s.Nombre, s.Descripcion, s.Precio, s.IvaPct, s.IsActive));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Borrar(int id)
    {
        var s = await _db.CafeServicios.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();
        // Si hay items que lo usan, soft delete (lo desactivamos pero no lo borramos)
        var enUso = await _db.Set<CafeVentaItem>().AnyAsync(i => i.ServicioId == id);
        if (enUso)
        {
            s.IsActive = false;
            s.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(new { soft = true, mensaje = "Servicio desactivado (tiene ventas asociadas, no se puede borrar)" });
        }
        _db.CafeServicios.Remove(s);
        await _db.SaveChangesAsync();
        return Ok(new { soft = false });
    }
}
