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
            .Include(p => p.BaseProduct)
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

        var derivedCounts = await _db.Products
            .Where(p => p.BaseProductId != null)
            .GroupBy(p => p.BaseProductId!.Value)
            .Select(g => new { ParentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ParentId, g => g.Count);

        return products.Select(p => new ProductListDto(
            p.Id, p.Title, p.Description,
            p.Brand, p.Model, p.Sku,
            p.Photo1, p.Photo2, p.Photo3,
            p.CostPrice, p.RetailPrice, p.Stock, p.CriticalStock,
            p.IsActive, p.CreatedAt, p.UpdatedAt,
            linksDict.GetValueOrDefault(p.Id, new List<ProductAccountLinkDto>()),
            p.BaseProductId,
            p.BaseProduct?.Sku,
            p.BaseProduct?.Title,
            derivedCounts.GetValueOrDefault(p.Id, 0)
        )).ToList();
    }

    public async Task<ProductListDto?> GetByIdAsync(int id)
    {
        var p = await _db.Products
            .Include(x => x.BaseProduct)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return null;

        var derivedCount = await _db.Products.CountAsync(x => x.BaseProductId == id);

        return new ProductListDto(
            p.Id, p.Title, p.Description,
            p.Brand, p.Model, p.Sku,
            p.Photo1, p.Photo2, p.Photo3,
            p.CostPrice, p.RetailPrice, p.Stock, p.CriticalStock,
            p.IsActive, p.CreatedAt, p.UpdatedAt,
            new List<ProductAccountLinkDto>(),
            p.BaseProductId,
            p.BaseProduct?.Sku,
            p.BaseProduct?.Title,
            derivedCount
        );
    }

    public async Task<ProductListDto?> CreateAsync(CreateProductRequest request)
    {
        // Validar y resolver producto base
        Product? baseProduct = null;
        if (request.BaseProductId.HasValue)
        {
            baseProduct = await _db.Products.FindAsync(request.BaseProductId.Value)
                ?? throw new InvalidOperationException("El producto base no existe.");
            if (baseProduct.BaseProductId.HasValue)
                throw new InvalidOperationException(
                    "El producto seleccionado ya hereda de otro: solo se permite un nivel de productos base.");
        }

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
            // Si tiene base, los precios se heredan; si no, los del request.
            CostPrice = baseProduct?.CostPrice ?? request.CostPrice,
            RetailPrice = baseProduct?.RetailPrice ?? request.RetailPrice,
            Stock = request.Stock,
            CriticalStock = request.CriticalStock,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            BaseProductId = request.BaseProductId
        };

        _db.Products.Add(product);
        await _db.SaveChangesAsync();

        return await GetByIdAsync(product.Id);
    }

    public async Task<ProductListDto?> UpdateAsync(int id, UpdateProductRequest request)
    {
        var product = await _db.Products.FindAsync(id);
        if (product is null) return null;

        // --- Manejo del producto base (asignar / cambiar / quitar) ---
        Product? newBase = null;

        if (request.ClearBaseProduct == true)
        {
            // Quitar la base: el producto pasa a ser independiente conservando el precio actual.
            if (product.BaseProductId.HasValue)
            {
                product.BaseProductId = null;
            }
        }
        else if (request.BaseProductId.HasValue && request.BaseProductId.Value != product.BaseProductId)
        {
            if (request.BaseProductId.Value == product.Id)
                throw new InvalidOperationException("Un producto no puede ser su propia base.");

            // No permitir asignar como base a un producto que ya tiene hijos? No, eso permite usarlo.
            // Pero no permitir asignar como base a un producto que YA TIENE BASE (1 nivel max).
            newBase = await _db.Products.FindAsync(request.BaseProductId.Value)
                ?? throw new InvalidOperationException("El producto base no existe.");

            if (newBase.BaseProductId.HasValue)
                throw new InvalidOperationException(
                    "El producto seleccionado ya hereda de otro: solo se permite un nivel de productos base.");

            // No permitir asignar como base a uno de los hijos del producto actual (evita ciclo)
            var hasDerived = await _db.Products.AnyAsync(p => p.BaseProductId == product.Id);
            if (hasDerived)
                throw new InvalidOperationException(
                    "Este producto ya es base de otros: no puede a su vez heredar de un tercero.");

            product.BaseProductId = newBase.Id;
        }

        // Recargar base actual (puede haber cambiado arriba)
        if (product.BaseProductId.HasValue && newBase is null)
        {
            newBase = await _db.Products.FindAsync(product.BaseProductId.Value);
        }

        // --- Resto de campos ---
        if (request.Title is not null) product.Title = request.Title;
        if (request.Description is not null) product.Description = request.Description;
        if (request.Brand is not null) product.Brand = request.Brand;
        if (request.Model is not null) product.Model = request.Model;
        if (request.Sku is not null) product.Sku = request.Sku == "" ? null : request.Sku;
        if (request.Photo1 is not null) product.Photo1 = request.Photo1 == "" ? null : request.Photo1;
        if (request.Photo2 is not null) product.Photo2 = request.Photo2 == "" ? null : request.Photo2;
        if (request.Photo3 is not null) product.Photo3 = request.Photo3 == "" ? null : request.Photo3;

        // Capturar precios viejos antes de aplicar cambios (para detectar propagacion a hijos)
        var oldCost = product.CostPrice;
        var oldRetail = product.RetailPrice;

        // Si el producto tiene base, los precios SIEMPRE se heredan del padre, ignoramos el request.
        if (newBase is not null)
        {
            product.CostPrice = newBase.CostPrice;
            product.RetailPrice = newBase.RetailPrice;
        }
        else
        {
            if (request.CostPrice.HasValue) product.CostPrice = request.CostPrice.Value;
            if (request.RetailPrice.HasValue) product.RetailPrice = request.RetailPrice.Value;
        }

        var oldStock = product.Stock;
        if (request.Stock.HasValue) product.Stock = request.Stock.Value;
        if (request.CriticalStock.HasValue) product.CriticalStock = request.CriticalStock.Value;
        if (request.IsActive.HasValue) product.IsActive = request.IsActive.Value;

        product.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // --- Propagar precio a hijos si este producto es base y cambio el precio ---
        var costChanged = product.CostPrice != oldCost;
        var retailChanged = product.RetailPrice != oldRetail;
        if (costChanged || retailChanged)
        {
            var derived = await _db.Products
                .Where(p => p.BaseProductId == product.Id)
                .ToListAsync();
            if (derived.Count > 0)
            {
                foreach (var child in derived)
                {
                    if (costChanged) child.CostPrice = product.CostPrice;
                    if (retailChanged) child.RetailPrice = product.RetailPrice;
                    child.UpdatedAt = DateTime.UtcNow;
                }
                await _db.SaveChangesAsync();
            }
        }

        // Propagar stock a publicaciones de MeLi si cambio
        if (request.Stock.HasValue && product.Stock != oldStock)
        {
            try { await _meliItemService.PropagateStockAsync(product.Id, product.Stock); }
            catch { /* No bloquear la actualizacion del producto si falla la propagacion */ }
        }

        return await GetByIdAsync(product.Id);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var product = await _db.Products.FindAsync(id);
        if (product is null) return false;

        // Desvincular hijos antes de borrar (quedan como productos independientes con su precio actual)
        var derived = await _db.Products.Where(p => p.BaseProductId == id).ToListAsync();
        foreach (var child in derived)
        {
            child.BaseProductId = null;
            child.UpdatedAt = DateTime.UtcNow;
        }
        if (derived.Count > 0) await _db.SaveChangesAsync();

        _db.Products.Remove(product);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<int> BulkDeleteAsync(List<int> ids)
    {
        var products = await _db.Products.Where(p => ids.Contains(p.Id)).ToListAsync();

        // Desvincular hijos cuyos padres estan en la lista de borrado
        var derived = await _db.Products
            .Where(p => p.BaseProductId != null && ids.Contains(p.BaseProductId!.Value) && !ids.Contains(p.Id))
            .ToListAsync();
        foreach (var child in derived)
        {
            child.BaseProductId = null;
            child.UpdatedAt = DateTime.UtcNow;
        }
        if (derived.Count > 0) await _db.SaveChangesAsync();

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
