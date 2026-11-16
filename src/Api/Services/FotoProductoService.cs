using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-11-16: UNA sola regla para la foto de MercadoLibre de cada producto, en TODO el sistema
/// (Productos, Preparación/armado, QR del depósito, Tareas del depósito, Stock ideal, Vencimientos).
/// Pedido del dueño: "si estaba mal en una pantalla estaba mal en todas". Antes cada pantalla agarraba
/// "la publi más nueva" o "la más vendida" y muchas veces era un PACK (x2, x4, x12) o, para una TAPA,
/// la publi del tacho/caja CON tapa.
///
/// Orden de preferencia (por publicación y variante de color):
///   0. el producto SOLO, de a 1 unidad
///   1. link viejo (MeliItem.CafeProductoId) sin componentes cargados para él: no se sabe, se confía
///   2. el producto es la pieza PRINCIPAL de un armado de a 1 (el tacho de "tacho + tapa")
///   3. pack de él mismo (x2, x12…): es el producto correcto, aunque sean varios
///   — nunca: ser el ACCESORIO de otro (la tapa en "caja con tapa") ni packs mezclados. Mejor sin foto
///     (se ve 📦 y piden foto propia) que una foto de otra cosa.
/// Dentro de cada nivel: publi activa antes que pausada, link directo, la más reciente.
/// La foto es la de la VARIANTE (color) de ese producto cuando la publi tiene colores.
/// La foto PROPIA del sistema y el "No mostrar" (REPORTADA) se resuelven en <see cref="FotosAsync"/>.
/// </summary>
public class FotoProductoService
{
    private readonly AppDbContext _db;
    public FotoProductoService(AppDbContext db) => _db = db;

    public const string UrlPropia = "/api/public/producto-foto/img/";

    /// <summary>productoId → miniatura de MeLi (https) según la regla de arriba. ids null = todos.</summary>
    public async Task<Dictionary<int, string>> MeliAsync(ICollection<int>? ids = null)
    {
        var res = new Dictionary<int, string>();
        if (ids is { Count: 0 }) return res;
        var idList = ids?.Distinct().ToList();

        // Publicaciones que tocan a estos productos (por componente o por el link viejo)
        var compsQ = _db.MeliItemComponentes.AsNoTracking();
        var itemsDeComp = idList is null
            ? compsQ.Select(c => c.MeliItemId)
            : compsQ.Where(c => idList.Contains(c.CafeProductoId)).Select(c => c.MeliItemId);
        var itemsLegacy = idList is null
            ? _db.MeliItems.Where(m => m.CafeProductoId != null).Select(m => m.MeliItemId)
            : _db.MeliItems.Where(m => m.CafeProductoId != null && idList.Contains(m.CafeProductoId.Value)).Select(m => m.MeliItemId);
        var itemIds = (await itemsDeComp.Union(itemsLegacy).Distinct().ToListAsync()).ToHashSet();
        if (itemIds.Count == 0) return res;

        var itemList = itemIds.ToList();
        var filas = await _db.MeliItems.AsNoTracking()
            .Where(m => itemList.Contains(m.MeliItemId) && m.Thumbnail != null && m.Thumbnail != "" && m.Status != "deleted")
            .Select(m => new { m.MeliItemId, m.VariationId, m.CafeProductoId, m.Thumbnail, m.Status, m.UpdatedAt })
            .ToListAsync();
        if (filas.Count == 0) return res;
        var filasPorItem = filas.GroupBy(f => f.MeliItemId).ToDictionary(g => g.Key, g => g.ToList());

        // Composición COMPLETA de esas publis (para saber si el producto va solo o con otros)
        var conFoto = filasPorItem.Keys.ToList();
        var comps = (await compsQ.Where(c => conFoto.Contains(c.MeliItemId))
                .Select(c => new { c.MeliItemId, c.MeliVariationId, c.CafeProductoId, c.Cantidad }).ToListAsync())
            .Distinct().ToList();

        // Costo y nombre para saber cuál es la pieza principal de un armado (la tapa es el accesorio)
        var pidsComps = comps.Select(c => c.CafeProductoId).Distinct().ToList();
        var prods = await _db.CafeProductos.AsNoTracking().Where(p => pidsComps.Contains(p.Id))
            .Select(p => new { p.Id, p.Nombre, p.Costo }).ToDictionaryAsync(p => p.Id);
        bool EsTapa(int pid) => prods.TryGetValue(pid, out var p) && p.Nombre.TrimStart().StartsWith("TAPA", StringComparison.OrdinalIgnoreCase);
        decimal CostoDe(int pid) => prods.TryGetValue(pid, out var p) ? p.Costo : 0m;

        var cands = new List<(int Pid, int Nivel, string Thumb, bool Activa, bool Directo, DateTime? Upd)>();

        // Fila de la publi que corresponde a esa variante (color); si no hay, cualquiera (activa primero)
        (string Thumb, bool Activa, DateTime? Upd, int? Directo)? FilaDe(string item, string? variacion)
        {
            if (!filasPorItem.TryGetValue(item, out var fs)) return null;
            var f = (variacion != null ? fs.FirstOrDefault(x => x.VariationId == variacion) : null)
                    ?? fs.OrderByDescending(x => x.Status == "active").ThenByDescending(x => x.UpdatedAt).First();
            return (f.Thumbnail!, f.Status == "active", f.UpdatedAt, f.CafeProductoId);
        }

        foreach (var g in comps.GroupBy(c => (c.MeliItemId, c.MeliVariationId)))
        {
            var porProd = g.GroupBy(c => c.CafeProductoId).ToDictionary(x => x.Key, x => x.Max(c => c.Cantidad));
            var fila = FilaDe(g.Key.MeliItemId, g.Key.MeliVariationId);
            if (fila is null) continue;
            foreach (var (pid, cant) in porProd)
            {
                if (idList is not null && !idList.Contains(pid)) continue;
                var otros = porProd.Keys.Where(k => k != pid).ToList();
                int nivel;
                if (otros.Count == 0) nivel = cant <= 1m ? 0 : 3;
                else
                {
                    var principal = !EsTapa(pid) && (otros.All(EsTapa) || otros.All(o => CostoDe(o) <= CostoDe(pid)));
                    if (!principal || cant > 1m) continue; // accesorio o pack mezclado: esa foto no es de él
                    nivel = 2;
                }
                var f = fila.Value;
                cands.Add((pid, nivel, f.Thumb, f.Activa, f.Directo == pid, f.Upd));
            }
        }

        // Link viejo sin componentes cargados para ese producto
        var conComp = comps.Select(c => (c.MeliItemId, c.CafeProductoId)).ToHashSet();
        foreach (var f in filas.Where(f => f.CafeProductoId != null))
        {
            var pid = f.CafeProductoId!.Value;
            if (idList is not null && !idList.Contains(pid)) continue;
            if (conComp.Contains((f.MeliItemId, pid))) continue;
            cands.Add((pid, 1, f.Thumbnail!, f.Status == "active", true, f.UpdatedAt));
        }

        foreach (var g in cands.GroupBy(c => c.Pid))
        {
            var best = g.OrderBy(c => c.Nivel)
                .ThenByDescending(c => c.Activa)
                .ThenByDescending(c => c.Directo)
                .ThenByDescending(c => c.Upd)
                .First();
            res[g.Key] = best.Thumb.Replace("http://", "https://");
        }
        return res;
    }

    public record Foto(string Url, bool Propia);

    /// <summary>productoId → foto que se muestra: la PROPIA del sistema si hay; si no, la de MeLi
    /// (salvo que la hayan marcado "No mostrar" / REPORTADA).</summary>
    public async Task<Dictionary<int, Foto>> FotosAsync(ICollection<int> ids)
    {
        var res = new Dictionary<int, Foto>();
        if (ids.Count == 0) return res;
        var idList = ids.Distinct().ToList();
        var regs = await _db.CafeProductoFotos.AsNoTracking()
            .Where(f => idList.Contains(f.CafeProductoId))
            .Select(f => new { f.CafeProductoId, f.FotoPropiaArchivo, f.Estado }).ToListAsync();
        foreach (var f in regs.Where(f => f.FotoPropiaArchivo != null))
            res[f.CafeProductoId] = new Foto(UrlPropia + f.FotoPropiaArchivo, true);
        var oculta = regs.Where(f => f.FotoPropiaArchivo == null && f.Estado == "REPORTADA").Select(f => f.CafeProductoId).ToHashSet();
        var faltan = idList.Where(id => !res.ContainsKey(id) && !oculta.Contains(id)).ToList();
        foreach (var (pid, url) in await MeliAsync(faltan))
            res[pid] = new Foto(url, false);
        return res;
    }
}
