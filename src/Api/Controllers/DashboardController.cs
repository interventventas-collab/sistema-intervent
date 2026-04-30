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
}
