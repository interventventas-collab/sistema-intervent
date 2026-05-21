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

    public MeliStockSyncService(AppDbContext db, ILogger<MeliStockSyncService> logger)
    {
        _db = db;
        _logger = logger;
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

        // Cargar todos los MeliItems referenciados de una sola vez (eficiente).
        var itemIds = pending.Select(o => o.ItemId).Distinct().ToList();
        var meliItems = await _db.MeliItems
            .Where(mi => itemIds.Contains(mi.MeliItemId))
            .ToDictionaryAsync(mi => mi.MeliItemId);

        // Cargar los CafeProductos linkeados
        var prodIds = meliItems.Values
            .Where(mi => mi.CafeProductoId.HasValue)
            .Select(mi => mi.CafeProductoId!.Value)
            .Distinct()
            .ToList();
        var productos = await _db.CafeProductos
            .Where(p => prodIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        foreach (var ord in pending)
        {
            try
            {
                if (!meliItems.TryGetValue(ord.ItemId, out var mi) || !mi.CafeProductoId.HasValue)
                {
                    // Sin linkeo → marcamos descontada igual (no hay nada que hacer).
                    ord.StockDiscounted = true;
                    ord.UpdatedAt = DateTime.UtcNow;
                    sinLink++;
                    procesadas++;
                    continue;
                }
                if (!productos.TryGetValue(mi.CafeProductoId.Value, out var prod))
                {
                    errores.Add($"Orden {ord.MeliOrderId} item {ord.ItemId}: producto {mi.CafeProductoId} no encontrado.");
                    continue;
                }

                if (string.Equals(prod.Categoria, "CAFE", StringComparison.OrdinalIgnoreCase))
                {
                    var gramos = (mi.CafeFormato ?? "1KG").ToUpperInvariant() switch
                    {
                        "1KG" => 1000m,
                        "MEDIO" => 500m,
                        "CUARTO" => 250m,
                        _ => 0m
                    };
                    if (gramos <= 0)
                    {
                        errores.Add($"Orden {ord.MeliOrderId}: formato '{mi.CafeFormato}' desconocido para CAFE.");
                        continue;
                    }
                    var aDescontar = gramos * ord.Quantity;
                    prod.StockGramos -= aDescontar;
                    descCafe++;
                }
                else
                {
                    // OTROS — usa unidades
                    var mult = 1;
                    if (string.Equals(mi.CafeFormato, "BULTO", StringComparison.OrdinalIgnoreCase)
                        && prod.UxB.HasValue && prod.UxB.Value > 0)
                    {
                        mult = prod.UxB.Value;
                    }
                    var unidades = mult * ord.Quantity;
                    prod.StockUnidades -= unidades;
                    descOtros++;
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
