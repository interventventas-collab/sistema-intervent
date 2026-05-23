using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Procesa ordenes de MeLi marcadas como pendientes de descontar stock
/// (MeliOrders.StockDiscounted = false) y baja el stock del CafeProducto
/// linkeado a cada item (MeliItems.CafeProductoId + CafeFormato).
///
/// Reglas:
/// - CAFE: descuenta de StockGramos según formato (1KG=1000g, MEDIO=500g, CUARTO=250g) × Quantity.
/// - OTROS: descuenta de StockUnidades, multiplicando por UxB si formato=BULTO.
/// - Si el MeliItem no esta linkeado (CafeProductoId=null), se ignora y se marca como descontada.
///
/// Pensado para correr al final de SyncOrdersAsync (auto) o on-demand vía endpoint.
/// </summary>
public class MeliStockSyncService
{
    private readonly AppDbContext _db;
    private readonly ILogger<MeliStockSyncService> _logger;
    private readonly CafeStockLogger _stockLogger;

    public MeliStockSyncService(AppDbContext db, ILogger<MeliStockSyncService> logger, CafeStockLogger stockLogger)
    {
        _db = db;
        _logger = logger;
        _stockLogger = stockLogger;
    }

    public record StockSyncResult(int Procesadas, int DescontadasCafe, int DescontadasOtros, int SinLinkear, List<string> Errores);

    public async Task<StockSyncResult> ProcessPendingAsync(int maxBatch = 500)
    {
        var pending = await _db.MeliOrders
            .Where(o => !o.StockDiscounted)
            .OrderBy(o => o.DateCreated)
            .Take(maxBatch)
            .ToListAsync();

        int procesadas = 0, descCafe = 0, descOtros = 0, sinLink = 0;
        var errores = new List<string>();

        if (pending.Count == 0)
            return new StockSyncResult(0, 0, 0, 0, errores);

        // Cargar todos los componentes que referencian a los items de las ordenes pendientes.
        // MeliItemComponente es la fuente de verdad: 1 item MeLi puede mapear a N productos sueltos.
        var itemIds = pending.Select(o => o.ItemId).Distinct().ToList();
        var componentes = await _db.MeliItemComponentes
            .Where(c => itemIds.Contains(c.MeliItemId))
            .ToListAsync();
        // Agrupado por MeliItemId — el matching por variation_id se hace abajo (puede haber
        // multiples filas para el mismo MeliItemId, una por variante).
        var compsByItem = componentes.GroupBy(c => c.MeliItemId).ToDictionary(g => g.Key, g => g.ToList());

        // Fallback (legacy): items que aun no migraron a MeliItemComponente y tienen el linkeo
        // viejo en MeliItem.CafeProductoId + CafeFormato (cafes ya linkeados desde antes).
        // Cuidado: MeliItems puede tener duplicados por MeliItemId (multiples cuentas/variations),
        // por eso usamos GroupBy.First() en vez de ToDictionary directo.
        var itemsLegacyRaw = await _db.MeliItems
            .Where(mi => itemIds.Contains(mi.MeliItemId) && mi.CafeProductoId.HasValue)
            .ToListAsync();
        var itemsLegacy = itemsLegacyRaw
            .GroupBy(mi => mi.MeliItemId)
            .ToDictionary(g => g.Key, g => g.First());

        // Cargar todos los productos referenciados (de los 2 lados) de una sola vez.
        var prodIds = componentes.Select(c => c.CafeProductoId)
            .Concat(itemsLegacy.Values.Where(mi => mi.CafeProductoId.HasValue).Select(mi => mi.CafeProductoId!.Value))
            .Distinct().ToList();
        var productos = await _db.CafeProductos
            .Where(p => prodIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        foreach (var ord in pending)
        {
            try
            {
                List<(CafeProducto prod, decimal cant, string? formato)>? toDiscount = null;

                if (compsByItem.TryGetValue(ord.ItemId, out var comps) && comps.Count > 0)
                {
                    // BUG-FIX 2026-05-22: Respeta variation_id para descontar SOLO la variante vendida.
                    // Logica:
                    //   1) Si la orden tiene VariationId, usar SOLO los componentes con ese variation_id.
                    //   2) Si no hay match exacto, fallback a componentes SIN variation_id (legacy / aplica a todas).
                    //   3) Si NO hay variation_id en la orden (publicacion sin variantes), usar todos los
                    //      componentes (asumimos que no hay multi-variante).
                    List<MeliItemComponente> compsAplicables;
                    if (!string.IsNullOrEmpty(ord.VariationId))
                    {
                        compsAplicables = comps.Where(c => c.MeliVariationId == ord.VariationId).ToList();
                        if (compsAplicables.Count == 0)
                        {
                            // Fallback: componentes sin variation_id (compat con linkeos pre-bug)
                            compsAplicables = comps.Where(c => string.IsNullOrEmpty(c.MeliVariationId)).ToList();
                        }
                    }
                    else
                    {
                        // Orden sin variation_id: si hay componentes sin variation_id, usar esos.
                        // Si SOLO hay componentes con variation_id (raro), usar todos (mejor descontar
                        // algo que nada — el operador puede ajustar a mano).
                        compsAplicables = comps.Where(c => string.IsNullOrEmpty(c.MeliVariationId)).ToList();
                        if (compsAplicables.Count == 0) compsAplicables = comps;
                    }

                    if (compsAplicables.Count > 0)
                    {
                        toDiscount = new();
                        foreach (var c in compsAplicables)
                        {
                            if (productos.TryGetValue(c.CafeProductoId, out var prod))
                                toDiscount.Add((prod, c.Cantidad, c.Formato));
                        }
                    }
                }
                else if (itemsLegacy.TryGetValue(ord.ItemId, out var mi) && mi.CafeProductoId.HasValue)
                {
                    if (productos.TryGetValue(mi.CafeProductoId.Value, out var prod))
                        toDiscount = new() { (prod, 1m, mi.CafeFormato) };
                }

                if (toDiscount is null || toDiscount.Count == 0)
                {
                    // Sin linkeo: marcar descontada igual (no hay nada que hacer).
                    ord.StockDiscounted = true;
                    ord.UpdatedAt = DateTime.UtcNow;
                    sinLink++;
                    procesadas++;
                    continue;
                }

                foreach (var (prod, cant, formato) in toDiscount)
                {
                    if (string.Equals(prod.Categoria, "CAFE", StringComparison.OrdinalIgnoreCase))
                    {
                        var gramos = (formato ?? "1KG").ToUpperInvariant() switch
                        {
                            "1KG" => 1000m,
                            "MEDIO" => 500m,
                            "CUARTO" => 250m,
                            _ => 1000m  // fallback razonable si no hay formato
                        };
                        prod.StockGramos -= gramos * cant * ord.Quantity;
                        descCafe++;
                        // Historial: registro el cambio para auditoría
                        var unidadesEquiv = (int)Math.Round(gramos * cant * ord.Quantity);
                        var antes = (int)Math.Round(prod.StockGramos + gramos * cant * ord.Quantity);
                        var despues = (int)Math.Round(prod.StockGramos);
                        await _stockLogger.LogAsync(prod.Id, "VENTA_MELI", antes, despues,
                            comentario: $"Orden MeLi #{ord.MeliOrderId} · {cant}x{(formato ?? "1KG")} · {unidadesEquiv}g",
                            saveChanges: false);
                    }
                    else
                    {
                        var mult = 1m;
                        if (string.Equals(formato, "BULTO", StringComparison.OrdinalIgnoreCase)
                            && prod.UxB.HasValue && prod.UxB.Value > 0)
                            mult = prod.UxB.Value;
                        var unidades = (int)Math.Round(mult * cant * ord.Quantity);
                        var antes = prod.StockUnidades;
                        prod.StockUnidades -= unidades;
                        descOtros++;
                        // Historial
                        await _stockLogger.LogAsync(prod.Id, "VENTA_MELI", antes, prod.StockUnidades,
                            comentario: $"Orden MeLi #{ord.MeliOrderId} · {cant}x{(formato ?? "U")} · -{unidades}u",
                            saveChanges: false);
                    }
                    // Marcar para que el job de respaldo / push event-driven sepa que hay que pushear a MeLi.
                    prod.StockChangedAt = DateTime.UtcNow;
                }

                ord.StockDiscounted = true;
                ord.UpdatedAt = DateTime.UtcNow;
                procesadas++;
            }
            catch (Exception ex)
            {
                errores.Add($"Orden {ord.MeliOrderId}: {ex.Message}");
                _logger.LogWarning(ex, "Error descontando stock de MeliOrder {Id}", ord.MeliOrderId);
            }
        }

        await _db.SaveChangesAsync();
        return new StockSyncResult(procesadas, descCafe, descOtros, sinLink, errores);
    }
}
