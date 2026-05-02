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
    /// Devuelve la sumatoria de kg de café vendidos en el mes actual.
    /// Considera todos los items de venta cuyo producto tenga marca FRIKAF
    /// (case insensitive, ignorando el simbolo de marca registrada).
    /// La cantidad de kg por linea es: Quantity * Product.Fraction.
    /// Excluye ventas anuladas.
    /// </summary>
    [HttpGet("coffee-monthly-kg")]
    public async Task<IActionResult> GetCoffeeMonthlyKg()
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var nextMonthStart = monthStart.AddMonths(1);

        // Items de ventas no anuladas, en el mes actual, de productos marca FRIKAF.
        // Sumamos Quantity * Fraction (= kg vendidos por linea).
        var query =
            from si in _db.SaleItems
            join s in _db.Sales on si.SaleId equals s.Id
            join p in _db.Products on si.ProductId equals p.Id
            where !s.IsCancelled
                && s.Date >= monthStart && s.Date < nextMonthStart
                && p.Brand != null
                && p.Brand.ToUpper().Contains("FRIKAF")
            select new { si.Quantity, p.Fraction };

        // Ejecutar y sumar en memoria (Quantity es decimal, Fraction es decimal — multiplicacion segura)
        var rows = await query.ToListAsync();
        decimal kgTotal = 0m;
        int items = 0;
        foreach (var r in rows)
        {
            kgTotal += r.Quantity * r.Fraction;
            items++;
        }

        // Cuantas ventas distintas
        var distinctSales = await (
            from si in _db.SaleItems
            join s in _db.Sales on si.SaleId equals s.Id
            join p in _db.Products on si.ProductId equals p.Id
            where !s.IsCancelled
                && s.Date >= monthStart && s.Date < nextMonthStart
                && p.Brand != null
                && p.Brand.ToUpper().Contains("FRIKAF")
            select s.Id).Distinct().CountAsync();

        return Ok(new
        {
            kgTotal = Math.Round(kgTotal, 3),
            items,
            sales = distinctSales,
            periodStart = monthStart,
            periodEnd = nextMonthStart.AddMilliseconds(-1),
            generatedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Stock total de cafe disponible (en kg). Suma el Stock de todos los productos
    /// que estan en kg-mode (StockUnit='kg'), que son los padres de cada variedad de cafe.
    /// </summary>
    [HttpGet("coffee-stock-kg")]
    public async Task<IActionResult> GetCoffeeStockKg()
    {
        var rows = await _db.Products
            .Where(p => p.IsActive && p.StockUnit == "kg")
            .Select(p => new { p.Id, p.Title, p.Stock })
            .ToListAsync();

        decimal kgTotal = rows.Sum(r => r.Stock);
        int variedades = rows.Count;
        int conStock = rows.Count(r => r.Stock > 0);

        return Ok(new
        {
            kgTotal = Math.Round(kgTotal, 3),
            variedades,
            variedadesConStock = conStock,
            generatedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Resumen financiero del dashboard:
    /// - Ventas del mes en curso (suma de Total + cantidad)
    /// - Saldos pendientes de cobro a clientes (ventas !IsPaid !IsCancelled)
    /// </summary>
    [HttpGet("sales-summary")]
    public async Task<IActionResult> GetSalesSummary()
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var nextMonthStart = monthStart.AddMonths(1);

        // Ventas del mes (no anuladas)
        var monthlySales = await _db.Sales
            .Where(s => !s.IsCancelled && s.Date >= monthStart && s.Date < nextMonthStart)
            .GroupBy(_ => 1)
            .Select(g => new { Total = g.Sum(s => s.Total), Count = g.Count() })
            .FirstOrDefaultAsync();

        // Saldos a cobrar: ventas no pagadas, no anuladas, con cliente asignado
        // (consumidor final sin pagar lo descartamos — generalmente es venta cash que el operador no marco)
        var clientBalance = await _db.Sales
            .Where(s => !s.IsCancelled && !s.IsPaid && s.ClientId != null)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Sum(s => s.Total),
                Count = g.Count(),
                DistinctClients = g.Select(s => s.ClientId).Distinct().Count()
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
