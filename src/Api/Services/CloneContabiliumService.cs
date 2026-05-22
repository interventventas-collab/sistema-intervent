using System.Text.RegularExpressions;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Clone DEFINITIVO de Contabilium al sistema nuevo.
///
/// Diferencia con ContabiliumImportService:
///   - Importa productos (igual que antes)
///   - Importa COMBOS completos a Cafe_Combos + Cafe_ComboItems (nuevo, no existia)
///   - Expande combos anidados recursivamente
///   - Re-linkea MeliItemComponentes respetando variation_id (arregla bug de variantes)
///   - Marca componentes-solo como IsVisibleEnVentas=false (no aparecen en buscador de ventas)
///
/// Usa los datos YA importados en Contab_Productos / Contab_Combos / Contab_ComboItems
/// (no toca la API de Contabilium — es responsabilidad de ContabiliumStagingService).
///
/// Es idempotente: corrida 2 veces NO duplica nada. Reutiliza filas existentes por SKU.
/// </summary>
public class CloneContabiliumService
{
    private readonly AppDbContext _db;
    private readonly ILogger<CloneContabiliumService> _logger;

    public const string ImportSourceTag = "CONTABILIUM_CLONE_2026_05_22";

    public CloneContabiliumService(AppDbContext db, ILogger<CloneContabiliumService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public record CloneResult(
        int ProductosCreados, int ProductosActualizados,
        int CombosCreados, int CombosActualizados,
        int MappingsCreados, int ItemsConVariante, int ItemsSinMatch,
        List<string> Warnings);

    /// <summary>Regex para detectar SKUs de cafe (F1, F12, F2.5, FR3, etc) — esos NO se importan como producto suelto
    /// (los cafes ya viven en Cafe_Productos con SKU F#). PERO sí se mapean cuando aparecen como componente
    /// de combo (ej: combo "BR1KG-IT1KG-VIE1KG" contiene FR2, FR3, FR5 → mapear a F2, F3, F5 con formato 1KG).</summary>
    private static readonly Regex EsCafeRegex = new(@"^F[R]?\d+(\.\d+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parsea un SKU café de Contabilium y devuelve (skuSistema, formato) o null si no es café.
    /// Ejemplos: FR2 → (F2, 1KG); FR2.2 → (F2, MEDIO); FR2.4 → (F2, CUARTO); F1 → (F1, 1KG); F1.2 → (F1, MEDIO).</summary>
    private static (string skuSistema, string formato)? ParseCafeSku(string sku)
    {
        var m = Regex.Match(sku, @"^F[R]?(?<num>\d+)(\.(?<frac>\d))?$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var num = m.Groups["num"].Value;
        var frac = m.Groups["frac"].Success ? m.Groups["frac"].Value : null;
        var formato = frac switch { "2" => "MEDIO", "4" => "CUARTO", _ => "1KG" };
        return ($"F{num}", formato);
    }

    public async Task<CloneResult> RunCloneAsync(CancellationToken ct = default)
    {
        var warnings = new List<string>();
        _logger.LogInformation("[CloneContab] Iniciando clone de Contabilium...");

        // 1) Cargar staging (Contab_*)
        var contabProductos = await _db.ContabProductos
            .Where(p => p.Tipo == "Producto" && !string.IsNullOrEmpty(p.Sku))
            .ToListAsync(ct);
        var contabProdsBySku = contabProductos
            .GroupBy(p => p.Sku.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First());

        var contabCombos = await _db.ContabCombos
            .Where(c => !string.IsNullOrEmpty(c.SkuCombo))
            .ToListAsync(ct);
        var contabCombosBySku = contabCombos
            .GroupBy(c => c.SkuCombo.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First());

        var contabComboItems = await _db.ContabComboItems.ToListAsync(ct);
        var contabComboItemsBySkuCombo = contabComboItems
            .GroupBy(ci => ci.SkuCombo.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.ToList());

        _logger.LogInformation("[CloneContab] Staging: {P} productos, {C} combos, {CI} items combo",
            contabProductos.Count, contabCombos.Count, contabComboItems.Count);

        // 2) MeliItems con SKU — la fuente de verdad de "que se vende"
        var meliItems = await _db.MeliItems
            .Where(mi => mi.Sku != null && mi.Sku != "")
            .ToListAsync(ct);

        // SKUs unicos publicados en MeLi (todas las variantes pueden compartir SKU o no)
        var meliSkusUnique = meliItems
            .Select(mi => (mi.Sku ?? "").Trim().ToUpperInvariant())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 3) Calcular sets:
        //   Set A: SKUs de productos puros vendidos directo en MeLi
        //   Set B: SKUs de combos vendidos en MeLi
        //   Set C: SKUs componentes de combos en Set B (recursivamente para anidados)
        var setA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sku in meliSkusUnique)
        {
            if (EsCafeRegex.IsMatch(sku)) continue;
            if (contabProdsBySku.ContainsKey(sku)) setA.Add(sku);
            else if (contabCombosBySku.ContainsKey(sku)) setB.Add(sku);
            // si no es ninguno → es un SKU MeLi sin contraparte en Contabilium (warning, no error)
        }

        // Para cada combo del Set B, expandir recursivamente sus componentes a productos puros
        // (componentes que sean a su vez combos se expanden hasta llegar a productos).
        // Resultado: para cada SkuCombo (B), una lista de (skuComponenteProducto, cantidadFinal)
        var combosExpandidos = new Dictionary<string, List<(string skuProd, decimal cantidad)>>(StringComparer.OrdinalIgnoreCase);
        var setC = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var skuCombo in setB)
        {
            var expanded = new List<(string skuProd, decimal cantidad)>();
            ExpandirCombo(skuCombo, 1m, expanded, contabProdsBySku, contabCombosBySku, contabComboItemsBySkuCombo,
                visiting: new HashSet<string>(StringComparer.OrdinalIgnoreCase), warnings);
            // Agregar cantidades de componentes duplicados (mismo skuProd → sumar cantidad)
            var agg = expanded
                .GroupBy(e => e.skuProd.Trim().ToUpperInvariant())
                .Select(g => (skuProd: g.Key, cantidad: g.Sum(x => x.cantidad)))
                .ToList();
            combosExpandidos[skuCombo] = agg;
            foreach (var (sp, _) in agg)
            {
                if (!EsCafeRegex.IsMatch(sp)) setC.Add(sp);
            }
        }

        _logger.LogInformation("[CloneContab] Set A (productos directos): {A}, Set B (combos): {B}, Set C (componentes): {C}",
            setA.Count, setB.Count, setC.Count);

        // 4) Productos a importar = A ∪ C (todos los productos puros relevantes)
        var skusProductosAImportar = new HashSet<string>(setA, StringComparer.OrdinalIgnoreCase);
        skusProductosAImportar.UnionWith(setC);

        // 5) Cargar productos existentes en Cafe_Productos por SKU
        var existingProds = await _db.CafeProductos
            .Where(p => p.Sku != null)
            .ToListAsync(ct);
        var existingProdsBySku = existingProds
            .Where(p => !string.IsNullOrEmpty(p.Sku))
            .GroupBy(p => p.Sku!.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        int prodsCreados = 0, prodsActualizados = 0;

        // 6) Importar productos
        foreach (var sku in skusProductosAImportar)
        {
            if (EsCafeRegex.IsMatch(sku)) continue;
            if (!contabProdsBySku.TryGetValue(sku, out var con)) continue;

            // Visibilidad en ventas: true si esta en A (vendido directo), false si SOLO en C
            bool isVisible = setA.Contains(sku);

            if (existingProdsBySku.TryGetValue(sku, out var prod))
            {
                // Actualizar stock + visibilidad (si se mantiene en setA, queda visible; si solo en C, NO pisar
                // visibilidad pre-existente — el usuario puede tenerla en true a proposito)
                prod.StockUnidades = (int)Math.Round(con.Stock ?? 0m);
                // Si el producto no tenia ImportSource (creado a mano), no lo pisar.
                // Si lo tenia del clone, mantener el tag actualizado.
                if (prod.ImportSource == ImportSourceTag || string.IsNullOrEmpty(prod.ImportSource))
                {
                    prod.ImportSource = ImportSourceTag;
                    // Si el producto fue creado por el clone Y volvio a aparecer en setA, marcamos visible
                    if (isVisible && !prod.IsVisibleEnVentas)
                        prod.IsVisibleEnVentas = true;
                }
                prod.UpdatedAt = DateTime.UtcNow;
                prodsActualizados++;
            }
            else
            {
                var nombreFull = (con.Nombre ?? sku).Trim();
                var nombre = nombreFull.Length > 200 ? nombreFull.Substring(0, 197) + "..." : nombreFull;
                var skuTrim = sku.Length > 50 ? sku.Substring(0, 50) : sku;
                var nuevo = new CafeProducto
                {
                    Sku = skuTrim,
                    Nombre = nombre,
                    Categoria = "OTROS",
                    Costo = con.CostoInterno ?? 0m,
                    StockUnidades = (int)Math.Round(con.Stock ?? 0m),
                    IsActive = string.Equals(con.Estado, "Activo", StringComparison.OrdinalIgnoreCase),
                    IsVisibleEnVentas = isVisible,
                    ImportSource = ImportSourceTag,
                    CreatedAt = DateTime.UtcNow
                };
                if (con.PrecioFinal.HasValue && con.PrecioFinal.Value > 0)
                {
                    nuevo.Pvp2 = con.PrecioFinal.Value;
                    nuevo.PrecioOtro = con.PrecioFinal.Value;
                }
                _db.CafeProductos.Add(nuevo);
                existingProdsBySku[sku] = nuevo;
                prodsCreados++;
            }
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("[CloneContab] Productos: {C} creados, {U} actualizados", prodsCreados, prodsActualizados);

        // 7) Importar COMBOS (Set B) → Cafe_Combos + Cafe_ComboItems
        //    Para cada combo: usar la lista EXPANDIDA (componentes resueltos a productos puros).
        var existingCombos = await _db.CafeCombos
            .Where(c => c.Sku != null)
            .Include(c => c.Items)
            .ToListAsync(ct);
        var existingCombosBySku = existingCombos
            .Where(c => !string.IsNullOrEmpty(c.Sku))
            .GroupBy(c => c.Sku!.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        int combosCreados = 0, combosActualizados = 0;

        foreach (var skuCombo in setB)
        {
            if (!contabCombosBySku.TryGetValue(skuCombo, out var conCombo)) continue;
            var expansion = combosExpandidos.GetValueOrDefault(skuCombo) ?? new List<(string, decimal)>();
            if (expansion.Count == 0)
            {
                warnings.Add($"Combo {skuCombo}: sin componentes despues de expandir (skip)");
                continue;
            }

            var nombre = (conCombo.Nombre ?? skuCombo).Trim();
            if (nombre.Length > 200) nombre = nombre.Substring(0, 197) + "...";
            var skuTrim = skuCombo.Length > 80 ? skuCombo.Substring(0, 80) : skuCombo;

            CafeCombo combo;
            if (existingCombosBySku.TryGetValue(skuCombo, out var existing))
            {
                combo = existing;
                combo.Nombre = nombre;
                combo.Categoria = "OTROS";
                combo.PrecioReferencia = conCombo.PrecioFinal ?? 0m;
                combo.IsActive = string.Equals(conCombo.Estado, "Activo", StringComparison.OrdinalIgnoreCase);
                combo.ImportSource = ImportSourceTag;
                combo.UpdatedAt = DateTime.UtcNow;
                // Limpiar items para recrear (re-expansion puede cambiar la composicion)
                _db.CafeComboItems.RemoveRange(combo.Items);
                combo.Items.Clear();
                combosActualizados++;
            }
            else
            {
                combo = new CafeCombo
                {
                    Sku = skuTrim,
                    Nombre = nombre,
                    Categoria = "OTROS",
                    PrecioReferencia = conCombo.PrecioFinal ?? 0m,
                    IsActive = string.Equals(conCombo.Estado, "Activo", StringComparison.OrdinalIgnoreCase),
                    ImportSource = ImportSourceTag,
                    CreatedAt = DateTime.UtcNow
                };
                _db.CafeCombos.Add(combo);
                existingCombosBySku[skuCombo] = combo;
                combosCreados++;
            }

            // Agregar items expandidos. Cantidad es int en Cafe_ComboItems → Math.Ceiling para no perder
            foreach (var (skuComp, cantidad) in expansion)
            {
                // ─── CAFE: mapear FR2 → F2 (1KG), FR2.2 → F2 (MEDIO), FR2.4 → F2 (CUARTO) ───
                var cafeMap = ParseCafeSku(skuComp);
                if (cafeMap is not null)
                {
                    var (skuSistema, formato) = cafeMap.Value;
                    if (!existingProdsBySku.TryGetValue(skuSistema, out var prodCafe))
                    {
                        warnings.Add($"Combo {skuCombo}: cafe {skuComp} → {skuSistema} no encontrado en Cafe_Productos");
                        continue;
                    }
                    int cantCafe = (int)Math.Max(1, Math.Ceiling(cantidad));
                    combo.Items.Add(new CafeComboItem
                    {
                        ProductoId = prodCafe.Id,
                        Formato = formato,
                        Cantidad = cantCafe,
                        Molienda = null,
                        EsDoyPack = false,
                        EsEnvasePlateado = false,
                        SortOrder = combo.Items.Count
                    });
                    continue;
                }
                if (!existingProdsBySku.TryGetValue(skuComp, out var prod))
                {
                    warnings.Add($"Combo {skuCombo}: componente {skuComp} no encontrado en Cafe_Productos");
                    continue;
                }
                // Cantidad: round-up para no perder fracciones (mejor descontar mas que de menos)
                int cant = (int)Math.Max(1, Math.Ceiling(cantidad));
                combo.Items.Add(new CafeComboItem
                {
                    ProductoId = prod.Id,
                    Formato = "UNIT",
                    Cantidad = cant,
                    Molienda = null,
                    EsDoyPack = false,
                    EsEnvasePlateado = false,
                    SortOrder = combo.Items.Count
                });
            }
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("[CloneContab] Combos: {C} creados, {U} actualizados", combosCreados, combosActualizados);

        // 8) Re-linkear MeliItemComponentes — RESPETANDO variation_id
        // DELETE de los que NO son manuales (preservamos los que el usuario linkeo a mano)
        await _db.Database.ExecuteSqlRawAsync(
            "DELETE FROM MeliItemComponentes WHERE Source IS NULL OR Source <> 'manual'", ct);

        int mappingsCreados = 0;
        int itemsConVariante = 0;
        int itemsSinMatch = 0;

        // Re-cargar productos por id para mapear
        existingProds = await _db.CafeProductos
            .Where(p => p.Sku != null)
            .ToListAsync(ct);
        var prodsBySku = existingProds
            .Where(p => !string.IsNullOrEmpty(p.Sku))
            .GroupBy(p => p.Sku!.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var mi in meliItems)
        {
            var sku = (mi.Sku ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(sku)) continue;
            // Café — el linkeo viejo en MeliItem.CafeProductoId+CafeFormato sigue siendo el oficial
            if (EsCafeRegex.IsMatch(sku)) continue;

            // ¿Es combo expandido?
            if (combosExpandidos.TryGetValue(sku, out var comps) && comps.Count > 0)
            {
                bool huboMatch = false;
                foreach (var (cSku, cCant) in comps)
                {
                    // CAFE en combo: mapear FR2→F2 (1KG), FR2.2→F2 (MEDIO), FR2.4→F2 (CUARTO).
                    var cafeMap = ParseCafeSku(cSku);
                    if (cafeMap is not null)
                    {
                        var (skuSis, formato) = cafeMap.Value;
                        if (prodsBySku.TryGetValue(skuSis, out var prodCafe))
                        {
                            _db.MeliItemComponentes.Add(new MeliItemComponente
                            {
                                MeliItemId = mi.MeliItemId,
                                MeliVariationId = string.IsNullOrEmpty(mi.VariationId) ? null : mi.VariationId,
                                CafeProductoId = prodCafe.Id,
                                Cantidad = cCant,
                                Formato = formato,
                                Source = "combo-contabilium",
                                CreatedAt = DateTime.UtcNow
                            });
                            mappingsCreados++;
                            huboMatch = true;
                        }
                        continue;
                    }
                    if (!prodsBySku.TryGetValue(cSku, out var prod)) continue;
                    _db.MeliItemComponentes.Add(new MeliItemComponente
                    {
                        MeliItemId = mi.MeliItemId,
                        MeliVariationId = string.IsNullOrEmpty(mi.VariationId) ? null : mi.VariationId,
                        CafeProductoId = prod.Id,
                        Cantidad = cCant,
                        Source = "combo-contabilium",
                        CreatedAt = DateTime.UtcNow
                    });
                    mappingsCreados++;
                    huboMatch = true;
                }
                if (huboMatch && !string.IsNullOrEmpty(mi.VariationId)) itemsConVariante++;
                if (!huboMatch) itemsSinMatch++;
            }
            else if (prodsBySku.TryGetValue(sku, out var prod))
            {
                _db.MeliItemComponentes.Add(new MeliItemComponente
                {
                    MeliItemId = mi.MeliItemId,
                    MeliVariationId = string.IsNullOrEmpty(mi.VariationId) ? null : mi.VariationId,
                    CafeProductoId = prod.Id,
                    Cantidad = 1m,
                    Source = "meli-sku-directo",
                    CreatedAt = DateTime.UtcNow
                });
                mappingsCreados++;
                if (!string.IsNullOrEmpty(mi.VariationId)) itemsConVariante++;
            }
            else
            {
                itemsSinMatch++;
            }
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("[CloneContab] Mappings: {M} creados ({V} con variante, {S} sin match)",
            mappingsCreados, itemsConVariante, itemsSinMatch);

        return new CloneResult(
            prodsCreados, prodsActualizados,
            combosCreados, combosActualizados,
            mappingsCreados, itemsConVariante, itemsSinMatch,
            warnings);
    }

    /// <summary>
    /// Expande recursivamente un combo a productos puros. Multiplica cantidades.
    /// Si un componente es a su vez un combo, lo expande. Detecta ciclos con `visiting`.
    /// </summary>
    private static void ExpandirCombo(
        string skuComboActual,
        decimal multiplicador,
        List<(string skuProd, decimal cantidad)> acumulador,
        Dictionary<string, ContabProducto> productos,
        Dictionary<string, ContabCombo> combos,
        Dictionary<string, List<ContabComboItem>> comboItems,
        HashSet<string> visiting,
        List<string> warnings)
    {
        var keyCombo = skuComboActual.Trim().ToUpperInvariant();
        if (!visiting.Add(keyCombo))
        {
            warnings.Add($"Combo {skuComboActual}: ciclo detectado al expandir, skip");
            return;
        }
        try
        {
            if (!comboItems.TryGetValue(keyCombo, out var items)) return;
            foreach (var it in items)
            {
                var skuComp = (it.SkuComponente ?? "").Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(skuComp)) continue;
                var cantTotal = it.Cantidad * multiplicador;
                if (productos.ContainsKey(skuComp))
                {
                    acumulador.Add((skuComp, cantTotal));
                }
                else if (combos.ContainsKey(skuComp))
                {
                    // Combo anidado → expandir
                    ExpandirCombo(skuComp, cantTotal, acumulador, productos, combos, comboItems, visiting, warnings);
                }
                else
                {
                    warnings.Add($"Combo {skuComboActual}: componente {skuComp} no es ni producto ni combo, skip");
                }
            }
        }
        finally
        {
            visiting.Remove(keyCombo);
        }
    }
}
