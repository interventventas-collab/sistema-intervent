namespace Api.Services;

/// <summary>
/// 2026-09-18 — Publicaciones con COLORES (variaciones) donde cada color está vinculado a su propio
/// producto. El vínculo automático del 08/06 ("auto-link-prod") cargó un componente por color SIN
/// decir de qué color es (MeliVariationId = null). El costo se calculaba sumando todos los
/// componentes —correcto en un combo, donde de verdad se venden juntos— y en una publicación con
/// colores eso es cobrar 4 bateas por una.
///
/// Caso real: MLA686863575, batea N°1 en 4 colores. Costo de una: $5.658. El sistema usaba
/// 4 × $5.658 = $22.632 y con el objetivo del 50% la publicó a $63.299 en vez de ~$27.300.
/// Igual la mesita infantil (5 colores) a $300.899. Medido en prod el 18/09: 8 publicaciones.
///
/// Regla: si los componentes son "uno por color" (todos sin variación y cada uno es el producto de
/// una de las filas-color de la publicación), el costo de UNA unidad es el del color de la fila.
/// Sin fila (cuentas por publicación): el del color más caro, para no mostrar un margen de más.
/// Cualquier otro caso (combos, packs, recetas) no se toca: ahí sumar está bien.
/// </summary>
public static class MeliCostoPorColor
{
    public static List<T> ComponentesDeUnaUnidad<T>(
        List<T> comps,
        Func<T, int> producto,
        Func<T, string?> variacion,
        Func<T, decimal> costo,
        IReadOnlyCollection<int> productosDeLosColores,
        string? variationIdFila = null,
        int? productoIdFila = null)
    {
        if (comps.Count == 0) return comps;

        // Componentes cargados con su color: se usan los de esta fila.
        if (!string.IsNullOrEmpty(variationIdFila))
        {
            var propios = comps.Where(c => variacion(c) == variationIdFila).ToList();
            if (propios.Count > 0) return propios;
        }

        if (!EsUnoPorColor(comps, producto, variacion, productosDeLosColores)) return comps;

        if (productoIdFila.HasValue)
        {
            var delColor = comps.Where(c => producto(c) == productoIdFila.Value).ToList();
            if (delColor.Count > 0) return delColor.Take(1).ToList();
        }
        return comps.OrderByDescending(costo).Take(1).ToList();
    }

    private static bool EsUnoPorColor<T>(List<T> comps, Func<T, int> producto, Func<T, string?> variacion,
        IReadOnlyCollection<int> productosDeLosColores)
    {
        if (productosDeLosColores.Count < 2) return false;
        var distintos = comps.Select(producto).Distinct().Count();
        return distintos >= 2
            && comps.All(c => string.IsNullOrEmpty(variacion(c)) && productosDeLosColores.Contains(producto(c)));
    }
}
