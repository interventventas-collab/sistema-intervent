using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-07-17: Arma y mantiene la base propia de clientes de MercadoLibre (tabla MeliClientes +
/// historial MeliClienteCompras). Toma las ventas de MeliOrders AGRUPADAS POR PAQUETE (una venta con
/// varios productos en un mismo carrito/pack es UNA sola compra), suma el contacto (telefono/direccion)
/// de MeliShipments cuando existe (Flex/ME1), y va acumulando.
///
/// Es INCREMENTAL e IDEMPOTENTE: solo procesa ventas que todavia no estan en MeliClienteCompras
/// (dedup por SaleKey = PackId o MeliOrderId). Correr dos veces no duplica ni cuenta de mas.
///
/// Ademas, para los clientes Flex/ME1 que todavia no tienen telefono, EnrichPhonesAsync va a buscarlo
/// a MeLi automaticamente (MeLi lo ofusca hasta que el envio avanza, asi que reintenta cada tantas horas).
/// </summary>
public class MeliClientesService
{
    private readonly AppDbContext _db;
    private readonly MeliShipmentService _shipmentService;

    public MeliClientesService(AppDbContext db, MeliShipmentService shipmentService)
    {
        _db = db;
        _shipmentService = shipmentService;
    }

    /// <summary>Procesa las ventas nuevas (agrupadas por paquete) y refresca telefonos desde los envios locales.
    /// Devuelve cuantas VENTAS proceso.</summary>
    public async Task<int> SyncAsync(int batchSize = 2000)
    {
        int totalProcessadas = 0;

        while (true)
        {
            // Ventas todavia no guardadas (por SaleKey = PackId ?? MeliOrderId).
            var nuevas = await _db.MeliOrders
                .Where(o => !_db.MeliClienteCompras.Any(c => c.SaleKey == (o.PackId ?? o.MeliOrderId)))
                .OrderBy(o => o.Id)
                .Take(batchSize)
                .ToListAsync();

            if (nuevas.Count == 0) break;

            // Completo los paquetes: traigo TODAS las lineas de los packs tocados (por si el batch corto un pack).
            var packIds = nuevas.Where(o => o.PackId != null).Select(o => o.PackId!.Value).Distinct().ToList();
            var siblings = packIds.Count == 0
                ? new List<MeliOrder>()
                : await _db.MeliOrders.Where(o => o.PackId != null && packIds.Contains(o.PackId.Value)).ToListAsync();

            // Union de las lineas del batch (sin pack) + todas las lineas de los packs tocados.
            var allRows = nuevas.Where(o => o.PackId == null)
                .Concat(siblings)
                .GroupBy(o => o.Id).Select(g => g.First())  // por las dudas, sin duplicar filas
                .ToList();

            // Precargo clientes de esos compradores.
            var buyerIds = allRows.Select(o => o.BuyerId).Distinct().ToList();
            var clientes = await _db.MeliClientes.Where(c => buyerIds.Contains(c.BuyerId)).ToListAsync();
            var byBuyer = clientes.ToDictionary(c => c.BuyerId);

            // Precargo envios (para el contacto) por ShippingId.
            var shipIds = allRows.Where(o => o.ShippingId != null).Select(o => o.ShippingId!.Value).Distinct().ToList();
            var shipments = shipIds.Count == 0
                ? new List<MeliShipment>()
                : await _db.MeliShipments.Where(s => shipIds.Contains(s.MeliShipmentId)).ToListAsync();
            var shipById = shipments.GroupBy(s => s.MeliShipmentId).ToDictionary(g => g.Key, g => g.First());

            // Agrupo por VENTA = SaleKey (PackId si existe, si no MeliOrderId).
            foreach (var grupo in allRows.GroupBy(o => o.PackId ?? o.MeliOrderId))
            {
                var saleKey = grupo.Key;
                // Si ya existe esa venta (pudo haberse creado en un batch anterior por un sibling), la salteo.
                if (await _db.MeliClienteCompras.AnyAsync(c => c.SaleKey == saleKey)) continue;

                var first = grupo.First();

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
                // Total del paquete = suma del total de cada ORDEN distinta (dentro de una orden el total se repite por linea).
                var totalVenta = grupo.GroupBy(x => x.MeliOrderId).Sum(og => og.Max(x => x.TotalAmount));
                var cantidad = grupo.Sum(x => x.Quantity);
                var items = string.Join(" + ", grupo
                    .Where(x => !string.IsNullOrWhiteSpace(x.ItemTitle))
                    .Select(x => $"{x.Quantity}x {x.ItemTitle}"));
                if (items.Length > 500) items = items[..500];
                var canal = first.LogisticType == "self_service" ? "Flex"
                          : (first.ShippingMode == "me1" ? "ME1" : "Correo");

                _db.MeliClienteCompras.Add(new MeliClienteCompra
                {
                    Cliente = cli,
                    BuyerId = first.BuyerId,
                    MeliOrderId = first.MeliOrderId,
                    PackId = first.PackId,
                    SaleKey = saleKey,
                    ShippingId = first.ShippingId,
                    Fecha = fecha,
                    Items = string.IsNullOrEmpty(items) ? null : items,
                    Cantidad = cantidad,
                    Total = totalVenta,
                    Canal = canal,
                    CreatedAt = DateTime.UtcNow
                });

                cli.OrdersCount += 1;
                cli.TotalSpent += totalVenta;
                if (cli.FirstPurchaseAt == null || fecha < cli.FirstPurchaseAt) cli.FirstPurchaseAt = fecha;
                if (cli.LastPurchaseAt == null || fecha >= cli.LastPurchaseAt)
                {
                    cli.LastPurchaseAt = fecha;
                    cli.LastItems = string.IsNullOrEmpty(items) ? null : items;
                    if (!string.IsNullOrWhiteSpace(first.BuyerNickname)) cli.Nickname = first.BuyerNickname;
                }

                // Contacto desde el envio local, si lo hay.
                MeliShipment? sh = null;
                if (first.ShippingId != null) shipById.TryGetValue(first.ShippingId.Value, out sh);
                if (sh != null && !string.IsNullOrWhiteSpace(sh.ReceiverPhone))
                    AplicarContacto(cli, sh, fecha);

                cli.UpdatedAt = DateTime.UtcNow;
                totalProcessadas++;
            }

            await _db.SaveChangesAsync();
            if (nuevas.Count < batchSize) break;
        }

        // Refresco telefonos desde los envios que YA tenemos localmente (gratis, sin llamar a MeLi).
        await RefreshPhonesFromLocalShipmentsAsync();

        return totalProcessadas;
    }

    private static void AplicarContacto(MeliCliente cli, MeliShipment sh, DateTime? fecha)
    {
        // Solo telefonos reales (MeLi manda "XXXXXXX" cuando todavia lo tapa).
        if (!TelefonoUtil.EsReal(sh.ReceiverPhone)) return;
        if (cli.LastContactAt != null && fecha != null && fecha < cli.LastContactAt) return;
        cli.Phone = sh.ReceiverPhone;
        if (!string.IsNullOrWhiteSpace(sh.ReceiverName)) cli.ReceiverName = sh.ReceiverName;
        cli.AddressLine = sh.AddressLine;
        cli.Neighborhood = sh.Neighborhood;
        cli.City = sh.City;
        cli.State = sh.State;
        cli.ZipCode = sh.ZipCode;
        cli.LastContactAt = fecha ?? cli.LastContactAt;
        cli.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Completa el telefono de los clientes que NO lo tienen, usando los envios que ya estan
    /// guardados localmente (no llama a MeLi). Rapido y gratis.</summary>
    private async Task RefreshPhonesFromLocalShipmentsAsync()
    {
        var matches = await (
            from c in _db.MeliClienteCompras
            join s in _db.MeliShipments on c.ShippingId equals s.MeliShipmentId
            join cli in _db.MeliClientes on c.MeliClienteId equals cli.Id
            where (cli.Phone == null || cli.Phone == "")
                  && s.ReceiverPhone != null && s.ReceiverPhone != ""
            select new { Cli = cli, Ship = s, c.Fecha }
        ).ToListAsync();

        foreach (var g in matches.GroupBy(m => m.Cli.Id))
        {
            var mejor = g.OrderByDescending(x => x.Fecha).First();
            AplicarContacto(mejor.Cli, mejor.Ship, mejor.Fecha);
        }
        if (matches.Count > 0) await _db.SaveChangesAsync();
    }

    /// <summary>Va a buscar a MeLi el telefono de los clientes Flex/ME1 que todavia no lo tienen. Acotado
    /// (maxLlamadas por vuelta) y con reintento cada 12h, para no martillar la API ni reintentar infinito
    /// ventas viejas que MeLi nunca libera. Solo mira ventas de los ultimos 90 dias.</summary>
    public async Task<int> EnrichPhonesAsync(int maxLlamadas = 100)
    {
        // MeLi libera el telefono en una ventana corta (cuando el envio esta "en camino"). Miramos ventas
        // recientes (30 dias) y reintentamos cada 6h para atrapar esa ventana sin martillar la API.
        var desde = DateTime.UtcNow.AddDays(-30);
        var reintentarAntesDe = DateTime.UtcNow.AddHours(-6);

        // Solo ME1: MeLi entrega el telefono real en ME1 (envio a coordinar). En Flex y correo lo esconde
        // siempre (lo verificamos preguntandole directo a MeLi), asi que no gastamos llamadas ahi.
        var candidatos = await _db.MeliClientes
            .Where(cli => (cli.Phone == null || cli.Phone == "")
                && (cli.PhoneCheckedAt == null || cli.PhoneCheckedAt < reintentarAntesDe)
                && _db.MeliClienteCompras.Any(c => c.MeliClienteId == cli.Id
                        && c.Canal == "ME1"
                        && c.ShippingId != null
                        && c.Fecha >= desde))
            .OrderByDescending(cli => cli.LastPurchaseAt)
            .Take(maxLlamadas)
            .ToListAsync();

        int traidos = 0;
        foreach (var cli in candidatos)
        {
            cli.PhoneCheckedAt = DateTime.UtcNow;

            var shippingId = await _db.MeliClienteCompras
                .Where(c => c.MeliClienteId == cli.Id && c.Canal == "ME1" && c.ShippingId != null)
                .OrderByDescending(c => c.Fecha)
                .Select(c => c.ShippingId)
                .FirstOrDefaultAsync();
            if (shippingId == null) continue;

            try
            {
                await _shipmentService.SyncSingleShipmentAsync(shippingId.Value);
                var sh = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == shippingId.Value);
                if (sh != null && !string.IsNullOrWhiteSpace(sh.ReceiverPhone))
                {
                    AplicarContacto(cli, sh, cli.LastPurchaseAt);
                    traidos++;
                }
            }
            catch { /* MeLi tuvo un problema temporal: se reintenta en la proxima vuelta */ }
        }

        await _db.SaveChangesAsync();
        return traidos;
    }
}
