using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-11-12: dar de baja un producto TAMBIÉN pausa sus publicaciones de MercadoLibre.
///
/// Antes, dar de baja (IsActive=false) dejaba las publicaciones activas en MeLi con el último stock
/// que tenían, y encima el job de stock deja de mirar productos inactivos: se podía seguir vendiendo
/// algo que ya no existe. Ahora el dueño elige "stock en 0" o "stock como está" y, en los dos casos,
/// se pausan todas las publicaciones vivas que lo contienen (sueltas, packs, sets y combos).
///
/// Pausar (no finalizar): si se equivocó, vuelve a activar el producto y prende las publicaciones.
///
/// Publicaciones con varios colores donde los OTROS colores siguen activos: NO se pausan (se cortaría
/// la venta de colores que existen). A esas se les manda el stock: con "stock en 0" ese color queda en 0.
/// </summary>
public class ProductoBajaService
{
    private readonly AppDbContext _db;
    private readonly MeliEstadoService _estado;
    private readonly MeliStockPushService _stockPush;
    private readonly AuditLogService _audit;
    private readonly ILogger<ProductoBajaService> _logger;

    public ProductoBajaService(AppDbContext db, MeliEstadoService estado, MeliStockPushService stockPush,
        AuditLogService audit, ILogger<ProductoBajaService> logger)
    {
        _db = db; _estado = estado; _stockPush = stockPush; _audit = audit; _logger = logger;
    }

    private static readonly string[] EstadosMuertos = { "deleted", "closed" };

    public record PubliBaja(string MeliItemId, string Titulo, string Estado, decimal Cantidad, bool EsPack, bool CompartidaConOtrosColores);
    public record BajaPreview(int ProductoId, string? Sku, string Nombre, int StockUnidades, decimal StockGramos,
        int StockEnFull, bool YaInactivo, List<PubliBaja> Publicaciones);
    public record PubliResultado(string MeliItemId, string Titulo, bool Ok, string Mensaje);
    public record BajaResultado(bool Ok, string Mensaje, int Pausadas, int YaPausadas, int Fallaron, List<PubliResultado> Detalle);

    public async Task<BajaPreview?> PreviewAsync(int id, CancellationToken ct = default)
    {
        var p = await _db.CafeProductos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;

        var enFull = await _db.CafeStockPorDeposito.Where(s => s.ProductoId == id)
            .Join(_db.CafeDepositos, s => s.DepositoId, d => d.Id, (s, d) => new { s.StockUnidades, d.Nombre })
            .Where(x => x.Nombre == "Full MeLi").SumAsync(x => (int?)x.StockUnidades, ct) ?? 0;

        return new BajaPreview(p.Id, p.Sku, p.Nombre, p.StockUnidades, p.StockGramos, enFull, !p.IsActive,
            await PublicacionesAsync(id, ct));
    }

    /// <summary>Publicaciones vivas (no borradas ni finalizadas) que contienen al producto.</summary>
    private async Task<List<PubliBaja>> PublicacionesAsync(int id, CancellationToken ct)
    {
        var directas = await _db.MeliItems.AsNoTracking()
            .Where(m => m.CafeProductoId == id && !EstadosMuertos.Contains(m.Status))
            .Select(m => new { m.MeliItemId, m.Title, m.Status, Cant = 1m })
            .ToListAsync(ct);

        var porComp = await (from c in _db.MeliItemComponentes.AsNoTracking()
                             join m in _db.MeliItems.AsNoTracking() on c.MeliItemId equals m.MeliItemId
                             where c.CafeProductoId == id && !EstadosMuertos.Contains(m.Status)
                             select new { m.MeliItemId, m.Title, m.Status, Cant = c.Cantidad })
            .ToListAsync(ct);

        var todas = directas.Concat(porComp)
            .GroupBy(x => x.MeliItemId)
            .Select(g => new { Mla = g.Key, g.First().Title, g.First().Status, Cant = g.Max(x => x.Cant) })
            .ToList();
        if (todas.Count == 0) return new();

        var mlas = todas.Select(x => x.Mla).ToList();

        // ¿Hay OTROS productos activos en la misma publicación como otra variante (otro color)?
        var otrosDirectos = await (from m in _db.MeliItems.AsNoTracking()
                                   join p in _db.CafeProductos.AsNoTracking() on m.CafeProductoId equals p.Id
                                   where mlas.Contains(m.MeliItemId) && m.CafeProductoId != id && p.IsActive
                                         && m.VariationId != null
                                   select m.MeliItemId).Distinct().ToListAsync(ct);
        var otrosComp = await (from c in _db.MeliItemComponentes.AsNoTracking()
                               join p in _db.CafeProductos.AsNoTracking() on c.CafeProductoId equals p.Id
                               where mlas.Contains(c.MeliItemId) && c.CafeProductoId != id && p.IsActive
                                     && c.MeliVariationId != null && c.MeliVariationId != ""
                               select c.MeliItemId).Distinct().ToListAsync(ct);
        var compartidas = otrosDirectos.Concat(otrosComp).ToHashSet();

        return todas
            .Select(x => new PubliBaja(x.Mla, x.Title, x.Status, x.Cant, x.Cant > 1 || x.Title.Contains("Set", StringComparison.OrdinalIgnoreCase) || x.Title.Contains("Pack", StringComparison.OrdinalIgnoreCase),
                compartidas.Contains(x.Mla)))
            .OrderBy(x => x.Estado == "active" ? 0 : 1).ThenBy(x => x.Cantidad).ThenBy(x => x.Titulo)
            .ToList();
    }

    /// <summary>TOCA MELI. Deja el producto inactivo (opcionalmente con stock en 0) y pausa sus publicaciones.</summary>
    public async Task<BajaResultado> DarDeBajaAsync(int id, bool stockEnCero, string desde, string? quien, CancellationToken ct = default)
    {
        var p = await _db.CafeProductos.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return new BajaResultado(false, "Producto no encontrado.", 0, 0, 0, new());

        var publis = await PublicacionesAsync(id, ct);
        var stockAntes = p.StockUnidades;
        var gramosAntes = p.StockGramos;

        if (stockEnCero)
        {
            // El stock de Full está en el depósito de MercadoLibre: ese no se toca desde acá.
            var filasDep = await _db.CafeStockPorDeposito.Where(s => s.ProductoId == id)
                .Join(_db.CafeDepositos, s => s.DepositoId, d => d.Id, (s, d) => new { Fila = s, DepId = d.Id, DepNombre = d.Nombre })
                .ToListAsync(ct);
            var enFull = filasDep.Where(x => x.DepNombre == "Full MeLi").Sum(x => x.Fila.StockUnidades);
            foreach (var f in filasDep.Where(x => x.DepNombre != "Full MeLi"))
            {
                if (f.Fila.StockUnidades == 0 && f.Fila.StockGramos == 0) continue;
                _db.StockMovimientos.Add(new StockMovimiento
                {
                    ProductoId = id, OperadorNombreSnap = quien, DepositoId = f.DepId, DepositoNombreSnap = f.DepNombre,
                    TipoMov = "SET", Cantidad = 0, StockAntes = f.Fila.StockUnidades, StockDespues = 0,
                    Comentario = $"Dado de baja desde {desde}: stock en 0", CreatedAt = DateTime.UtcNow
                });
                f.Fila.StockUnidades = 0; f.Fila.StockGramos = 0; f.Fila.UpdatedAt = DateTime.UtcNow;
            }
            if (filasDep.Count == 0 && p.StockUnidades != 0)
            {
                _db.StockMovimientos.Add(new StockMovimiento
                {
                    ProductoId = id, OperadorNombreSnap = quien, DepositoNombreSnap = "9 de Abril", TipoMov = "SET",
                    Cantidad = 0, StockAntes = p.StockUnidades, StockDespues = 0,
                    Comentario = $"Dado de baja desde {desde}: stock en 0", CreatedAt = DateTime.UtcNow
                });
            }
            p.StockUnidades = enFull;
            p.StockGramos = 0;
            p.StockChangedAt = DateTime.UtcNow;
        }

        p.IsActive = false;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // MercadoLibre: pausar las que contienen solo este producto; a las compartidas con otros colores, mandarles el stock.
        var detalle = new List<PubliResultado>();
        int pausadas = 0, yaPausadas = 0, fallaron = 0;
        foreach (var pub in publis.Where(x => !x.CompartidaConOtrosColores))
        {
            if (pub.Estado == "paused") { yaPausadas++; detalle.Add(new(pub.MeliItemId, pub.Titulo, true, "Ya estaba pausada")); continue; }
            try
            {
                var r = await _estado.PausarAsync(pub.MeliItemId, ct);
                if (r.Ok) pausadas++; else fallaron++;
                detalle.Add(new(pub.MeliItemId, pub.Titulo, r.Ok, r.Ok ? "Pausada" : r.Mensaje));
            }
            catch (Exception ex)
            {
                fallaron++;
                _logger.LogWarning(ex, "[Baja] no se pudo pausar {Mla}", pub.MeliItemId);
                detalle.Add(new(pub.MeliItemId, pub.Titulo, false, "No se pudo pausar: " + ex.Message));
            }
        }
        var compartidas = publis.Where(x => x.CompartidaConOtrosColores).Select(x => x.MeliItemId).ToList();
        if (compartidas.Count > 0)
        {
            try { await _stockPush.PushStockForMeliItemsAsync(compartidas, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Baja] no se pudo mandar stock a {Mlas}", string.Join(",", compartidas)); }
            foreach (var pub in publis.Where(x => x.CompartidaConOtrosColores))
                detalle.Add(new(pub.MeliItemId, pub.Titulo, true,
                    stockEnCero ? "Tiene otros colores: sigue activa, este color quedó en 0" : "Tiene otros colores: sigue activa"));
        }

        await _audit.LogAsync("CafeProducto", id.ToString(), "BAJA",
            $"{p.Sku} dado de baja desde {desde} · " +
            (stockEnCero ? $"stock {stockAntes} u / {gramosAntes} g → 0" : $"stock queda {stockAntes} u / {gramosAntes} g") +
            $" · MeLi: {pausadas} pausadas, {yaPausadas} ya pausadas, {fallaron} fallaron" +
            (publis.Count > 0 ? " (" + string.Join(", ", detalle.Select(d => $"{d.MeliItemId} {d.Mensaje}")) + ")" : ""),
            quien);

        var msg = fallaron > 0
            ? $"Dado de baja. OJO: {fallaron} publicación(es) no se pudieron pausar, pausalas a mano en Publicaciones."
            : publis.Count == 0 ? "Dado de baja. No tenía publicaciones en MercadoLibre."
            : $"Dado de baja. {pausadas} publicación(es) pausada(s) en MercadoLibre" + (yaPausadas > 0 ? $", {yaPausadas} ya estaban pausadas." : ".");
        return new BajaResultado(fallaron == 0, msg, pausadas, yaPausadas, fallaron, detalle);
    }
}
