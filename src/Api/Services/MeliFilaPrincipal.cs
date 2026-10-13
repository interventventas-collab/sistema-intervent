using Api.Models;

namespace Api.Services;

/// <summary>
/// 10/2026 — La fila que representa a UNA publicación en MeliItems.
///
/// Una publicación común tiene una sola fila (VariationId null). Una con COLORES (variantes) no
/// tiene esa fila: tiene una por color. Muchas acciones de la pantalla buscaban sólo la fila sin
/// variante y con las de colores decían "no encuentro esta publicación" (ej. gavetero MLA915155790).
/// Esto devuelve la fila sin variante si existe; si no, la del primer color (menor Id), que es la
/// misma que muestra la pantalla nueva y donde se guarda la comisión.
/// </summary>
public static class MeliFilaPrincipal
{
    public static IQueryable<MeliItem> FilaPrincipal(this IQueryable<MeliItem> q, string meliItemId)
        => q.Where(i => i.MeliItemId == meliItemId)
            .OrderBy(i => i.VariationId == null ? 0 : 1).ThenBy(i => i.Id);
}
