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
        p.MarcaId, p.MarcaNav?.Nombre,
        p.Costo, p.PrecioPorKg,
        p.Pvp1, p.Pvp2,
        p.BarPctSobreCosto, p.UxB,
        p.OemId, p.OemNav?.Codigo,
        p.StockGramos, p.StockUnidades,
        p.Notas, p.IsActive, p.IvaPct, p.CreatedAt, p.UpdatedAt,
        p.OemNav?.PvpConIva, p.OemNav?.IvaPct);

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? categoria = null)
    {
        var q = _db.CafeProductos.Include(p => p.OemNav).Include(p => p.MarcaNav).AsQueryable();
        if (!string.IsNullOrWhiteSpace(categoria))
        {
            var c = NormCat(categoria);
            q = q.Where(p => p.Categoria == c);
        }
        var list = await q.ToListAsync();
        // Orden natural por SKU (F1, F2, F3, ..., F10, F11) y luego por nombre.
        // Productos sin SKU caen al final, ordenados por nombre.
        list = list
            .OrderBy(p => p.Categoria)
            .ThenBy(p => string.IsNullOrEmpty(p.Sku) ? 1 : 0)
            .ThenBy(p => SkuLetras(p.Sku))
            .ThenBy(p => SkuNumero(p.Sku))
            .ThenBy(p => p.Sku)
            .ThenBy(p => p.Nombre)
            .ToList();
        return Ok(list.Select(Map).ToList());
    }

    // "F1" → "F", "C8733NEG" → "C", "PAL50COLOR" → "PAL"
    private static string SkuLetras(string? sku)
    {
        if (string.IsNullOrEmpty(sku)) return "";
        var i = 0;
        while (i < sku.Length && !char.IsDigit(sku[i])) i++;
        return sku.Substring(0, i).ToUpperInvariant();
    }

    // "F1" → 1, "F11" → 11, "C8733NEG" → 8733, "ABC" → 0
    private static int SkuNumero(string? sku)
    {
        if (string.IsNullOrEmpty(sku)) return 0;
        var i = 0;
        while (i < sku.Length && !char.IsDigit(sku[i])) i++;
        var start = i;
        while (i < sku.Length && char.IsDigit(sku[i])) i++;
        if (i == start) return 0;
        return int.TryParse(sku.AsSpan(start, i - start), out var n) ? n : 0;
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var p = await _db.CafeProductos.Include(x => x.OemNav).Include(x => x.MarcaNav).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound(new { error = "Producto no encontrado" });
        return Ok(Map(p));
    }

    /// <summary>
    /// Preview de sincronizacion a MeLi: lista todas las publicaciones vinculadas a este cafe,
    /// con el stock+precio actual en MeLi vs el que se va a pushear.
    /// </summary>
    [HttpGet("{id:int}/meli-preview")]
    public async Task<IActionResult> MeliPreview(int id)
    {
        var cafe = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == id);
        if (cafe is null) return NotFound(new { error = "Cafe no encontrado" });
        var settings = await _db.CafeSettings.FindAsync(1) ?? new Models.CafeSetting { Id = 1 };

        var items = await _db.MeliItems
            .Include(i => i.MeliAccount)
            .Where(i => i.CafeProductoId == id && i.Status == "active")
            .OrderBy(i => i.CafeFormato).ThenBy(i => i.MeliItemId)
            .ToListAsync();

        var listaKg = cafe.Pvp1 ?? cafe.Pvp2 ?? cafe.PrecioPorKg ?? 0m;
        var rows = items.Select(it =>
        {
            var formato = string.IsNullOrEmpty(it.CafeFormato) ? "1KG" : it.CafeFormato;
            decimal precioSinIva = formato switch
            {
                "1KG" => listaKg,
                "MEDIO" => Math.Round(listaKg / 2m + settings.CostoFraccionamiento, 2, MidpointRounding.AwayFromZero),
                "CUARTO" => Math.Round(listaKg / 4m + settings.CostoFraccionamiento, 2, MidpointRounding.AwayFromZero),
                _ => listaKg
            };
            decimal precioConIva = cafe.IvaPct > 0
                ? Math.Round(precioSinIva * (1m + cafe.IvaPct / 100m), 2, MidpointRounding.AwayFromZero)
                : precioSinIva;
            int gramosPorUnidad = formato switch { "MEDIO" => 500, "CUARTO" => 250, _ => 1000 };
            int stockNuevo = (int)Math.Floor(cafe.StockGramos / gramosPorUnidad);

            return new
            {
                meliItemId = it.MeliItemId,
                title = it.Title,
                cuenta = it.MeliAccount != null ? it.MeliAccount.Nickname : "—",
                formato,
                stockMeli = it.AvailableQuantity,
                stockNuevo,
                stockDelta = stockNuevo - it.AvailableQuantity,
                precioMeli = it.Price,
                precioNuevo = precioConIva,
                precioDelta = precioConIva - it.Price,
                cambia = stockNuevo != it.AvailableQuantity || precioConIva != it.Price
            };
        }).ToList();

        return Ok(new
        {
            cafe = new { id = cafe.Id, sku = cafe.Sku, nombre = cafe.Nombre, stockGramos = cafe.StockGramos, pvp1 = cafe.Pvp1, ivaPct = cafe.IvaPct },
            publicaciones = rows
        });
    }

    public record PushMeliRequest(List<int>? MeliItemIds, bool PushPrice = true, bool PushStock = true);

    /// <summary>
    /// Pushea precio + stock a las publicaciones MeLi vinculadas al cafe.
    /// Si no se pasan IDs, pushea todas. Devuelve resultado por publicacion.
    /// </summary>
    [HttpPost("{id:int}/push-meli")]
    public async Task<IActionResult> PushMeli(int id, [FromBody] PushMeliRequest req, [FromServices] Api.Services.MeliItemService meliService)
    {
        if (!await _db.CafeProductos.AnyAsync(p => p.Id == id))
            return NotFound(new { error = "Cafe no encontrado" });

        var q = _db.MeliItems.Where(i => i.CafeProductoId == id && i.Status == "active");
        if (req.MeliItemIds is not null && req.MeliItemIds.Count > 0)
        {
            var ids = req.MeliItemIds;
            q = q.Where(i => ids.Contains(i.Id));
        }
        var items = await q.ToListAsync();

        var results = new List<object>();
        foreach (var it in items)
        {
            try
            {
                var r = await meliService.PushFromProductAsync(it.Id, req.PushPrice, req.PushStock);
                results.Add(new { meliItemId = it.MeliItemId, success = r.Success, message = r.Message, pushedPrice = r.PushedPrice, pushedStock = r.PushedStock });
            }
            catch (Exception ex)
            {
                results.Add(new { meliItemId = it.MeliItemId, success = false, message = ex.Message, pushedPrice = (decimal?)null, pushedStock = (int?)null });
            }
        }
        return Ok(new { total = items.Count, ok = results.Count(r => (bool)r.GetType().GetProperty("success")!.GetValue(r)!), results });
    }

    [HttpGet("{id:int}/historial-precios")]
    public async Task<IActionResult> HistorialPrecios(int id)
    {
        if (!await _db.CafeProductos.AnyAsync(p => p.Id == id))
            return NotFound(new { error = "Producto no encontrado" });

        var rows = await _db.CafeHistorialPrecios
            .Where(h => h.ProductoId == id)
            .OrderByDescending(h => h.ChangedAt)
            .Select(h => new CafeHistorialPrecioDto(
                h.Id,
                h.Pvp1Anterior, h.Pvp2Anterior, h.CostoAnterior, h.IvaPctAnterior,
                h.Pvp1Nuevo, h.Pvp2Nuevo, h.CostoNuevo, h.IvaPctNuevo,
                h.ChangedAt, h.ChangedBy, h.Motivo))
            .ToListAsync();

        return Ok(rows);
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

        // Resolver MarcaId: si vino MarcaId valido, lo uso. Si no, intento crear marca al vuelo desde el string Marca.
        var (marcaId, marcaNombre) = await ResolveMarcaAsync(req.MarcaId, req.Marca);

        var p = new CafeProducto
        {
            Sku = string.IsNullOrWhiteSpace(req.Sku) ? null : req.Sku.Trim().ToUpperInvariant(),
            Barcode = string.IsNullOrWhiteSpace(req.Barcode) ? null : req.Barcode.Trim(),
            Nombre = req.Nombre.Trim(),
            Categoria = cat,
            Marca = marcaNombre,
            MarcaId = marcaId,
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
            IvaPct = NormalizeIva(req.IvaPct),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeProductos.Add(p);
        await _db.SaveChangesAsync();
        var saved = await _db.CafeProductos.Include(x => x.OemNav).Include(x => x.MarcaNav).FirstAsync(x => x.Id == p.Id);
        return Ok(Map(saved));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCafeProductoRequest req)
    {
        var p = await _db.CafeProductos.FindAsync(id);
        if (p is null) return NotFound(new { error = "Producto no encontrado" });

        // Snapshot de los valores ANTERIORES para grabar historial si cambian.
        var oldPvp1 = p.Pvp1; var oldPvp2 = p.Pvp2; var oldCosto = p.Costo; var oldIva = p.IvaPct;
        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "El nombre no puede ser vacio" });
            p.Nombre = req.Nombre.Trim();
        }
        if (req.Sku is not null) p.Sku = string.IsNullOrWhiteSpace(req.Sku) ? null : req.Sku.Trim().ToUpperInvariant();
        if (req.Barcode is not null) p.Barcode = string.IsNullOrWhiteSpace(req.Barcode) ? null : req.Barcode.Trim();
        if (req.Categoria is not null) p.Categoria = NormCat(req.Categoria);
        // Marca: si viene MarcaId (incluyendo 0/null+ClearMarcaId), lo aplico. El string Marca queda
        // sincronizado con el nombre de la marca correspondiente, o null si se desvinculo.
        if (req.MarcaId.HasValue && req.MarcaId.Value > 0)
        {
            var (mid, mnombre) = await ResolveMarcaAsync(req.MarcaId, null);
            p.MarcaId = mid;
            p.Marca = mnombre;
        }
        else if (req.ClearMarcaId)
        {
            p.MarcaId = null;
            p.Marca = null;
        }
        else if (req.Marca is not null)
        {
            // Compatibilidad: si solo viene el texto, intento crear/matchear marca por nombre.
            var (mid, mnombre) = await ResolveMarcaAsync(null, req.Marca);
            p.MarcaId = mid;
            p.Marca = mnombre;
        }
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
        if (req.IvaPct.HasValue) p.IvaPct = NormalizeIva(req.IvaPct);
        p.UpdatedAt = DateTime.UtcNow;

        // Si algun precio cambio, grabar historial.
        bool precioCambio = oldPvp1 != p.Pvp1 || oldPvp2 != p.Pvp2 || oldCosto != p.Costo || oldIva != p.IvaPct;
        if (precioCambio)
        {
            _db.CafeHistorialPrecios.Add(new CafeHistorialPrecio
            {
                ProductoId = p.Id,
                Pvp1Anterior = oldPvp1, Pvp2Anterior = oldPvp2, CostoAnterior = oldCosto, IvaPctAnterior = oldIva,
                Pvp1Nuevo = p.Pvp1, Pvp2Nuevo = p.Pvp2, CostoNuevo = p.Costo, IvaPctNuevo = p.IvaPct,
                ChangedAt = DateTime.UtcNow,
                ChangedBy = User?.Identity?.Name ?? "Sistema"
            });
        }

        await _db.SaveChangesAsync();
        var saved = await _db.CafeProductos.Include(x => x.OemNav).Include(x => x.MarcaNav).FirstAsync(x => x.Id == p.Id);
        return Ok(Map(saved));
    }

    /// <summary>Resuelve marca: si viene un MarcaId valido, lo busca. Si no y viene texto, lo busca/crea
    /// por nombre. Devuelve (MarcaId, NombreMarca) — null/null si no hay marca.</summary>
    private async Task<(int?, string?)> ResolveMarcaAsync(int? marcaId, string? marcaTexto)
    {
        if (marcaId.HasValue && marcaId.Value > 0)
        {
            var existing = await _db.CafeMarcas.FindAsync(marcaId.Value);
            if (existing is null) return (null, null);
            return (existing.Id, existing.Nombre);
        }
        if (string.IsNullOrWhiteSpace(marcaTexto)) return (null, null);
        var nombre = marcaTexto.Trim();
        var match = await _db.CafeMarcas.FirstOrDefaultAsync(m => m.Nombre == nombre);
        if (match is not null) return (match.Id, match.Nombre);
        // Crear al vuelo
        var nuevo = new CafeMarca { Nombre = nombre, IsActive = true, CreatedAt = DateTime.UtcNow };
        _db.CafeMarcas.Add(nuevo);
        await _db.SaveChangesAsync();
        return (nuevo.Id, nuevo.Nombre);
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

    // IVA permitido: 21 (default) o 10.5. Cualquier otro valor cae a 21.
    private static decimal NormalizeIva(decimal? iva)
    {
        if (!iva.HasValue) return 21m;
        var v = iva.Value;
        if (v == 10.5m) return 10.5m;
        return 21m;
    }
}
