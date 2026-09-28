using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-09-28 — Pedido del dueño: que los productos que en realidad son "tacho + tapa" o "caja + tapa"
/// muestren al final del nombre, entre paréntesis, los códigos que los componen. Ej: C9234TT
/// "CAJA X 34 LTS COLOMBRARO" → "(9234TR + 7045-TR)". Así no se carga stock en el producto equivocado.
///
/// Esto es para los productos "shell": un producto del catálogo que no tiene stock propio, sino que
/// está linkeado a publicaciones MeLi cuyas componentes son las piezas reales. Los compuestos de
/// Cafe_Combos arman su paréntesis en el frontend con sus propios items.
/// </summary>
public static class ComposicionProductos
{
    /// <summary>Devuelve productoId → "(A + B)" solo para los productos shell. Si se pasan ids, se limita a esos.</summary>
    public static async Task<Dictionary<int, string>> ShellsAsync(AppDbContext db, ICollection<int>? productoIds = null)
    {
        var result = new Dictionary<int, string>();

        // Sin Contains(ids) en SQL: con ~2000 productos se pasa del límite de parámetros de SQL Server.
        var meliItems = await db.MeliItems.AsNoTracking()
            .Where(mi => mi.CafeProductoId != null && (mi.Status == "active" || mi.Status == "paused"))
            .Select(mi => new { mi.MeliItemId, ProdId = mi.CafeProductoId!.Value })
            .ToListAsync();
        if (productoIds is not null)
        {
            var set = productoIds as HashSet<int> ?? productoIds.ToHashSet();
            meliItems = meliItems.Where(x => set.Contains(x.ProdId)).ToList();
        }
        if (meliItems.Count == 0) return result;

        var meliIds = meliItems.Select(x => x.MeliItemId).Distinct().ToHashSet();
        // Solo las componentes de publicaciones linkeadas a un producto (se filtra en SQL con un join).
        var comps = (await db.MeliItemComponentes.AsNoTracking()
                .Where(c => db.MeliItems.Any(mi => mi.MeliItemId == c.MeliItemId && mi.CafeProductoId != null
                                                   && (mi.Status == "active" || mi.Status == "paused")))
                .Select(c => new { c.Id, c.MeliItemId, c.MeliVariationId, c.CafeProductoId, c.Cantidad })
                .ToListAsync())
            .Where(c => meliIds.Contains(c.MeliItemId))
            .ToList();
        if (comps.Count == 0) return result;

        var compProdIds = comps.Select(c => c.CafeProductoId).Distinct().ToList();
        var skus = (await db.CafeProductos.AsNoTracking()
                .Select(p => new { p.Id, p.Sku })
                .ToListAsync())
            .Where(p => compProdIds.Contains(p.Id))
            .ToDictionary(p => p.Id, p => p.Sku ?? "?");

        var compsByItem = comps.GroupBy(c => c.MeliItemId).ToDictionary(g => g.Key, g => g.ToList());

        // Las componentes de MeLi tienen mucha basura vieja (ej. publicaciones multicolor donde el azul
        // figura "compuesto" del rojo). Por eso solo se acepta una composición que coincida EXACTO con la
        // de un compuesto real de Cafe_Combos (ej. C9234TT = 9234TR + 7045-TR = compuesto 9234TR-TR).
        var compuestosValidos = (await db.CafeCombos.AsNoTracking()
                .Where(c => c.IsActive && c.EsCompuesto)
                .Select(c => c.Items.Select(i => new { i.ProductoId, i.Cantidad }).ToList())
                .ToListAsync())
            .Where(items => items.Count > 0)
            .Select(items => Clave(items.Select(i => (i.ProductoId, (decimal)i.Cantidad))))
            .ToHashSet();

        foreach (var prodGroup in meliItems.GroupBy(x => x.ProdId))
        {
            // Cada publicación (y cada variante) propone una composición; gana la que más se repite.
            var candidatas = new List<string>();
            foreach (var mi in prodGroup)
            {
                if (!compsByItem.TryGetValue(mi.MeliItemId, out var lista)) continue;
                foreach (var porVariante in lista.GroupBy(c => c.MeliVariationId ?? ""))
                {
                    // Mismo criterio que el stock armable: ignorar las autorreferencias (basura de Contabilium).
                    var validas = porVariante.Where(c => c.CafeProductoId != prodGroup.Key).OrderBy(c => c.Id).ToList();
                    // Un armado de verdad tiene al menos 2 piezas distintas (tacho + tapa, caja + tapa).
                    if (validas.Select(c => c.CafeProductoId).Distinct().Count() < 2) continue;
                    if (!compuestosValidos.Contains(Clave(validas.Select(c => (c.CafeProductoId, c.Cantidad))))) continue;
                    candidatas.Add("(" + string.Join(" + ", validas.Select(c =>
                        (c.Cantidad == 1m ? "" : $"{c.Cantidad:0.##}× ") + (skus.TryGetValue(c.CafeProductoId, out var s) ? s : "?"))) + ")");
                }
            }
            if (candidatas.Count == 0) continue;
            result[prodGroup.Key] = candidatas.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
        }
        return result;
    }

    /// <summary>Clave canónica de una composición: "12x1|40x1" (producto × cantidad, ordenado).</summary>
    private static string Clave(IEnumerable<(int ProductoId, decimal Cantidad)> items) =>
        string.Join("|", items.GroupBy(i => i.ProductoId)
            .Select(g => $"{g.Key}x{g.Sum(x => x.Cantidad):0.####}")
            .OrderBy(x => x, StringComparer.Ordinal));
}
