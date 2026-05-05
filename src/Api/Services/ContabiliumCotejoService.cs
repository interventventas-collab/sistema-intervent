using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

// Cotejo: por cada publicacion activa de MeLi (con SKU), determina si ese SKU
// existe en Contabilium como producto, como combo, o si es huerfano. Para combos
// reporta tambien la salud de la composicion (componentes faltantes en productos).
public class ContabiliumCotejoService
{
    private readonly AppDbContext _db;

    public ContabiliumCotejoService(AppDbContext db)
    {
        _db = db;
    }

    public record CotejoResumen(
        int TotalPublicaciones,
        int SinSku,
        int Producto,
        int Combo,
        int Huerfano,
        int CombosConComponentesFaltantes,
        int ProductosCargados,
        int CombosCargados);

    public async Task<CotejoResumen> ResumenAsync()
    {
        var prodCount = await _db.ContabProductos.CountAsync();
        var combosCount = await _db.ContabCombos.CountAsync();

        var pubs = await _db.MeliItems
            .Where(i => i.Status == "active")
            .Select(i => new { i.MeliItemId, i.VariationId, i.Sku })
            .ToListAsync();

        var skusContabProductos = await _db.ContabProductos.Select(p => p.Sku.ToLower()).ToListAsync();
        var skusContabCombos = await _db.ContabCombos.Select(c => c.SkuCombo.ToLower()).ToListAsync();
        var setProd = new HashSet<string>(skusContabProductos);
        var setCombo = new HashSet<string>(skusContabCombos);

        int sinSku = 0, prod = 0, combo = 0, huerfano = 0;
        foreach (var p in pubs)
        {
            var s = p.Sku?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(s)) { sinSku++; continue; }
            if (setCombo.Contains(s)) combo++;
            else if (setProd.Contains(s)) prod++;
            else huerfano++;
        }

        // Combos con componentes faltantes: cualquier ComponenteSku que no este en
        // Contab_Productos. Se cuenta el combo solo una vez aunque le falten varios.
        var brokenCombos = await _db.ContabComboItems
            .Where(ci => !_db.ContabProductos.Any(p => p.Sku == ci.SkuComponente))
            .Select(ci => ci.SkuCombo)
            .Distinct()
            .CountAsync();

        return new CotejoResumen(pubs.Count, sinSku, prod, combo, huerfano, brokenCombos, prodCount, combosCount);
    }

    public record CotejoFila(
        int MeliRowId,
        string MeliItemId,
        string? VariationId,
        string? VariationAttributes,
        string Title,
        string? Sku,
        string Categoria,
        decimal Price,
        int AvailableQuantity,
        string? ContabNombre,
        // Solo informativo: el campo "Proveedor" de Contabilium, que el usuario suele
        // usar como marca/fabricante (ej: "COLOMBRARO HERMANOS S.A.").
        string? MarcaContab,
        decimal? ContabPrecioFinal,
        decimal? ContabStock,
        bool ComboTieneFaltantes,
        int? ProductIdVinculado,
        int? ComboIdVinculado,
        int? CafeProductoIdVinculado,
        int? CafeComboIdVinculado);

    public async Task<List<CotejoFila>> ListarAsync(string categoria = "todos", string? buscar = null, int? meliAccountId = null, int take = 200, string? marcaContab = null)
    {
        // Set de combos con problemas para marcar la fila.
        var brokenCombosSql = await _db.ContabComboItems
            .Where(ci => !_db.ContabProductos.Any(p => p.Sku == ci.SkuComponente))
            .Select(ci => ci.SkuCombo)
            .Distinct()
            .ToListAsync();
        var brokenCombos = new HashSet<string>(brokenCombosSql, StringComparer.OrdinalIgnoreCase);

        var q = _db.MeliItems
            .Where(i => i.Status == "active");
        if (meliAccountId.HasValue) q = q.Where(i => i.MeliAccountId == meliAccountId.Value);
        if (!string.IsNullOrWhiteSpace(buscar))
        {
            var needle = buscar.Trim().ToLower();
            q = q.Where(i =>
                (i.Sku != null && i.Sku.ToLower().Contains(needle)) ||
                i.Title.ToLower().Contains(needle) ||
                i.MeliItemId.ToLower().Contains(needle));
        }

        // Filtro por marca de Contabilium (campo Proveedor) — solo aplicable si la fila
        // matchea con un producto de Contab.
        if (!string.IsNullOrWhiteSpace(marcaContab))
        {
            var mc = marcaContab.Trim();
            q = q.Where(i => i.Sku != null && i.Sku != ""
                && _db.ContabProductos.Any(p => p.Sku == i.Sku && p.Proveedor == mc));
        }

        // Filtro por categoria a nivel SQL (subqueries contra Contab_*).
        switch (categoria)
        {
            case "sin_sku":
                q = q.Where(i => i.Sku == null || i.Sku == "");
                break;
            case "combo":
                q = q.Where(i => i.Sku != null && i.Sku != ""
                    && _db.ContabCombos.Any(c => c.SkuCombo == i.Sku));
                break;
            case "producto":
                // Producto = SKU existe en Contab_Productos pero NO en Contab_Combos.
                q = q.Where(i => i.Sku != null && i.Sku != ""
                    && _db.ContabProductos.Any(p => p.Sku == i.Sku)
                    && !_db.ContabCombos.Any(c => c.SkuCombo == i.Sku));
                break;
            case "huerfano":
                q = q.Where(i => i.Sku != null && i.Sku != ""
                    && !_db.ContabProductos.Any(p => p.Sku == i.Sku)
                    && !_db.ContabCombos.Any(c => c.SkuCombo == i.Sku));
                break;
            // "todos" no agrega filtro
        }

        var meliRows = await q
            .OrderBy(i => i.Title)
            .Take(take)
            .Select(i => new
            {
                i.Id, i.MeliItemId, i.VariationId, i.VariationAttributes,
                i.Title, i.Sku, i.Price, i.AvailableQuantity, i.ProductId, i.ComboId,
                i.CafeProductoId, i.CafeComboId
            })
            .ToListAsync();

        // Lookup de Contabilium por SKU (en lower).
        var skus = meliRows.Where(r => !string.IsNullOrEmpty(r.Sku))
            .Select(r => r.Sku!.Trim().ToLower()).Distinct().ToList();

        var contabProds = await _db.ContabProductos
            .Where(p => skus.Contains(p.Sku.ToLower()))
            .Select(p => new { p.Sku, p.Nombre, p.PrecioFinal, p.Stock, p.Proveedor })
            .ToListAsync();
        var prodLookup = contabProds.ToDictionary(p => p.Sku.ToLowerInvariant(), p => (p.Nombre, p.PrecioFinal, p.Stock, p.Proveedor));

        var contabCombos = await _db.ContabCombos
            .Where(c => skus.Contains(c.SkuCombo.ToLower()))
            .Select(c => new { c.SkuCombo, c.Nombre, c.PrecioFinal })
            .ToListAsync();
        var comboLookup = contabCombos.ToDictionary(c => c.SkuCombo.ToLowerInvariant(), c => (c.Nombre, c.PrecioFinal));

        var rows = new List<CotejoFila>();
        foreach (var r in meliRows)
        {
            var skuLower = r.Sku?.Trim().ToLowerInvariant();
            string cat;
            string? cName = null;
            string? cMarca = null;
            decimal? cPF = null;
            decimal? cStock = null;
            bool comboBroken = false;

            if (string.IsNullOrEmpty(skuLower))
            {
                cat = "sin_sku";
            }
            else if (comboLookup.TryGetValue(skuLower, out var cv))
            {
                cat = "combo";
                cName = cv.Nombre;
                cPF = cv.PrecioFinal;
                comboBroken = brokenCombos.Contains(skuLower);
            }
            else if (prodLookup.TryGetValue(skuLower, out var pv))
            {
                cat = "producto";
                cName = pv.Nombre;
                cPF = pv.PrecioFinal;
                cStock = pv.Stock;
                cMarca = pv.Proveedor;
            }
            else
            {
                cat = "huerfano";
            }

            rows.Add(new CotejoFila(
                r.Id, r.MeliItemId, r.VariationId, r.VariationAttributes,
                r.Title, r.Sku, cat, r.Price, r.AvailableQuantity,
                cName, cMarca, cPF, cStock, comboBroken,
                r.ProductId, r.ComboId, r.CafeProductoId, r.CafeComboId));

            if (rows.Count >= take) break;
        }

        return rows;
    }

    public record ComboDetalle(
        string SkuCombo,
        string? Nombre,
        decimal? PrecioFinal,
        List<ComboComponente> Componentes,
        int FaltantesCount);

    public record ComboComponente(
        string SkuComponente,
        string? NombreComponente,
        decimal Cantidad,
        bool ExisteEnContabilium,
        decimal? StockComponente);

    public async Task<ComboDetalle?> DetalleComboAsync(string skuCombo)
    {
        var combo = await _db.ContabCombos.FirstOrDefaultAsync(c => c.SkuCombo == skuCombo);
        if (combo is null) return null;

        var items = await _db.ContabComboItems
            .Where(ci => ci.SkuCombo == skuCombo)
            .ToListAsync();

        var compSkus = items.Select(i => i.SkuComponente).Distinct().ToList();
        var prodCompList = await _db.ContabProductos
            .Where(p => compSkus.Contains(p.Sku))
            .Select(p => new { p.Sku, p.Stock })
            .ToListAsync();
        var prodLookup = prodCompList.ToDictionary(p => p.Sku, StringComparer.OrdinalIgnoreCase);

        var componentes = items.Select(i =>
        {
            var existe = prodLookup.ContainsKey(i.SkuComponente);
            decimal? stock = existe ? prodLookup[i.SkuComponente].Stock : null;
            return new ComboComponente(i.SkuComponente, i.NombreComponente, i.Cantidad, existe, stock);
        }).ToList();

        var faltantes = componentes.Count(c => !c.ExisteEnContabilium);
        return new ComboDetalle(combo.SkuCombo, combo.Nombre, combo.PrecioFinal, componentes, faltantes);
    }

    public record CrearProductosRequest(List<string> Skus, int? MarcaId, string? Categoria);
    public record CrearProductosResultado(int Creados, int Vinculados, int Omitidos, List<string> Detalles);

    // Crea CafeProductos en lote a partir de los SKU seleccionados (que tienen pareja
    // como producto en Contab_Productos). Vincula despues TODAS las MeliItems con
    // ese SKU al CafeProducto creado, asi compartis stock entre publicaciones.
    public async Task<CrearProductosResultado> CrearProductosBatchAsync(CrearProductosRequest req)
    {
        var detalles = new List<string>();
        int creados = 0, vinculados = 0, omitidos = 0;

        var categoriaFinal = string.IsNullOrWhiteSpace(req.Categoria) ? "OTROS" : req.Categoria.Trim().ToUpperInvariant();
        if (categoriaFinal != "CAFE" && categoriaFinal != "OTROS") categoriaFinal = "OTROS";

        // Marca: si vino MarcaId, busco el nombre en Cafe_Marcas para tambien escribir el
        // campo legacy "Marca" (texto). Si no, queda sin marca.
        string? marcaNombre = null;
        if (req.MarcaId.HasValue)
        {
            marcaNombre = await _db.CafeMarcas
                .Where(m => m.Id == req.MarcaId.Value)
                .Select(m => m.Nombre)
                .FirstOrDefaultAsync();
            if (marcaNombre is null)
                throw new InvalidOperationException("La marca seleccionada no existe.");
        }

        foreach (var skuRaw in req.Skus.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var sku = skuRaw.Trim();
            if (string.IsNullOrEmpty(sku)) continue;

            // Si ya hay un CafeProducto con ese SKU, no lo duplicamos.
            var existente = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Sku == sku);

            CafeProducto producto;
            if (existente is not null)
            {
                producto = existente;
                detalles.Add($"{sku}: ya existia, solo vinculo.");
                omitidos++;
            }
            else
            {
                // Tomamos los datos de Contabilium.
                var contab = await _db.ContabProductos.FirstOrDefaultAsync(p => p.Sku == sku);
                if (contab is null)
                {
                    detalles.Add($"{sku}: no esta en Contab_Productos, omito.");
                    omitidos++;
                    continue;
                }

                producto = new CafeProducto
                {
                    Sku = sku,
                    Nombre = string.IsNullOrWhiteSpace(contab.Nombre) ? sku : contab.Nombre!,
                    Barcode = contab.CodigoBarras,
                    Categoria = categoriaFinal,
                    MarcaId = req.MarcaId,
                    Marca = marcaNombre, // texto legacy, igual a la marca elegida
                    Costo = contab.CostoInterno ?? 0m,
                    Pvp1 = contab.PrecioFinal,
                    StockUnidades = (int)Math.Round(contab.Stock ?? 0m),
                    StockGramos = 0m,
                    Notas = string.IsNullOrWhiteSpace(contab.Proveedor) ? null : $"Proveedor Contabilium: {contab.Proveedor}",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };
                _db.CafeProductos.Add(producto);
                await _db.SaveChangesAsync();
                creados++;
                detalles.Add($"{sku}: creado.");
            }

            // Vincular todas las MeliItems con ese SKU al CafeProducto.
            var pubs = await _db.MeliItems
                .Where(i => i.Sku == sku && i.CafeProductoId == null)
                .ToListAsync();
            foreach (var p in pubs) p.CafeProductoId = producto.Id;
            if (pubs.Count > 0)
            {
                await _db.SaveChangesAsync();
                vinculados += pubs.Count;
            }
        }

        return new CrearProductosResultado(creados, vinculados, omitidos, detalles);
    }
}
