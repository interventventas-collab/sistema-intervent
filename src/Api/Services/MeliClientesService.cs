using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-07-17: Arma y mantiene la base propia de clientes de MercadoLibre (tabla MeliClientes +
/// historial MeliClienteCompras). Toma las ventas de MeliOrders (agrupadas por venta), suma el
/// contacto (telefono/direccion) de MeliShipments cuando existe (Flex/ME1), y va acumulando.
///
/// Es INCREMENTAL e IDEMPOTENTE: solo procesa ventas que todavia no estan en MeliClienteCompras
/// (dedup por MeliOrderId). Sirve tanto para el backfill inicial ("traer lo antiguo") como para
/// el robot que la mantiene al dia. Correr dos veces no duplica ni cuenta de mas.
/// </summary>
public class MeliClientesService
{
    private readonly AppDbContext _db;

    public MeliClientesService(AppDbContext db) { _db = db; }

    /// <summary>Procesa todas las ventas nuevas (en tandas). Devuelve cuantas VENTAS proceso.</summary>
    public async Task<int> SyncAsync(int batchSize = 2000)
    {
        int totalProcessadas = 0;

        while (true)
        {
            // Numeros de venta que todavia no estan en el historial de clientes.
            var nuevosOrderIds = await _db.MeliOrders
                .Where(o => !_db.MeliClienteCompras.Any(c => c.MeliOrderId == o.MeliOrderId))
                .Select(o => o.MeliOrderId)
                .Distinct()
                .OrderBy(id => id)
                .Take(batchSize)
                .ToListAsync();

            if (nuevosOrderIds.Count == 0) break;

            // Traigo TODAS las lineas de esas ventas (una venta con varios productos = varias filas).
            var rows = await _db.MeliOrders
                .Where(o => nuevosOrderIds.Contains(o.MeliOrderId))
                .ToListAsync();

            // Precargo los clientes de esos compradores.
            var buyerIds = rows.Select(o => o.BuyerId).Distinct().ToList();
            var clientes = await _db.MeliClientes
                .Where(c => buyerIds.Contains(c.BuyerId))
                .ToListAsync();
            var byBuyer = clientes.ToDictionary(c => c.BuyerId);

            // Precargo los envios (para el contacto) por ShippingId.
            var shipIds = rows.Where(o => o.ShippingId != null).Select(o => o.ShippingId!.Value).Distinct().ToList();
            var shipments = shipIds.Count == 0
                ? new List<MeliShipment>()
                : await _db.MeliShipments.Where(s => shipIds.Contains(s.MeliShipmentId)).ToListAsync();
            var shipById = shipments.GroupBy(s => s.MeliShipmentId).ToDictionary(g => g.Key, g => g.First());

            foreach (var grupo in rows.GroupBy(o => o.MeliOrderId))
            {
                var first = grupo.First();

                // Cliente (crear si no existe).
                if (!byBuyer.TryGetValue(first.BuyerId, out var cli))
                {
                    cli = new MeliCliente
                    {
                        BuyerId = first.BuyerId,
                        Nickname = first.BuyerNickname,
                        CreatedAt = DateTime.UtcNow
                    };
                    _db.MeliClientes.Add(cli);
                    byBuyer[first.BuyerId] = cli;
                }

                var fecha = grupo.Min(x => x.DateCreated);
                var totalVenta = grupo.Max(x => x.TotalAmount); // el total_amount de MeLi es de la venta, igual en cada linea
                var cantidad = grupo.Sum(x => x.Quantity);
                var items = string.Join(" + ", grupo
                    .Where(x => !string.IsNullOrWhiteSpace(x.ItemTitle))
                    .Select(x => $"{x.Quantity}x {x.ItemTitle}"));
                if (items.Length > 500) items = items[..500];
                var canal = first.LogisticType == "self_service" ? "Flex"
                          : (first.ShippingMode == "me1" ? "ME1" : "Correo");

                // Historial de compra (dedup por MeliOrderId).
                _db.MeliClienteCompras.Add(new MeliClienteCompra
                {
                    Cliente = cli,
                    BuyerId = first.BuyerId,
                    MeliOrderId = first.MeliOrderId,
                    Fecha = fecha,
                    Items = string.IsNullOrEmpty(items) ? null : items,
                    Cantidad = cantidad,
                    Total = totalVenta,
                    Canal = canal,
                    CreatedAt = DateTime.UtcNow
                });

                // Agregados del cliente.
                cli.OrdersCount += 1;
                cli.TotalSpent += totalVenta;
                if (cli.FirstPurchaseAt == null || fecha < cli.FirstPurchaseAt) cli.FirstPurchaseAt = fecha;
                if (cli.LastPurchaseAt == null || fecha >= cli.LastPurchaseAt)
                {
                    cli.LastPurchaseAt = fecha;
                    cli.LastItems = string.IsNullOrEmpty(items) ? null : items;
                    if (!string.IsNullOrWhiteSpace(first.BuyerNickname)) cli.Nickname = first.BuyerNickname;
                }

                // Contacto (telefono/direccion) desde el envio, si lo hay. Guardo el mas reciente.
                MeliShipment? sh = null;
                if (first.ShippingId != null) shipById.TryGetValue(first.ShippingId.Value, out sh);
                if (sh != null && !string.IsNullOrWhiteSpace(sh.ReceiverPhone))
                {
                    if (cli.LastContactAt == null || fecha >= cli.LastContactAt)
                    {
                        cli.Phone = sh.ReceiverPhone;
                        if (!string.IsNullOrWhiteSpace(sh.ReceiverName)) cli.ReceiverName = sh.ReceiverName;
                        cli.AddressLine = sh.AddressLine;
                        cli.Neighborhood = sh.Neighborhood;
                        cli.City = sh.City;
                        cli.State = sh.State;
                        cli.ZipCode = sh.ZipCode;
                        cli.LastContactAt = fecha;
                    }
                }

                cli.UpdatedAt = DateTime.UtcNow;
                totalProcessadas++;
            }

            await _db.SaveChangesAsync();
            if (nuevosOrderIds.Count < batchSize) break;
        }

        return totalProcessadas;
    }
}
