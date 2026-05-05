using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/kits")]
[Authorize]
public class CafeKitsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CafeKitService _service;
    private static readonly string[] CategoriasValidas = { "CAFE", "OTROS" };

    public CafeKitsController(AppDbContext db, CafeKitService service)
    {
        _db = db;
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] bool? activos = null, [FromQuery] string? categoria = null)
    {
        var q = _db.CafeKits
            .Include(k => k.MarcaNav)
            .Include(k => k.Items).ThenInclude(i => i.Producto)
            .AsQueryable();
        if (activos == true) q = q.Where(k => k.IsActive);
        if (!string.IsNullOrWhiteSpace(categoria))
        {
            var c = NormCat(categoria);
            q = q.Where(k => k.Categoria == c);
        }
        var list = await q.OrderBy(k => k.Sku).ToListAsync();
        var dtos = new List<CafeKitDto>();
        foreach (var k in list) dtos.Add(await _service.MapAsync(k));
        return Ok(dtos);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var k = await _db.CafeKits
            .Include(x => x.MarcaNav)
            .Include(x => x.Items).ThenInclude(i => i.Producto)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (k is null) return NotFound(new { error = "Kit no encontrado" });
        return Ok(await _service.MapAsync(k));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCafeKitRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Sku)) return BadRequest(new { error = "El SKU es obligatorio" });
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "El nombre es obligatorio" });

        var sku = req.Sku.Trim().ToUpperInvariant();
        if (await _db.CafeKits.AnyAsync(x => x.Sku == sku))
            return BadRequest(new { error = $"Ya existe un kit con SKU '{sku}'" });
        // Tampoco permitir colisiones con SKU de productos simples.
        if (await _db.CafeProductos.AnyAsync(p => p.Sku == sku))
            return BadRequest(new { error = $"Ya existe un producto simple con SKU '{sku}'" });

        if (req.Items is null || req.Items.Count == 0)
            return BadRequest(new { error = "El kit debe tener al menos un componente" });

        // Validar componentes existentes y sin duplicados.
        var prodIds = req.Items.Select(i => i.ProductoId).Distinct().ToList();
        if (prodIds.Count != req.Items.Count)
            return BadRequest(new { error = "Componentes duplicados — sumá cantidades en una sola fila" });
        var prodsExisten = await _db.CafeProductos.Where(p => prodIds.Contains(p.Id)).Select(p => p.Id).ToListAsync();
        var faltantes = prodIds.Except(prodsExisten).ToList();
        if (faltantes.Count > 0) return BadRequest(new { error = $"Productos no encontrados: {string.Join(",", faltantes)}" });

        // Validacion: ningun item con cantidad <= 0.
        if (req.Items.Any(i => i.Cantidad <= 0))
            return BadRequest(new { error = "La cantidad de cada componente debe ser > 0" });

        var (marcaId, marcaNombre) = await ResolveMarcaAsync(req.MarcaId, req.Marca);
        var iva = NormalizeIva(req.IvaPct);

        var k = new CafeKit
        {
            Sku = sku,
            Nombre = req.Nombre.Trim(),
            Descripcion = string.IsNullOrWhiteSpace(req.Descripcion) ? null : req.Descripcion.Trim(),
            Categoria = NormCat(req.Categoria),
            Marca = marcaNombre,
            MarcaId = marcaId,
            Pvp1 = req.Pvp1.HasValue ? Math.Round(req.Pvp1.Value, 2) : null,
            Pvp2 = req.Pvp2.HasValue ? Math.Round(req.Pvp2.Value, 2) : null,
            IvaPct = iva,
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            Items = req.Items.Select(i => new CafeKitItem
            {
                ProductoId = i.ProductoId,
                Cantidad = i.Cantidad
            }).ToList()
        };
        _db.CafeKits.Add(k);
        await _db.SaveChangesAsync();

        var saved = await _db.CafeKits
            .Include(x => x.MarcaNav)
            .Include(x => x.Items).ThenInclude(i => i.Producto)
            .FirstAsync(x => x.Id == k.Id);
        return Ok(await _service.MapAsync(saved));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCafeKitRequest req)
    {
        var k = await _db.CafeKits
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (k is null) return NotFound(new { error = "Kit no encontrado" });

        if (req.Sku is not null)
        {
            var sku = req.Sku.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(sku)) return BadRequest(new { error = "SKU no puede ser vacio" });
            if (sku != k.Sku && await _db.CafeKits.AnyAsync(x => x.Sku == sku && x.Id != id))
                return BadRequest(new { error = $"Ya existe otro kit con SKU '{sku}'" });
            k.Sku = sku;
        }
        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre no puede ser vacio" });
            k.Nombre = req.Nombre.Trim();
        }
        if (req.Descripcion is not null)
            k.Descripcion = string.IsNullOrWhiteSpace(req.Descripcion) ? null : req.Descripcion.Trim();
        if (req.Categoria is not null) k.Categoria = NormCat(req.Categoria);
        if (req.MarcaId.HasValue && req.MarcaId.Value > 0)
        {
            var (mid, mnombre) = await ResolveMarcaAsync(req.MarcaId, null);
            k.MarcaId = mid; k.Marca = mnombre;
        }
        else if (req.ClearMarcaId)
        {
            k.MarcaId = null; k.Marca = null;
        }
        else if (req.Marca is not null)
        {
            var (mid, mnombre) = await ResolveMarcaAsync(null, req.Marca);
            k.MarcaId = mid; k.Marca = mnombre;
        }
        if (req.Pvp1.HasValue) k.Pvp1 = Math.Round(req.Pvp1.Value, 2);
        if (req.Pvp2.HasValue) k.Pvp2 = Math.Round(req.Pvp2.Value, 2);
        if (req.IvaPct.HasValue) k.IvaPct = NormalizeIva(req.IvaPct);
        if (req.Notas is not null) k.Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim();
        if (req.IsActive.HasValue) k.IsActive = req.IsActive.Value;

        // Reemplazar items si vinieron en la request.
        if (req.Items is not null)
        {
            if (req.Items.Count == 0) return BadRequest(new { error = "El kit debe tener al menos un componente" });
            if (req.Items.Any(i => i.Cantidad <= 0)) return BadRequest(new { error = "Cantidad debe ser > 0" });
            var prodIds = req.Items.Select(i => i.ProductoId).Distinct().ToList();
            if (prodIds.Count != req.Items.Count)
                return BadRequest(new { error = "Componentes duplicados — sumá cantidades" });
            var prodsExisten = await _db.CafeProductos.Where(p => prodIds.Contains(p.Id)).Select(p => p.Id).ToListAsync();
            var faltantes = prodIds.Except(prodsExisten).ToList();
            if (faltantes.Count > 0) return BadRequest(new { error = $"Productos no encontrados: {string.Join(",", faltantes)}" });

            _db.CafeKitItems.RemoveRange(k.Items);
            k.Items = req.Items.Select(i => new CafeKitItem
            {
                KitId = k.Id,
                ProductoId = i.ProductoId,
                Cantidad = i.Cantidad
            }).ToList();
        }

        k.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var saved = await _db.CafeKits
            .Include(x => x.MarcaNav)
            .Include(x => x.Items).ThenInclude(i => i.Producto)
            .FirstAsync(x => x.Id == k.Id);
        return Ok(await _service.MapAsync(saved));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var k = await _db.CafeKits.FindAsync(id);
        if (k is null) return NotFound(new { error = "Kit no encontrado" });
        // Si esta vinculado a publicaciones de MeLi, lo desactivamos en lugar de borrar
        var meliCount = await _db.MeliItems.CountAsync(mi => mi.CafeKitId == id);
        if (meliCount > 0)
        {
            k.IsActive = false;
            k.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(new { deleted = false, deactivated = true, message = $"Kit con {meliCount} publicaciones MeLi vinculadas: se desactivo." });
        }
        _db.CafeKits.Remove(k);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    private async Task<(int?, string?)> ResolveMarcaAsync(int? marcaId, string? marcaTexto)
    {
        if (marcaId.HasValue && marcaId.Value > 0)
        {
            var m = await _db.CafeMarcas.FindAsync(marcaId.Value);
            return m is null ? (null, null) : (m.Id, m.Nombre);
        }
        if (string.IsNullOrWhiteSpace(marcaTexto)) return (null, null);
        var nombre = marcaTexto.Trim();
        var match = await _db.CafeMarcas.FirstOrDefaultAsync(m => m.Nombre == nombre);
        return match is null ? (null, nombre) : (match.Id, match.Nombre);
    }

    private static string NormCat(string? c)
    {
        if (string.IsNullOrWhiteSpace(c)) return "OTROS";
        var v = c.Trim().ToUpperInvariant();
        return CategoriasValidas.Contains(v) ? v : "OTROS";
    }

    private static decimal NormalizeIva(decimal? iva)
    {
        if (!iva.HasValue) return 21m;
        return iva.Value == 10.5m ? 10.5m : 21m;
    }
}
