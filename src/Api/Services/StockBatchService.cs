using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class StockBatchService
{
    private readonly AppDbContext _db;
    private readonly MeliItemService _meliItemService;

    public StockBatchService(AppDbContext db, MeliItemService meliItemService)
    {
        _db = db;
        _meliItemService = meliItemService;
    }

    public async Task<List<StockBatchDto>> GetByProductAsync(int productId)
    {
        var batches = await _db.ProductStockBatches
            .Where(b => b.ProductId == productId)
            .OrderBy(b => b.ExpiryDate)
            .ToListAsync();

        var today = DateTime.UtcNow.Date;
        return batches.Select(b => ToDto(b, today)).ToList();
    }

    public async Task<StockBatchDto> CreateAsync(int productId, CreateStockBatchRequest r)
    {
        var product = await _db.Products.Include(p => p.BrandNav).FirstOrDefaultAsync(p => p.Id == productId)
            ?? throw new InvalidOperationException("Producto no encontrado.");

        if (r.Quantity < 1)
            throw new InvalidOperationException("La cantidad debe ser mayor a 0.");

        var batch = new ProductStockBatch
        {
            ProductId = productId,
            Quantity = r.Quantity,
            ExpiryDate = r.ExpiryDate.Date,
            Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _db.ProductStockBatches.Add(batch);
        await _db.SaveChangesAsync();

        await RecalculateProductStockAsync(productId);

        return ToDto(batch, DateTime.UtcNow.Date);
    }

    public async Task<StockBatchDto?> UpdateAsync(int batchId, UpdateStockBatchRequest r)
    {
        var batch = await _db.ProductStockBatches.FindAsync(batchId);
        if (batch is null) return null;

        if (r.Quantity.HasValue) batch.Quantity = r.Quantity.Value;
        if (r.ExpiryDate.HasValue) batch.ExpiryDate = r.ExpiryDate.Value.Date;
        if (r.Notes is not null) batch.Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim();
        batch.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await RecalculateProductStockAsync(batch.ProductId);

        return ToDto(batch, DateTime.UtcNow.Date);
    }

    public async Task<bool> DeleteAsync(int batchId)
    {
        var batch = await _db.ProductStockBatches.FindAsync(batchId);
        if (batch is null) return false;
        var productId = batch.ProductId;
        _db.ProductStockBatches.Remove(batch);
        await _db.SaveChangesAsync();
        await RecalculateProductStockAsync(productId);
        return true;
    }

    private async Task RecalculateProductStockAsync(int productId)
    {
        var product = await _db.Products.FindAsync(productId);
        if (product is null) return;

        var totalQty = await _db.ProductStockBatches
            .Where(b => b.ProductId == productId)
            .SumAsync(b => (int?)b.Quantity) ?? 0;

        var oldStock = product.Stock;
        product.Stock = totalQty;
        product.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        if (totalQty != oldStock)
        {
            try { await _meliItemService.PropagateStockAsync(product.Id, totalQty); }
            catch { /* No bloquear si falla la propagacion */ }
        }
    }

    private static StockBatchDto ToDto(ProductStockBatch b, DateTime today)
    {
        var days = (int)(b.ExpiryDate.Date - today).TotalDays;
        return new StockBatchDto(
            b.Id, b.ProductId, b.Quantity, b.ExpiryDate,
            days, days < 0,
            b.Notes, b.CreatedAt, b.UpdatedAt);
    }
}
