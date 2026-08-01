using Api.Data;
using Api.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

// 2026-08-01: Catalogo unificado. Devuelve TODOS los codigos del sistema
// (productos sueltos, combos MeLi, compuestos, kits y servicios) en una sola
// lista, para buscar/filtrar desde una unica pantalla (/cafe/catalogo).
// Es de solo lectura: cada item trae el EditRoute a su pantalla de edicion actual.
[ApiController]
[Route("api/cafe/catalogo")]
[Authorize]
public class CafeCatalogoController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafeCatalogoController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var items = new List<CafeCatalogoItemDto>();

        // Stock por producto (para calcular lo "armable" de combos/kits)
        var stockProd = await _db.CafeProductos.AsNoTracking()
            .ToDictionaryAsync(p => p.Id, p => p.StockUnidades);
        var skuProd = await _db.CafeProductos.AsNoTracking()
            .Where(p => p.Sku != null)
            .ToDictionaryAsync(p => p.Id, p => p.Sku!);

        // ── Productos sueltos ──
        var productos = await _db.CafeProductos.AsNoTracking()
            .Select(p => new { p.Id, p.Sku, p.Nombre, p.Categoria, p.StockUnidades, p.IsActive })
            .ToListAsync();
        foreach (var p in productos)
        {
            items.Add(new CafeCatalogoItemDto(
                p.Id, "producto", p.Sku, p.Nombre, p.Categoria,
                p.StockUnidades, false, p.IsActive, null, "/cafe/productos"));
        }

        // ── Combos y compuestos (Cafe_Combos, se distinguen por EsCompuesto) ──
        var combos = await _db.CafeCombos.AsNoTracking()
            .Include(c => c.Items)
            .ToListAsync();
        foreach (var c in combos)
        {
            var tipo = c.EsCompuesto ? "compuesto" : "combo";
            int? armable = null;
            if (c.Items.Count > 0)
            {
                armable = c.Items.Min(it =>
                {
                    var st = stockProd.TryGetValue(it.ProductoId, out var s) ? s : 0;
                    return it.Cantidad > 0 ? st / it.Cantidad : 0;
                });
            }
            items.Add(new CafeCatalogoItemDto(
                c.Id, tipo, c.Sku, c.Nombre, c.Categoria,
                armable, true, c.IsActive,
                DetalleItems(c.Items.Select(i => (i.ProductoId, (decimal)i.Cantidad)), skuProd),
                $"/cafe/combos?edit={c.Id}"));
        }

        // ── Kits ──
        var kits = await _db.CafeKits.AsNoTracking()
            .Include(k => k.Items)
            .ToListAsync();
        foreach (var k in kits)
        {
            int? armable = null;
            if (k.Items.Count > 0)
            {
                armable = k.Items.Min(it =>
                {
                    var st = stockProd.TryGetValue(it.ProductoId, out var s) ? s : 0;
                    return it.Cantidad > 0 ? (int)(st / it.Cantidad) : 0;
                });
            }
            items.Add(new CafeCatalogoItemDto(
                k.Id, "kit", k.Sku, k.Nombre, k.Categoria,
                armable, true, k.IsActive,
                DetalleItems(k.Items.Select(i => (i.ProductoId, i.Cantidad)), skuProd),
                "/cafe/kits"));
        }

        // ── Servicios (sin SKU ni stock) ──
        var servicios = await _db.CafeServicios.AsNoTracking()
            .Select(s => new { s.Id, s.Nombre, s.Descripcion, s.IsActive })
            .ToListAsync();
        foreach (var s in servicios)
        {
            items.Add(new CafeCatalogoItemDto(
                s.Id, "servicio", null, s.Nombre, null,
                null, false, s.IsActive, s.Descripcion, "/cafe/servicios"));
        }

        var ordenado = items
            .OrderBy(i => string.IsNullOrEmpty(i.Sku) ? 1 : 0)
            .ThenBy(i => i.Sku)
            .ThenBy(i => i.Nombre)
            .ToList();

        return Ok(ordenado);
    }

    // "4 × ABE-CHED" para 1 componente; "3 componentes" para varios.
    private static string? DetalleItems(IEnumerable<(int ProductoId, decimal Cantidad)> items, Dictionary<int, string> skuProd)
    {
        var list = items.ToList();
        if (list.Count == 0) return null;
        if (list.Count == 1)
        {
            var it = list[0];
            var sku = skuProd.TryGetValue(it.ProductoId, out var sk) ? sk : "?";
            var cant = it.Cantidad == Math.Floor(it.Cantidad) ? ((int)it.Cantidad).ToString() : it.Cantidad.ToString("0.##");
            return $"{cant} × {sku}";
        }
        return $"{list.Count} componentes";
    }
}
