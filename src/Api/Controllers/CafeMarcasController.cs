using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/marcas")]
[Authorize]
public class CafeMarcasController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafeMarcasController(AppDbContext db) { _db = db; }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] bool? activos = null, [FromQuery] int? proveedorId = null)
    {
        var q = _db.CafeMarcas.Include(m => m.ProveedorNav).AsQueryable();
        if (activos == true) q = q.Where(m => m.IsActive);
        if (proveedorId.HasValue) q = q.Where(m => m.ProveedorId == proveedorId.Value);
        var list = await q.OrderBy(m => m.Nombre).ToListAsync();

        var ids = list.Select(m => m.Id).ToList();
        var prodCounts = await _db.CafeProductos
            .Where(p => p.MarcaId != null && ids.Contains(p.MarcaId.Value))
            .GroupBy(p => p.MarcaId!.Value)
            .Select(g => new { Id = g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.N);
        var oemCounts = await _db.CafeOems
            .Where(o => o.MarcaId != null && ids.Contains(o.MarcaId.Value))
            .GroupBy(o => o.MarcaId!.Value)
            .Select(g => new { Id = g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.N);

        return Ok(list.Select(m => Map(m,
            prodCounts.GetValueOrDefault(m.Id, 0),
            oemCounts.GetValueOrDefault(m.Id, 0))).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var m = await _db.CafeMarcas.Include(x => x.ProveedorNav).FirstOrDefaultAsync(x => x.Id == id);
        if (m is null) return NotFound(new { error = "Marca no encontrada" });
        var prodN = await _db.CafeProductos.CountAsync(p => p.MarcaId == id);
        var oemN = await _db.CafeOems.CountAsync(o => o.MarcaId == id);
        return Ok(Map(m, prodN, oemN));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCafeMarcaRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "El nombre es obligatorio" });
        var nombre = req.Nombre.Trim();
        if (await _db.CafeMarcas.AnyAsync(x => x.Nombre == nombre))
            return BadRequest(new { error = $"Ya existe una marca con el nombre '{nombre}'" });
        if (req.ProveedorId.HasValue && req.ProveedorId.Value > 0
            && !await _db.CafeProveedores.AnyAsync(p => p.Id == req.ProveedorId.Value))
            return BadRequest(new { error = "Proveedor no encontrado" });

        var m = new CafeMarca
        {
            Nombre = nombre,
            ProveedorId = req.ProveedorId.HasValue && req.ProveedorId.Value > 0 ? req.ProveedorId.Value : null,
            Notas = NullIfEmpty(req.Notas),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeMarcas.Add(m);
        await _db.SaveChangesAsync();

        var saved = await _db.CafeMarcas.Include(x => x.ProveedorNav).FirstAsync(x => x.Id == m.Id);
        return Ok(Map(saved, 0, 0));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCafeMarcaRequest req)
    {
        var m = await _db.CafeMarcas.FindAsync(id);
        if (m is null) return NotFound(new { error = "Marca no encontrada" });
        bool nombreChanged = false;
        if (req.Nombre is not null)
        {
            var nuevo = req.Nombre.Trim();
            if (string.IsNullOrEmpty(nuevo)) return BadRequest(new { error = "El nombre no puede estar vacio" });
            if (nuevo != m.Nombre && await _db.CafeMarcas.AnyAsync(x => x.Nombre == nuevo && x.Id != id))
                return BadRequest(new { error = $"Ya existe otra marca con el nombre '{nuevo}'" });
            if (nuevo != m.Nombre) nombreChanged = true;
            m.Nombre = nuevo;
        }
        if (req.ProveedorId.HasValue && req.ProveedorId.Value > 0)
        {
            if (!await _db.CafeProveedores.AnyAsync(p => p.Id == req.ProveedorId.Value))
                return BadRequest(new { error = "Proveedor no encontrado" });
            m.ProveedorId = req.ProveedorId.Value;
        }
        else if (req.ClearProveedor)
        {
            m.ProveedorId = null;
        }
        if (req.Notas is not null) m.Notas = NullIfEmpty(req.Notas);
        if (req.IsActive.HasValue) m.IsActive = req.IsActive.Value;
        if (req.BloqueaDescuento.HasValue) m.BloqueaDescuento = req.BloqueaDescuento.Value;
        m.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Si el nombre cambio, propagar a los productos y OEMs vinculados (sincroniza el campo texto cacheado).
        if (nombreChanged)
        {
            var ahora = DateTime.UtcNow;
            var prods = await _db.CafeProductos.Where(p => p.MarcaId == id).ToListAsync();
            foreach (var p in prods) { p.Marca = m.Nombre; p.UpdatedAt = ahora; }
            var oems = await _db.CafeOems.Where(o => o.MarcaId == id).ToListAsync();
            foreach (var o in oems) { o.Marca = m.Nombre; o.UpdatedAt = ahora; }
            if (prods.Count > 0 || oems.Count > 0) await _db.SaveChangesAsync();
        }

        var saved = await _db.CafeMarcas.Include(x => x.ProveedorNav).FirstAsync(x => x.Id == m.Id);
        var prodN = await _db.CafeProductos.CountAsync(p => p.MarcaId == id);
        var oemN = await _db.CafeOems.CountAsync(o => o.MarcaId == id);
        return Ok(Map(saved, prodN, oemN));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var m = await _db.CafeMarcas.FindAsync(id);
        if (m is null) return NotFound(new { error = "Marca no encontrada" });
        // Si tiene productos u OEMs vinculados, no se borra: se desactiva. Asi se preserva la integridad referencial.
        var prodN = await _db.CafeProductos.CountAsync(p => p.MarcaId == id);
        var oemN = await _db.CafeOems.CountAsync(o => o.MarcaId == id);
        if (prodN > 0 || oemN > 0)
        {
            m.IsActive = false;
            m.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(new { deleted = false, deactivated = true,
                message = $"Marca con {prodN} productos y {oemN} OEMs vinculados: se desactivo en lugar de eliminar." });
        }
        _db.CafeMarcas.Remove(m);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    private static CafeMarcaDto Map(CafeMarca m, int productosCount, int oemsCount) => new(
        m.Id, m.Nombre,
        m.ProveedorId, m.ProveedorNav?.Nombre,
        m.Notas, m.IsActive, m.BloqueaDescuento, m.CreatedAt, m.UpdatedAt,
        productosCount, oemsCount);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
