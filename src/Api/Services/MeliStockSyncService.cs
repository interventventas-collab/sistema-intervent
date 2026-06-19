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
    private readonly IServiceScopeFactory _scopeFactory;

    public MeliStockSyncService(AppDbContext db, ILogger<MeliStockSyncService> logger,
        CafeStockLogger stockLogger, IServiceScopeFactory scopeFactory)
    {
        _db = db;
        _logger = logger;
        _stockLogger = stockLogger;
        _scopeFactory = scopeFactory;
    }

    public record StockSyncResult(int Procesadas, int DescontadasCafe, int DescontadasOtros, int SinLinkear, List<string> Errores);

    public async Task<StockSyncResult> ProcessPendingAsync(int maxBatch = 500)
    {
        var pending = await _db.MeliOrders
            .Where(o => !o.StockDiscounted)
            .OrderBy(o => o.DateCreated)
            .Take(maxBatch)
            .ToListAsync();

        int procesadas = 0, descCafe = 0, descOtros = 0, sinLink = 0, fullOmitidas = 0;
        var errores = new List<string>();
        // Set de productos cuyo stock se modificó durante el procesamiento.
        // Al final, vamos a disparar PUSH inmediato para todos los CafeProductoIds afectados
        // y así actualizar las OTRAS publicaciones MeLi que dependen del mismo producto base.
        // Bug detectado 2026-05-27: cuando se vendía un pack (ej. HE410X6), descontaba stock pero
        // no republicaba las otras pubs que dependen del mismo producto base (HE410 y sus packs).
        var productosTocadosParaPush = new HashSet<int>();

        // 2026-05-25: cargar id del depósito Full una sola vez. Si la orden es Full
        // (LogisticType=fulfillment), descontamos de Cafe_StockPorDeposito[Full] en vez
        // de tocar Cafe_Productos.StockUnidades (que representa el stock propio en 9 de Abril).
        // El total real disponible se calcula al vuelo como StockUnidades + Full en las pantallas.
        var depFullId = await _db.CafeDepositos
            .Where(d => d.Nombre == "Full MeLi" && d.IsActive)
            .Select(d => (int?)d.Id).FirstOrDefaultAsync();

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
            // BUG-FIX 2026-05-27: race condition con webhooks MeLi reintentados.
            // MeLi reintenta el mismo webhook varias veces (at-least-once). Si dos llegan casi
            // simultaneamente, ambos invocan ProcessPendingAsync, ambos ven StockDiscounted=false
            // y ambos descuentan stock → log duplicado en Stock_Movimientos (y riesgo de doble descuento real).
            // Fix: claim atómico via UPDATE ... WHERE StockDiscounted=0. SQL Server garantiza que
            // solo UN UPDATE concurrent gana (rowsAffected=1), los demás devuelven 0 y skipean.
            var claimedRows = await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE MeliOrders SET StockDiscounted = 1, UpdatedAt = SYSUTCDATETIME() WHERE Id = {ord.Id} AND StockDiscounted = 0");
            if (claimedRows == 0)
            {
                _logger.LogDebug("Orden MeLi #{Id} ya fue claim-eada por otro proceso, salteando", ord.MeliOrderId);
                continue;
            }
            // Sincronizar el ChangeTracker para que el SaveChanges final no sobrescriba el claim.
            ord.StockDiscounted = true;
            ord.UpdatedAt = DateTime.UtcNow;

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
                            // 2026-06-19 SAFE-GUARD: la orden vino con VariationId pero los linkeos no lo
                            // tienen seteado. Antes haciamos fallback a NULL, que descontaba TODOS los
                            // componentes — interpretando variantes como combo. Caso real C141/Batea
                            // Colombraro: cada venta descontaba beige + blanco + gris + rojo a la vez.
                            // Nueva logica: si hay >1 componente con SKU distinto sin variation_id,
                            // asumimos que son VARIANTES con linkeo roto y NO descontamos. Loguear claro
                            // para que el operador pueda re-linkear con MeliVariationId correcto.
                            var compsNull = comps.Where(c => string.IsNullOrEmpty(c.MeliVariationId)).ToList();
                            var distinctSkus = compsNull.Select(c => c.CafeProductoId).Distinct().Count();
                            if (compsNull.Count > 1 && distinctSkus > 1)
                            {
                                _logger.LogWarning(
                                    "⚠ Orden MeLi #{Id} (Item={ItemId}, Variation={Var}) tiene linkeos con MeliVariationId=NULL pero hay {N} productos distintos. SE OMITE el descuento para evitar restar de variantes erroneas. Re-linkear el MLA con variation_id correcto.",
                                    ord.MeliOrderId, ord.ItemId, ord.VariationId, distinctSkus);
                                compsAplicables = new List<MeliItemComponente>();
                            }
                            else
                            {
                                // Caso seguro: 1 solo componente sin variation_id, o multiples del mismo producto.
                                // Si son multiples del mismo producto (caso cafe 1/4 .4 con 36 variantes de
                                // molienda apuntando a F3), DEDUPLICAR para no descontar 36x.
                                compsAplicables = compsNull
                                    .GroupBy(c => c.CafeProductoId)
                                    .Select(g => g.First())
                                    .ToList();
                            }
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
                    {
                        // 2026-06-19: cafe fraccionado en path legacy. Si el SKU de la MLA termina
                        // en .4 (cuarto) o .2 (medio), descontar la fraccion proporcional del kilo.
                        decimal cant = 1m;
                        if (!string.IsNullOrEmpty(mi.Sku))
                        {
                            if (mi.Sku.EndsWith(".4")) cant = 0.25m;
                            else if (mi.Sku.EndsWith(".2")) cant = 0.5m;
                        }
                        toDiscount = new() { (prod, cant, mi.CafeFormato) };
                    }
                }

                if (toDiscount is null || toDiscount.Count == 0)
                {
                    // Sin linkeo: ya quedó marcada como descontada en el claim atómico arriba.
                    sinLink++;
                    procesadas++;
                    continue;
                }

                // 2026-05-25: detectar si la orden es Full (sale del depósito de MeLi) vs no-Full
                // (sale de nuestro depósito 9 de Abril).
                // 2026-05-29: regla "Full desenlazado". Si la orden es Full, NO descontamos nada
                // del sistema — Full lo administra MeLi. La orden igual se marca como procesada
                // (StockDiscounted=true arriba) para no reprocesarla. Solo registramos en logs
                // para que el usuario vea las ventas Full en sus dashboards.
                bool esFull = string.Equals(ord.LogisticType, "fulfillment", StringComparison.OrdinalIgnoreCase)
                              && depFullId.HasValue;

                if (esFull)
                {
                    // No-op: orden Full marcada como procesada, sin tocar stock.
                    _logger.LogInformation("Orden MeLi Full #{Id} omitida del descuento (Full desenlazado del sistema)", ord.MeliOrderId);
                    fullOmitidas++;
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
                        var gramosADescontar = gramos * cant * ord.Quantity;

                        // 2026-05-29: Full desenlazado. El branch esFull se eliminó porque
                        // las órdenes Full se cortan con `continue` antes de entrar al loop.
                        prod.StockGramos -= gramosADescontar;
                        // Historial: registro el cambio para auditoría
                        var unidadesEquiv = (int)Math.Round(gramosADescontar);
                        var antes = (int)Math.Round(prod.StockGramos + gramosADescontar);
                        var despues = (int)Math.Round(prod.StockGramos);
                        await _stockLogger.LogAsync(prod.Id, "VENTA_MELI", antes, despues,
                            comentario: $"Orden MeLi #{ord.MeliOrderId} · {cant}x{(formato ?? "1KG")} · {unidadesEquiv}g",
                            saveChanges: false);
                        prod.StockChangedAt = DateTime.UtcNow;
                        await Api.Services.CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);
                        // Stock base cambió → republicar las pubs que dependen de este producto.
                        productosTocadosParaPush.Add(prod.Id);
                        descCafe++;
                    }
                    else
                    {
                        var mult = 1m;
                        if (string.Equals(formato, "BULTO", StringComparison.OrdinalIgnoreCase)
                            && prod.UxB.HasValue && prod.UxB.Value > 0)
                            mult = prod.UxB.Value;
                        var unidades = (int)Math.Round(mult * cant * ord.Quantity);

                        // 2026-05-29: Full desenlazado. El branch esFull se eliminó porque
                        // las órdenes Full se cortan con `continue` antes de entrar al loop.
                        var antes = prod.StockUnidades;
                        prod.StockUnidades -= unidades;
                        // Historial
                        await _stockLogger.LogAsync(prod.Id, "VENTA_MELI", antes, prod.StockUnidades,
                            comentario: $"Orden MeLi #{ord.MeliOrderId} · {cant}x{(formato ?? "U")} · -{unidades}u",
                            saveChanges: false);
                        prod.StockChangedAt = DateTime.UtcNow;
                        await Api.Services.CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);
                        // Stock base cambió → republicar las pubs que dependen de este producto.
                        productosTocadosParaPush.Add(prod.Id);
                        descOtros++;
                    }
                }

                // StockDiscounted ya quedó en true por el claim atómico al inicio del loop.
                procesadas++;
            }
            catch (Exception ex)
            {
                // El claim ya marcó la orden como descontada. Si el procesamiento falló a mitad,
                // dejamos la orden marcada igual para evitar reprocesarla en loop. El operador
                // ve el error en `errores` y ajusta a mano. Preferimos perder 1 orden a duplicar.
                errores.Add($"Orden {ord.MeliOrderId}: {ex.Message}");
                _logger.LogWarning(ex, "Error descontando stock de MeliOrder {Id} (quedó marcada como descontada por claim previo)", ord.MeliOrderId);
            }
        }

        await _db.SaveChangesAsync();

        // PUSH EN CASCADA: después de descontar stock por ventas MeLi, republicamos TODAS las
        // pubs que dependen de los productos afectados. Fire-and-forget para no demorar este job.
        // Cada producto va en su propio scope (DbContext fresco) para no chocar con _db actual.
        // Si una venta de pack baja el stock de HE410, esto republica las otras pubs de HE410 + sus packs.
        if (productosTocadosParaPush.Count > 0)
        {
            _logger.LogInformation("Disparando push en cascada para {N} productos tocados por ventas MeLi",
                productosTocadosParaPush.Count);
            foreach (var pid in productosTocadosParaPush)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var pushSvc = scope.ServiceProvider.GetRequiredService<MeliStockPushService>();
                        await pushSvc.PushStockForProductoAsync(pid);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error pusheando producto {Id} en cascada tras venta MeLi", pid);
                    }
                });
            }
        }

        return new StockSyncResult(procesadas, descCafe, descOtros, sinLink, errores);
    }
}
