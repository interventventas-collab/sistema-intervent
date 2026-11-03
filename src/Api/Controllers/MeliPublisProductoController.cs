using Api.Data;
using Api.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>2026-11-03: "en qué publicaciones de MeLi está este producto". Lo usan el listado y
/// la ficha de Productos, y la bandeja de preguntas (packs para ofrecer + buscador por código).
/// Todo sale de la base propia (MeliItemComponentes + MeliItems), cero llamadas a MeLi.</summary>
[ApiController]
[Route("api/meli/publis-producto")]
[Authorize]
public class MeliPublisProductoController : ControllerBase
{
    private readonly AppDbContext _db;
    private static readonly string[] Vivas = { "active", "paused" };

    public MeliPublisProductoController(AppDbContext db) { _db = db; }

    /// <summary>Cuántas publicaciones activas/pausadas tiene cada producto (para el botoncito del listado).</summary>
    [HttpGet("conteos")]
    public async Task<ActionResult<List<MeliPublisConteoDto>>> Conteos()
    {
        var filas = await (from c in _db.MeliItemComponentes
                           join i in _db.MeliItems on c.MeliItemId equals i.MeliItemId
                           where Vivas.Contains(i.Status)
                           select new { c.CafeProductoId, i.MeliItemId, i.Status })
                          .Distinct().ToListAsync();
        return filas.GroupBy(f => f.CafeProductoId)
            .Select(g => new MeliPublisConteoDto(g.Key,
                g.Where(x => x.Status == "active").Select(x => x.MeliItemId).Distinct().Count(),
                g.Where(x => x.Status == "paused").Select(x => x.MeliItemId).Distinct().Count()))
            .ToList();
    }

    [HttpGet("{productoId:int}")]
    public async Task<ActionResult<MeliPublisDeProductoDto>> DeProducto(int productoId)
    {
        var r = await ArmarAsync(productoId);
        return r is null ? NotFound() : r;
    }

    /// <summary>Buscador por código: SKU exacto o código de barras; si no hay, devuelve parecidos.</summary>
    [HttpGet("buscar")]
    public async Task<ActionResult<MeliPublisBusquedaDto>> Buscar([FromQuery] string q)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2) return new MeliPublisBusquedaDto(null, new());
        var exacto = await _db.CafeProductos.Where(p => p.Sku == q || p.Barcode == q)
            .OrderByDescending(p => p.IsActive).Select(p => (int?)p.Id).FirstOrDefaultAsync();
        if (exacto is int id)
            return new MeliPublisBusquedaDto(await ArmarAsync(id), new());
        // Código de un armado/combo (ej. C1925NEG = cuerpo + tapa): se ofrecen sus partes y se
        // muestra la que más unidades lleva.
        var partes = await (from c in _db.CafeCombos
                            join ci in _db.CafeComboItems on c.Id equals ci.ComboId
                            join p in _db.CafeProductos on ci.ProductoId equals p.Id
                            where c.Sku == q && c.IsActive
                            orderby ci.Cantidad descending, p.Sku
                            select new MeliProductoCandidatoDto(p.Id, p.Sku, p.Nombre)).ToListAsync();
        partes = partes.DistinctBy(x => x.Id).ToList();
        if (partes.Count > 0)
            return new MeliPublisBusquedaDto(await ArmarAsync(partes[0].Id), partes.Count > 1 ? partes : new());
        var parecidos = await _db.CafeProductos
            .Where(p => p.IsActive && ((p.Sku != null && p.Sku.Contains(q)) || p.Nombre.Contains(q)))
            .OrderBy(p => p.Sku).Take(12)
            .Select(p => new MeliProductoCandidatoDto(p.Id, p.Sku, p.Nombre)).ToListAsync();
        return new MeliPublisBusquedaDto(null, parecidos);
    }

    /// <summary>Para la bandeja de preguntas: a partir de la publicación preguntada, el producto que
    /// lleva (el de más unidades si es combo) y, si es combo, sus otros productos como candidatos.</summary>
    [HttpGet("de-publi/{meliItemId}")]
    public async Task<ActionResult<MeliPublisBusquedaDto>> DePubli(string meliItemId)
    {
        var partes = await (from c in _db.MeliItemComponentes
                            join p in _db.CafeProductos on c.CafeProductoId equals p.Id
                            where c.MeliItemId == meliItemId
                            select new { p.Id, p.Sku, p.Nombre, c.Cantidad }).ToListAsync();
        if (partes.Count == 0) return new MeliPublisBusquedaDto(null, new());
        var unicas = partes.GroupBy(x => x.Id).Select(g => g.OrderByDescending(x => x.Cantidad).First())
            .OrderByDescending(x => x.Cantidad).ThenBy(x => x.Sku).ToList();
        var candidatos = unicas.Count > 1
            ? unicas.Select(x => new MeliProductoCandidatoDto(x.Id, x.Sku, x.Nombre)).ToList()
            : new List<MeliProductoCandidatoDto>();
        return new MeliPublisBusquedaDto(await ArmarAsync(unicas[0].Id), candidatos);
    }

    private async Task<MeliPublisDeProductoDto?> ArmarAsync(int productoId)
    {
        var prod = await _db.CafeProductos.Where(p => p.Id == productoId)
            .Select(p => new { p.Id, p.Sku, p.Nombre }).FirstOrDefaultAsync();
        if (prod is null) return null;

        var mios = await _db.MeliItemComponentes.Where(c => c.CafeProductoId == productoId)
            .Select(c => new { c.MeliItemId, c.MeliVariationId, c.Cantidad }).ToListAsync();
        var ids = mios.Select(m => m.MeliItemId).Distinct().ToList();
        var items = await _db.MeliItems.Where(i => ids.Contains(i.MeliItemId) && Vivas.Contains(i.Status))
            .ToListAsync();
        // Todas las filas de receta de esas publicaciones, para saber si llevan otros productos.
        var recetas = await _db.MeliItemComponentes.Where(c => ids.Contains(c.MeliItemId))
            .Select(c => new { c.MeliItemId, c.MeliVariationId, c.CafeProductoId }).ToListAsync();

        var publis = new List<MeliPubliDeProductoDto>();
        foreach (var it in items.GroupBy(i => i.MeliItemId).Select(g => g.First()))
        {
            // En publis con colores cada variante tiene su receta: se mira la variante donde está
            // este producto, así un multicolor no cuenta como "combo".
            var mia = mios.Where(m => m.MeliItemId == it.MeliItemId).OrderByDescending(m => m.Cantidad).First();
            var otros = recetas.Where(r => r.MeliItemId == it.MeliItemId && r.MeliVariationId == mia.MeliVariationId)
                .Select(r => r.CafeProductoId).Distinct().Count();
            var tipo = otros > 1 ? "combo" : mia.Cantidad > 1 ? "pack" : "suelta";
            var variante = mia.MeliVariationId is null ? null
                : items.FirstOrDefault(i => i.MeliItemId == it.MeliItemId && i.VariationId == mia.MeliVariationId)?.VariationAttributes;
            publis.Add(new MeliPubliDeProductoDto(it.MeliItemId, it.Title, it.Thumbnail, it.Permalink, it.Price,
                it.Status, it.LogisticType, it.AvailableQuantity, it.SoldQuantity, it.Sku, it.ListingTypeId,
                mia.Cantidad, tipo, variante));
        }
        publis = publis
            .OrderBy(p => p.Status == "active" ? 0 : 1)
            .ThenBy(p => p.Tipo == "suelta" ? 0 : p.Tipo == "pack" ? 1 : 2)
            .ThenBy(p => p.Cantidad).ThenByDescending(p => p.SoldQuantity).ToList();
        return new MeliPublisDeProductoDto(prod.Id, prod.Sku, prod.Nombre,
            publis.Count(p => p.Status == "active"), publis.Count(p => p.Status == "paused"), publis);
    }
}
