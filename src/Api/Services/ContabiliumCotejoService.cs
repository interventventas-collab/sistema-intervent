using Api.Data;
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
        decimal? ContabPrecioFinal,
        decimal? ContabStock,
        bool ComboTieneFaltantes,
        int? ProductIdVinculado,
        int? ComboIdVinculado);

    public async Task<List<CotejoFila>> ListarAsync(string categoria = "todos", string? buscar = null, int? meliAccountId = null, int take = 200)
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
                i.Title, i.Sku, i.Price, i.AvailableQuantity, i.ProductId, i.ComboId
            })
            .ToListAsync();

        // Lookup de Contabilium por SKU (en lower).
        var skus = meliRows.Where(r => !string.IsNullOrEmpty(r.Sku))
            .Select(r => r.Sku!.Trim().ToLower()).Distinct().ToList();

        var contabProds = await _db.ContabProductos
            .Where(p => skus.Contains(p.Sku.ToLower()))
            .Select(p => new { p.Sku, p.Nombre, p.PrecioFinal, p.Stock })
            .ToListAsync();
        var prodLookup = contabProds.ToDictionary(p => p.Sku.ToLowerInvariant(), p => (p.Nombre, p.PrecioFinal, p.Stock));

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
            }
            else
            {
                cat = "huerfano";
            }

            rows.Add(new CotejoFila(
                r.Id, r.MeliItemId, r.VariationId, r.VariationAttributes,
                r.Title, r.Sku, cat, r.Price, r.AvailableQuantity,
                cName, cPF, cStock, comboBroken, r.ProductId, r.ComboId));

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
}
