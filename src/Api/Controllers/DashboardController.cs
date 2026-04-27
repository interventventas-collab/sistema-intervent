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
}
