using System.Text.Json;
using Api.Data;
using Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Api.BackgroundJobs;

public class ProcessOrderStockJob : IScheduledJob
{
    public string Code => "ProcessOrderStock";

    private readonly IServiceScopeFactory _scopeFactory;

    public ProcessOrderStockJob(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<string> ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var meliItemService = scope.ServiceProvider.GetRequiredService<MeliItemService>();
        var auditLog = scope.ServiceProvider.GetRequiredService<AuditLogService>();

        // Buscar ordenes pagadas que no hayan descontado stock todavia
        var pendingOrders = await db.MeliOrders
            .Include(o => o.MeliAccount)
            .Where(o => !o.StockDiscounted && (o.Status == "paid" || o.Status == "shipped" || o.Status == "delivered"))
            .ToListAsync(cancellationToken);

        if (!pendingOrders.Any())
            return JsonSerializer.Serialize(new { mensaje = "No hay ordenes pendientes de descontar stock", procesadas = 0 });

        int processed = 0;
        int skippedNoProduct = 0;
        int errors = 0;
        var errorMessages = new List<string>();
        var productosActualizados = new List<object>();

        // Agrupar por ItemId para procesar por producto
        var ordersByItem = pendingOrders.GroupBy(o => o.ItemId).ToList();

        foreach (var group in ordersByItem)
        {
            try
            {
                var itemId = group.Key;

                // Buscar el MeliItem vinculado a un Producto
                var meliItem = await db.MeliItems
                    .Include(i => i.MeliAccount)
                    .FirstOrDefaultAsync(i => i.MeliItemId == itemId && i.ProductId != null, cancellationToken);

                if (meliItem is null || meliItem.ProductId is null)
                {
                    // No tiene producto vinculado, marcar como procesadas igualmente
                    foreach (var order in group)
                    {
                        order.StockDiscounted = true;
                        order.UpdatedAt = DateTime.UtcNow;
                    }
                    skippedNoProduct += group.Count();

                    await auditLog.LogAsync("MeliOrder", itemId, "STOCK_SKIP",
                        JsonSerializer.Serialize(new
                        {
                            motivo = "Sin producto vinculado",
                            item = itemId,
                            ordenes = group.Count(),
                            cantidadTotal = group.Sum(o => o.Quantity)
                        }), "Sistema/ProcessOrderStock");
                    continue;
                }

                var product = await db.Products.FindAsync(new object[] { meliItem.ProductId.Value }, cancellationToken);
                if (product is null)
                {
                    foreach (var order in group)
                    {
                        order.StockDiscounted = true;
                        order.UpdatedAt = DateTime.UtcNow;
                    }
                    skippedNoProduct += group.Count();
                    continue;
                }

                // Calcular total a descontar de este grupo de ordenes
                int totalToDiscount = group.Sum(o => o.Quantity);
                var oldStock = product.Stock;
                product.Stock = Math.Max(0, product.Stock - totalToDiscount);
                product.UpdatedAt = DateTime.UtcNow;

                // Detalle de ordenes para auditoria
                var ordenesDetalle = group.Select(o => new
                {
                    ordenMeli = o.MeliOrderId,
                    cantidad = o.Quantity,
                    comprador = o.BuyerNickname,
                    cuenta = o.MeliAccount?.Nickname ?? "Desconocida",
                    estado = o.Status
                }).ToList();

                // Marcar ordenes como procesadas
                foreach (var order in group)
                {
                    order.StockDiscounted = true;
                    order.UpdatedAt = DateTime.UtcNow;
                    processed++;
                }

                await db.SaveChangesAsync(cancellationToken);

                // Buscar todas las publicaciones vinculadas al producto (para la auditoria)
                var itemsVinculados = await db.MeliItems
                    .Include(i => i.MeliAccount)
                    .Where(i => i.ProductId == product.Id)
                    .Select(i => new
                    {
                        meliItemId = i.MeliItemId,
                        titulo = i.Title,
                        cuenta = i.MeliAccount != null ? i.MeliAccount.Nickname : "Desconocida",
                        stockAnterior = i.AvailableQuantity,
                        estado = i.Status
                    })
                    .ToListAsync(cancellationToken);

                // Registrar auditoria del descuento de stock
                await auditLog.LogAsync("Product", product.Id.ToString(), "STOCK_ORDER_DISCOUNT",
                    JsonSerializer.Serialize(new
                    {
                        producto = product.Title,
                        sku = product.Sku,
                        stockAnterior = oldStock,
                        stockNuevo = product.Stock,
                        cantidadDescontada = totalToDiscount,
                        ordenesProcesadas = ordenesDetalle,
                        publicacionesVinculadas = itemsVinculados
                    }), "Sistema/ProcessOrderStock");

                // Propagar el nuevo stock a TODAS las publicaciones vinculadas
                if (product.Stock != oldStock)
                {
                    try
                    {
                        await meliItemService.PropagateStockAsync(product.Id, product.Stock);

                        await auditLog.LogAsync("Product", product.Id.ToString(), "STOCK_PROPAGATED",
                            JsonSerializer.Serialize(new
                            {
                                producto = product.Title,
                                stockPropagado = product.Stock,
                                publicacionesActualizadas = itemsVinculados.Count,
                                detalle = itemsVinculados.Select(i => $"{i.cuenta}: {i.meliItemId} ({i.stockAnterior} -> {product.Stock})")
                            }), "Sistema/ProcessOrderStock");
                    }
                    catch (Exception ex)
                    {
                        errorMessages.Add($"Propagacion de stock para producto {product.Id} ({product.Title}): {ex.Message}");

                        await auditLog.LogAsync("Product", product.Id.ToString(), "STOCK_PROPAGATION_ERROR",
                            JsonSerializer.Serialize(new
                            {
                                producto = product.Title,
                                error = ex.Message,
                                stockQueDebiaPropagarse = product.Stock,
                                publicacionesAfectadas = itemsVinculados.Count
                            }), "Sistema/ProcessOrderStock");
                    }
                }

                productosActualizados.Add(new
                {
                    producto = product.Title,
                    sku = product.Sku,
                    stockAnterior = oldStock,
                    stockNuevo = product.Stock,
                    descontado = totalToDiscount,
                    ordenes = group.Count(),
                    publicaciones = itemsVinculados.Count
                });
            }
            catch (Exception ex)
            {
                errors++;
                errorMessages.Add($"Error procesando item {group.Key}: {ex.Message}");
            }
        }

        return JsonSerializer.Serialize(new
        {
            mensaje = $"Procesadas {processed} ordenes, {productosActualizados.Count} productos actualizados, {errors} errores",
            ordenesProcessadas = processed,
            sinProductoVinculado = skippedNoProduct,
            productosActualizados = productosActualizados,
            errores = errors,
            detalleErrores = errorMessages
        });
    }
}
