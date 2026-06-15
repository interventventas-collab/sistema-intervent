using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>2026-06-15: Calcula unidades "reservadas" — ventas MeLi cuyo stock YA se descontó
/// del sistema pero el producto sigue físicamente en mi depósito porque la etiqueta no se imprimió
/// (Flex) o el correo todavía no pasó a buscarla (ME1 / cross_docking). Full queda afuera.
///
/// Sirve para que en la auditoría móvil el operario vea el "stock físico esperado" = sistema + reservado,
/// evitando ajustes erróneos cuando todavía hay paquetes esperando despacho en el depósito.</summary>
public class StockReservaService
{
    private readonly AppDbContext _db;
    public StockReservaService(AppDbContext db) { _db = db; }

    // Sub-estados que indican "producto sigue físicamente en mi depósito"
    // Flex (self_service): solo ready_to_print → apenas se imprime la etiqueta, sale ese día con el conductor
    private static readonly HashSet<string> SubEstadosFlexReservados = new(StringComparer.OrdinalIgnoreCase)
    {
        "ready_to_print"
    };
    // ME1 (cross_docking): producto sigue acá hasta que el correo lo retire (picked_up).
    // Todos los estados previos cuentan como reservado.
    private static readonly HashSet<string> SubEstadosCrossDockingReservados = new(StringComparer.OrdinalIgnoreCase)
    {
        "ready_to_print", "ready_to_pack", "in_packing_list",
        "ready_for_pickup", "buffered", "waiting_for_withdrawal"
    };

    /// <summary>Devuelve dict { productoId -> unidades reservadas }. Solo incluye productos con reserva > 0.
    /// Si productoIds está vacío, devuelve dict vacío.</summary>
    public async Task<Dictionary<int, int>> GetReservasAsync(IReadOnlyCollection<int> productoIds)
    {
        var result = new Dictionary<int, int>();
        if (productoIds is null || productoIds.Count == 0) return result;

        // Tope de antiguedad: ignorar órdenes muy viejas. Las ventas que están físicamente
        // en el depósito tienen pocos días — no semanas.
        var desde = DateTime.UtcNow.AddDays(-14);

        var idsSet = productoIds.ToHashSet();

        // Join: órdenes paid no-Full reciente + componentes que mapean al producto sistema.
        // Se calcula la cantidad efectiva como Quantity (orden) * Cantidad (componente del combo).
        var rows = await (
            from o in _db.MeliOrders
            join c in _db.MeliItemComponentes on o.ItemId equals c.MeliItemId
            where o.Status == "paid"
               && o.StockDiscounted
               && o.LogisticType != "fulfillment"        // Full afuera
               && o.DateCreated >= desde
               && idsSet.Contains(c.CafeProductoId)
            select new
            {
                ProductoId = c.CafeProductoId,
                OrderQty = o.Quantity,
                CompQty = c.Cantidad,
                Logistic = o.LogisticType,
                SubStatus = o.ShippingSubstatus
            }
        ).ToListAsync();

        foreach (var r in rows)
        {
            // Decidir si está reservada según logística + substatus
            if (string.IsNullOrWhiteSpace(r.SubStatus)) continue; // sin substatus = no podemos asegurar que sigue acá
            bool reservada = false;
            if (string.Equals(r.Logistic, "self_service", StringComparison.OrdinalIgnoreCase))
                reservada = SubEstadosFlexReservados.Contains(r.SubStatus);
            else if (string.Equals(r.Logistic, "cross_docking", StringComparison.OrdinalIgnoreCase))
                reservada = SubEstadosCrossDockingReservados.Contains(r.SubStatus);
            // (fulfillment ya filtrado arriba; tipos desconocidos los ignoramos)

            if (!reservada) continue;

            // unidades = cantidad de la orden * cantidad del componente (caso combo). Redondear al entero superior.
            var unidades = (int)Math.Ceiling((decimal)r.OrderQty * r.CompQty);
            if (!result.ContainsKey(r.ProductoId)) result[r.ProductoId] = 0;
            result[r.ProductoId] += unidades;
        }
        return result;
    }

    /// <summary>Versión cómoda para un solo producto.</summary>
    public async Task<int> GetReservaAsync(int productoId)
    {
        var d = await GetReservasAsync(new[] { productoId });
        return d.TryGetValue(productoId, out var v) ? v : 0;
    }

    /// <summary>2026-06-15: Devuelve el detalle COMPLETO de cada orden MeLi pendiente,
    /// con el producto que reserva, la fecha, sub-status, etc. Para auditar contra el panel MeLi.</summary>
    public async Task<List<ReservaDetalleDto>> GetReservasDetalleAsync(int dias = 14)
    {
        var desde = DateTime.UtcNow.AddDays(-Math.Max(1, dias));
        var rows = await (
            from o in _db.MeliOrders
            join c in _db.MeliItemComponentes on o.ItemId equals c.MeliItemId
            join p in _db.CafeProductos on c.CafeProductoId equals p.Id
            where o.Status == "paid"
               && o.StockDiscounted
               && o.LogisticType != "fulfillment"
               && o.DateCreated >= desde
               && o.ShippingSubstatus != null
            select new
            {
                o.MeliOrderId,
                o.ItemId,
                o.Quantity,
                o.ShippingSubstatus,
                o.LogisticType,
                o.DateCreated,
                o.UpdatedAt,
                CompCantidad = c.Cantidad,
                ProductoId = p.Id,
                p.Sku,
                p.Nombre,
                p.StockUnidades
            }
        ).ToListAsync();

        var result = new List<ReservaDetalleDto>();
        foreach (var r in rows)
        {
            bool reservada = false;
            if (string.Equals(r.LogisticType, "self_service", StringComparison.OrdinalIgnoreCase))
                reservada = SubEstadosFlexReservados.Contains(r.ShippingSubstatus!);
            else if (string.Equals(r.LogisticType, "cross_docking", StringComparison.OrdinalIgnoreCase))
                reservada = SubEstadosCrossDockingReservados.Contains(r.ShippingSubstatus!);
            if (!reservada) continue;

            var unidades = (int)Math.Ceiling((decimal)r.Quantity * r.CompCantidad);
            result.Add(new ReservaDetalleDto(
                r.ProductoId, r.Sku ?? "", r.Nombre, r.StockUnidades,
                r.MeliOrderId, r.ItemId ?? "",
                r.Quantity, r.CompCantidad, unidades,
                r.ShippingSubstatus ?? "", r.LogisticType ?? "",
                r.DateCreated, r.UpdatedAt));
        }
        return result
            .OrderBy(x => x.Sku)
            .ThenByDescending(x => x.DateCreated)
            .ToList();
    }
}

public record ReservaDetalleDto(
    int ProductoId, string Sku, string Nombre, int StockDisponibles,
    long MeliOrderId, string MeliItemId,
    int OrderQty, decimal CompQty, int UnidadesReservadas,
    string ShippingSubstatus, string LogisticType,
    DateTime DateCreated, DateTime? UpdatedAt);
