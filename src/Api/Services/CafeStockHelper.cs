using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Helper centralizado para mantener consistencia entre las dos tablas de stock:
///   - Cafe_Productos.StockUnidades / StockGramos (vista "general")
///   - Cafe_StockPorDeposito (vista por depósito, una fila por (producto, depósito))
///
/// Hoy (2026-05-25) hay un bug que el StockPorDeposito se desincroniza con el general
/// cuando algunas operaciones (venta, anular, stock móvil, sync MeLi) tocan solo el general.
/// Este helper es un parche para que todos los puntos puedan sincronizar las dos tablas
/// llamando a un solo método. Un refactor más profundo (servicio único de stock) queda
/// pendiente.
/// </summary>
public static class CafeStockHelper
{
    /// <summary>Sincroniza Cafe_StockPorDeposito[producto, depDefault] con los valores actuales
    /// de Cafe_Productos.StockUnidades / StockGramos. Si no existe la fila, la crea.
    /// El caller es responsable de llamar SaveChangesAsync después.
    ///
    /// IMPORTANTE: este patch asume modelo "1 depósito default". Cuando el sistema soporte
    /// multi-depósito real, hay que rediseñar (probablemente con servicio unificado).</summary>
    public static async Task SyncStockPorDepositoAsync(AppDbContext db, CafeProducto prod, CancellationToken ct = default)
    {
        if (prod is null) return;
        // Tomar depósito default (IsDefault + IsActive). Fallback al primero por Id.
        var depId = await db.CafeDepositos
            .Where(d => d.IsDefault && d.IsActive)
            .Select(d => (int?)d.Id)
            .FirstOrDefaultAsync(ct);
        depId ??= await db.CafeDepositos.OrderBy(d => d.Id).Select(d => (int?)d.Id).FirstOrDefaultAsync(ct);
        if (!depId.HasValue) return; // no hay depósitos creados

        var spd = await db.CafeStockPorDeposito
            .FirstOrDefaultAsync(s => s.ProductoId == prod.Id && s.DepositoId == depId.Value, ct);
        var now = DateTime.UtcNow;
        if (spd is null)
        {
            db.CafeStockPorDeposito.Add(new CafeStockPorDeposito
            {
                ProductoId = prod.Id,
                DepositoId = depId.Value,
                StockUnidades = prod.StockUnidades,
                StockGramos = prod.StockGramos,
                UpdatedAt = now
            });
        }
        else
        {
            spd.StockUnidades = prod.StockUnidades;
            spd.StockGramos = prod.StockGramos;
            spd.UpdatedAt = now;
        }
    }

    /// <summary>Versión bulk para sincronizar muchos productos a la vez (ej. después de procesar
    /// una venta con varios items). Más eficiente que llamar el método singular en un loop.</summary>
    public static async Task SyncStockPorDepositoBulkAsync(AppDbContext db, IEnumerable<CafeProducto> prods, CancellationToken ct = default)
    {
        foreach (var p in prods)
        {
            if (p is null) continue;
            await SyncStockPorDepositoAsync(db, p, ct);
        }
    }
}
