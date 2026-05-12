using Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly AppDbContext _db;

    public DashboardController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var totalItems = await _db.MeliItems.CountAsync();
        var totalProducts = await _db.Products.CountAsync();
        var itemsSinProducto = await _db.MeliItems.CountAsync(i => i.ProductId == null);
        var productosSinItems = await _db.Products.CountAsync(p => !_db.MeliItems.Any(i => i.ProductId == p.Id));

        var accountStats = await _db.MeliAccounts
            .GroupJoin(
                _db.MeliItems,
                a => a.Id,
                i => i.MeliAccountId,
                (a, items) => new
                {
                    accountId = a.Id,
                    nickname = a.Nickname,
                    totalItems = items.Count(),
                    itemsConProducto = items.Count(i => i.ProductId != null),
                    itemsSinProducto = items.Count(i => i.ProductId == null),
                    productosVinculados = items.Where(i => i.ProductId != null).Select(i => i.ProductId).Distinct().Count()
                })
            .ToListAsync();

        return Ok(new
        {
            totalItems,
            totalProducts,
            itemsSinProducto,
            productosSinItems,
            accountStats
        });
    }

    /// <summary>
    /// Devuelve la sumatoria de kg de café vendidos en el mes actual desde el módulo
    /// Café (tablas Cafe_Ventas + Cafe_VentaItems). Suma los GramosDescontados de los
    /// items con Categoria='CAFE' de ventas no anuladas del mes en curso y los convierte
    /// a kg. Cuenta también cuántos items y cuántas ventas distintas.
    /// </summary>
    [HttpGet("coffee-monthly-kg")]
    public async Task<IActionResult> GetCoffeeMonthlyKg()
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var nextMonthStart = monthStart.AddMonths(1);

        // Items de ventas Café no anuladas, en el mes actual, con categoría "CAFE".
        // GramosDescontados ya viene calculado por línea (formato 1KG/MEDIO/CUARTO * cantidad).
        var rows = await _db.CafeVentaItems
            .Where(i => i.VentaNav != null
                        && i.VentaNav.Estado != "anulado"
                        && i.VentaNav.Fecha >= monthStart
                        && i.VentaNav.Fecha < nextMonthStart
                        && i.Categoria == "CAFE")
            .Select(i => new { i.GramosDescontados, i.VentaId })
            .ToListAsync();

        var gramosTotal = rows.Sum(r => r.GramosDescontados);
        var kgTotal = gramosTotal / 1000m;
        var items = rows.Count;
        var sales = rows.Select(r => r.VentaId).Distinct().Count();

        return Ok(new
        {
            kgTotal = Math.Round(kgTotal, 3),
            items,
            sales,
            periodStart = monthStart,
            periodEnd = nextMonthStart.AddMilliseconds(-1),
            generatedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Stock total de café disponible (en kg) desde el módulo Café (Cafe_Productos).
    /// Suma StockGramos de todos los productos con Categoria='CAFE' y activos, y lo
    /// convierte a kg. Cuenta variedades totales y cuántas tienen stock > 0.
    /// </summary>
    [HttpGet("coffee-stock-kg")]
    public async Task<IActionResult> GetCoffeeStockKg()
    {
        var rows = await _db.CafeProductos
            .Where(p => p.IsActive && p.Categoria == "CAFE")
            .Select(p => new { p.Id, p.Nombre, p.StockGramos })
            .ToListAsync();

        decimal kgTotal = rows.Sum(r => r.StockGramos) / 1000m;
        int variedades = rows.Count;
        int conStock = rows.Count(r => r.StockGramos > 0m);

        return Ok(new
        {
            kgTotal = Math.Round(kgTotal, 3),
            variedades,
            variedadesConStock = conStock,
            generatedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Resumen financiero del dashboard, vinculado al módulo Café (Cafe_Ventas):
    /// - Ventas del mes en curso (suma de Total + cantidad de comprobantes,
    ///   incluye cotización/proforma/FA/FB/FC, excluye anuladas).
    /// - Saldos pendientes de cobro: ventas no pagadas, no anuladas, con cliente asignado
    ///   (consumidor final sin pagar lo descartamos — generalmente es venta cash que el
    ///   operador no marcó).
    /// </summary>
    [HttpGet("sales-summary")]
    public async Task<IActionResult> GetSalesSummary()
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var nextMonthStart = monthStart.AddMonths(1);

        // Ventas del mes (no anuladas) — todas: cotización, proforma, FA, FB, FC.
        var monthlySales = await _db.CafeVentas
            .Where(s => s.Estado != "anulado"
                        && s.Fecha >= monthStart
                        && s.Fecha < nextMonthStart)
            .GroupBy(_ => 1)
            .Select(g => new { Total = g.Sum(s => s.Total), Count = g.Count() })
            .FirstOrDefaultAsync();

        // Saldos a cobrar: ventas no anuladas, no pagadas, con cliente asignado.
        var clientBalance = await _db.CafeVentas
            .Where(s => s.Estado != "anulado" && !s.IsPaid && s.ClienteId != null)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Sum(s => s.Total),
                Count = g.Count(),
                DistinctClients = g.Select(s => s.ClienteId).Distinct().Count()
            })
            .FirstOrDefaultAsync();

        return Ok(new
        {
            monthlySalesTotal = monthlySales?.Total ?? 0m,
            monthlySalesCount = monthlySales?.Count ?? 0,
            clientBalanceTotal = clientBalance?.Total ?? 0m,
            clientBalanceCount = clientBalance?.Count ?? 0,
            clientsWithBalance = clientBalance?.DistinctClients ?? 0,
            periodStart = monthStart,
            periodEnd = nextMonthStart.AddMilliseconds(-1),
            generatedAt = DateTime.UtcNow
        });
    }
}
