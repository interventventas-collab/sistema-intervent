using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class ProductService
{
    private readonly AppDbContext _db;
    private readonly MeliItemService _meliItemService;

    public ProductService(AppDbContext db, MeliItemService meliItemService)
    {
        _db = db;
        _meliItemService = meliItemService;
    }

    public async Task<List<ProductListDto>> GetAllAsync()
    {
        var products = await _db.Products
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        var productIds = products.Select(p => p.Id).ToList();

        var linksByProduct = await _db.MeliItems
            .Where(i => i.ProductId != null && productIds.Contains(i.ProductId.Value))
            .GroupBy(i => new { i.ProductId, i.MeliAccountId, i.MeliAccount!.Nickname })
            .Select(g => new { g.Key.ProductId, g.Key.Nickname, Count = g.Count() })
            .ToListAsync();

        var linksDict = linksByProduct
            .GroupBy(x => x.ProductId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => new ProductAccountLinkDto(x.Nickname ?? "", x.Count)).ToList()
            );

        return products.Select(p => new ProductListDto(
            p.Id, p.Title, p.Description,
            p.Brand, p.Model, p.Sku,
            p.Photo1, p.Photo2, p.Photo3,
            p.CostPrice, p.RetailPrice, p.Stock, p.CriticalStock,
            p.IsActive, p.CreatedAt, p.UpdatedAt,
            linksDict.GetValueOrDefault(p.Id, new List<ProductAccountLinkDto>())
        )).ToList();
    }

    public async Task<ProductListDto?> GetByIdAsync(int id)
    {
        return await _db.Products
            .Where(p => p.Id == id)
            .Select(p => new ProductListDto(
                p.Id, p.Title, p.Description,
                p.Brand, p.Model, p.Sku,
                p.Photo1, p.Photo2, p.Photo3,
                p.CostPrice, p.RetailPrice, p.Stock, p.CriticalStock,
                p.IsActive, p.CreatedAt, p.UpdatedAt,
                new List<ProductAccountLinkDto>()
            ))
            .FirstOrDefaultAsync();
    }

    public async Task<ProductListDto?> CreateAsync(CreateProductRequest request)
    {
        var product = new Product
        {
            Title = request.Title,
            Description = request.Description,
            Brand = request.Brand,
            Model = request.Model,
            Sku = request.Sku,
            Photo1 = request.Photo1,
            Photo2 = request.Photo2,
            Photo3 = request.Photo3,
            CostPrice = request.CostPrice,
            RetailPrice = request.RetailPrice,
            Stock = request.Stock,
            CriticalStock = request.CriticalStock,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _db.Products.Add(product);
        await _db.SaveChangesAsync();

        return new ProductListDto(
            product.Id, product.Title, product.Description,
            product.Brand, product.Model, product.Sku,
            product.Photo1, product.Photo2, product.Photo3,
            product.CostPrice, product.RetailPrice, product.Stock, product.CriticalStock,
            product.IsActive, product.CreatedAt, product.UpdatedAt,
            new List<ProductAccountLinkDto>()
        );
    }

    public async Task<ProductListDto?> UpdateAsync(int id, UpdateProductRequest request)
    {
        var product = await _db.Products.FindAsync(id);
        if (product is null) return null;

        if (request.Title is not null) product.Title = request.Title;
        if (request.Description is not null) product.Description = request.Description;
        if (request.Brand is not null) product.Brand = request.Brand;
        if (request.Model is not null) product.Model = request.Model;
        if (request.Sku is not null) product.Sku = request.Sku == "" ? null : request.Sku;
        if (request.Photo1 is not null) product.Photo1 = request.Photo1 == "" ? null : request.Photo1;
        if (request.Photo2 is not null) product.Photo2 = request.Photo2 == "" ? null : request.Photo2;
        if (request.Photo3 is not null) product.Photo3 = request.Photo3 == "" ? null : request.Photo3;
        if (request.CostPrice.HasValue) product.CostPrice = request.CostPrice.Value;
        if (request.RetailPrice.HasValue) product.RetailPrice = request.RetailPrice.Value;
        var oldStock = product.Stock;
        if (request.Stock.HasValue) product.Stock = request.Stock.Value;
        if (request.CriticalStock.HasValue) product.CriticalStock = request.CriticalStock.Value;
        if (request.IsActive.HasValue) product.IsActive = request.IsActive.Value;

        product.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Propagar stock a publicaciones de MeLi si cambio
        if (request.Stock.HasValue && product.Stock != oldStock)
        {
            try { await _meliItemService.PropagateStockAsync(product.Id, product.Stock); }
            catch { /* No bloquear la actualizacion del producto si falla la propagacion */ }
        }

        return new ProductListDto(
            product.Id, product.Title, product.Description,
            product.Brand, product.Model, product.Sku,
            product.Photo1, product.Photo2, product.Photo3,
            product.CostPrice, product.RetailPrice, product.Stock, product.CriticalStock,
            product.IsActive, product.CreatedAt, product.UpdatedAt,
            new List<ProductAccountLinkDto>()
        );
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var product = await _db.Products.FindAsync(id);
        if (product is null) return false;

        _db.Products.Remove(product);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<int> BulkDeleteAsync(List<int> ids)
    {
        var products = await _db.Products.Where(p => ids.Contains(p.Id)).ToListAsync();
        _db.Products.RemoveRange(products);
        await _db.SaveChangesAsync();
        return products.Count;
    }

    public async Task<int> BulkToggleStatusAsync(List<int> ids, bool isActive)
    {
        var products = await _db.Products.Where(p => ids.Contains(p.Id)).ToListAsync();
        foreach (var p in products)
        {
            p.IsActive = isActive;
            p.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return products.Count;
    }
}
