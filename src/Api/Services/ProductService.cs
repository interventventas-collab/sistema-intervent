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
            .Include(p => p.BrandNav)
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

        // Stock total de cada padre = suma del stock de sus hijos.
        // Para padres con hijos, el campo Stock devuelto en el DTO es esa suma,
        // ignorando el Stock propio del padre en la DB.
        var childrenStockByParent = await _db.Products
            .Where(p => p.BaseProductId != null)
            .GroupBy(p => p.BaseProductId!.Value)
            .Select(g => new { ParentId = g.Key, Sum = g.Sum(p => p.Stock) })
            .ToDictionaryAsync(g => g.ParentId, g => g.Sum);

        return products.Select(p => new ProductListDto(
            p.Id, p.Title, p.DisplayName, p.Description,
            p.Brand, p.Model, p.Sku, p.Barcode, p.OemCode, p.ImageUrl,
            p.Photo1, p.Photo2, p.Photo3,
            p.CostPrice, p.RetailPrice, p.VatRate,
            p.PurchaseAccount, p.SaleAccount, p.InventoryAccount,
            childrenStockByParent.TryGetValue(p.Id, out var childSum) ? childSum : p.Stock,
            p.CriticalStock,
            p.IsActive, p.CreatedAt, p.UpdatedAt,
            linksDict.GetValueOrDefault(p.Id, new List<ProductAccountLinkDto>()),
            p.BaseProductId,
            p.BaseProduct?.Sku,
            p.BaseProduct?.Title,
            derivedCounts.GetValueOrDefault(p.Id, 0),
            p.BrandId,
            p.BrandNav?.Name,
            p.BrandNav?.HasExpiry ?? false,
            p.IsBase,
            p.IsService,
            p.UnitsPerPack
        )).ToList();
    }

    public async Task<ProductListDto?> GetByIdAsync(int id)
    {
        var p = await _db.Products
            .Include(x => x.BaseProduct)
            .Include(x => x.BrandNav)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return null;

        var derivedCount = await _db.Products.CountAsync(x => x.BaseProductId == id);
        // Stock efectivo: si tiene hijos, suma; si no, su propio stock.
        var effectiveStock = derivedCount > 0
            ? await _db.Products.Where(x => x.BaseProductId == id).SumAsync(x => x.Stock)
            : p.Stock;

        return new ProductListDto(
            p.Id, p.Title, p.DisplayName, p.Description,
            p.Brand, p.Model, p.Sku, p.Barcode, p.OemCode, p.ImageUrl,
            p.Photo1, p.Photo2, p.Photo3,
            p.CostPrice, p.RetailPrice, p.VatRate,
            p.PurchaseAccount, p.SaleAccount, p.InventoryAccount,
            effectiveStock, p.CriticalStock,
            p.IsActive, p.CreatedAt, p.UpdatedAt,
            new List<ProductAccountLinkDto>(),
            p.BaseProductId,
            p.BaseProduct?.Sku,
            p.BaseProduct?.Title,
            derivedCount,
            p.BrandId,
            p.BrandNav?.Name,
            p.BrandNav?.HasExpiry ?? false,
            p.IsBase,
            p.IsService,
            p.UnitsPerPack
        );
    }

    public async Task<ProductListDto?> CreateAsync(CreateProductRequest request)
    {
        var result = await CreateOrUpdateAsync(request);
        return result?.Product;
    }

    /// <summary>
    /// Upsert por SKU: si llega un SKU que ya existe, actualiza el producto en vez de crear uno duplicado.
    /// Devuelve action ("created"|"updated") y un warning si bajo el precio respecto al anterior.
    /// </summary>
    public async Task<ProductUpsertResult?> CreateOrUpdateAsync(CreateProductRequest request)
    {
        // 1) Si el SKU ya existe, hacer UPDATE en lugar de crear duplicado.
        if (!string.IsNullOrWhiteSpace(request.Sku))
        {
            var existing = await _db.Products.FirstOrDefaultAsync(p => p.Sku == request.Sku);
            if (existing is not null)
            {
                var oldCost = existing.CostPrice;
                var oldRetail = existing.RetailPrice;

                var update = new UpdateProductRequest(
                    Title: request.Title,
                    DisplayName: request.DisplayName,
                    Description: request.Description,
                    Brand: request.Brand,
                    Model: request.Model,
                    Sku: request.Sku,
                    Barcode: request.Barcode,
                    OemCode: request.OemCode,
                    ImageUrl: request.ImageUrl,
                    Photo1: request.Photo1,
                    Photo2: request.Photo2,
                    Photo3: request.Photo3,
                    CostPrice: request.CostPrice,
                    RetailPrice: request.RetailPrice,
                    VatRate: request.VatRate,
                    PurchaseAccount: request.PurchaseAccount,
                    SaleAccount: request.SaleAccount,
                    InventoryAccount: request.InventoryAccount,
                    Stock: request.Stock,
                    CriticalStock: request.CriticalStock,
                    IsActive: null,
                    BaseProductId: request.BaseProductId,
                    ClearBaseProduct: !request.BaseProductId.HasValue,
                    BrandId: request.BrandId,
                    ClearBrand: !request.BrandId.HasValue,
                    IsBase: request.IsBase,
                    IsService: request.IsService,
                    UnitsPerPack: request.UnitsPerPack,
                    ClearUnitsPerPack: !request.UnitsPerPack.HasValue
                );
                var updated = await UpdateAsync(existing.Id, update);
                if (updated is null) return null;

                // Detectar bajadas de precio
                string? warning = null;
                var costDropped = request.CostPrice > 0 && request.CostPrice < oldCost;
                var retailDropped = request.RetailPrice > 0 && request.RetailPrice < oldRetail;
                if (costDropped && retailDropped)
                    warning = $"Bajaron costo (de ${oldCost:0.##} a ${request.CostPrice:0.##}) y PVP (de ${oldRetail:0.##} a ${request.RetailPrice:0.##}).";
                else if (costDropped)
                    warning = $"Bajo el costo de ${oldCost:0.##} a ${request.CostPrice:0.##}.";
                else if (retailDropped)
                    warning = $"Bajo el PVP de ${oldRetail:0.##} a ${request.RetailPrice:0.##}.";

                return new ProductUpsertResult(updated, "updated", warning);
            }
        }

        // 2) No existe: crear nuevo (validar producto base y marca)
        Product? baseProduct = null;
        if (request.BaseProductId.HasValue)
        {
            baseProduct = await _db.Products.FindAsync(request.BaseProductId.Value)
                ?? throw new InvalidOperationException("El producto base no existe.");
            if (baseProduct.BaseProductId.HasValue)
                throw new InvalidOperationException(
                    "El producto seleccionado ya hereda de otro: solo se permite un nivel de productos base.");
        }

        Brand? brand = null;
        if (request.BrandId.HasValue)
        {
            brand = await _db.Brands.FindAsync(request.BrandId.Value)
                ?? throw new InvalidOperationException("La marca seleccionada no existe.");
        }

        var product = new Product
        {
            Title = request.Title,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName,
            Description = request.Description,
            Brand = brand?.Name ?? request.Brand,
            Model = request.Model,
            Sku = request.Sku,
            Barcode = string.IsNullOrWhiteSpace(request.Barcode) ? null : request.Barcode,
            OemCode = string.IsNullOrWhiteSpace(request.OemCode) ? null : request.OemCode,
            ImageUrl = string.IsNullOrWhiteSpace(request.ImageUrl) ? null : request.ImageUrl,
            Photo1 = request.Photo1,
            Photo2 = request.Photo2,
            Photo3 = request.Photo3,
            CostPrice = baseProduct?.CostPrice ?? request.CostPrice,
            RetailPrice = baseProduct?.RetailPrice ?? request.RetailPrice,
            VatRate = request.VatRate,
            PurchaseAccount = string.IsNullOrWhiteSpace(request.PurchaseAccount) ? null : request.PurchaseAccount,
            SaleAccount = string.IsNullOrWhiteSpace(request.SaleAccount) ? null : request.SaleAccount,
            InventoryAccount = string.IsNullOrWhiteSpace(request.InventoryAccount) ? null : request.InventoryAccount,
            Stock = request.Stock,
            CriticalStock = request.CriticalStock,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            BaseProductId = request.BaseProductId,
            BrandId = request.BrandId,
            IsBase = request.IsBase ?? false,
            IsService = request.IsService ?? false,
            UnitsPerPack = request.UnitsPerPack
        };

        _db.Products.Add(product);
        await _db.SaveChangesAsync();

        var dto = await GetByIdAsync(product.Id);
        return dto is null ? null : new ProductUpsertResult(dto, "created", null);
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

        // --- Manejo de marca (asignar / cambiar / quitar) ---
        if (request.ClearBrand == true)
        {
            product.BrandId = null;
            // Limpiamos tambien el campo legacy Brand para no dejar texto huerfano.
            product.Brand = null;
        }
        else if (request.BrandId.HasValue)
        {
            var brand = await _db.Brands.FindAsync(request.BrandId.Value)
                ?? throw new InvalidOperationException("La marca seleccionada no existe.");
            product.BrandId = brand.Id;
            // Sincronizamos el texto Brand con el nombre de la marca seleccionada.
            product.Brand = brand.Name;
        }

        // --- Resto de campos ---
        if (request.Title is not null) product.Title = request.Title;
        if (request.DisplayName is not null) product.DisplayName = request.DisplayName == "" ? null : request.DisplayName;
        if (request.Description is not null) product.Description = request.Description;
        // El input texto Brand solo se aplica si NO se mando una BrandId/ClearBrand
        // (si vino BrandId, ya sincronizamos el texto arriba).
        if (request.Brand is not null && !request.BrandId.HasValue && request.ClearBrand != true)
            product.Brand = request.Brand;
        if (request.Model is not null) product.Model = request.Model;
        if (request.Sku is not null) product.Sku = request.Sku == "" ? null : request.Sku;
        if (request.Barcode is not null) product.Barcode = request.Barcode == "" ? null : request.Barcode;
        if (request.OemCode is not null) product.OemCode = request.OemCode == "" ? null : request.OemCode;
        if (request.ImageUrl is not null) product.ImageUrl = request.ImageUrl == "" ? null : request.ImageUrl;
        if (request.Photo1 is not null) product.Photo1 = request.Photo1 == "" ? null : request.Photo1;
        if (request.Photo2 is not null) product.Photo2 = request.Photo2 == "" ? null : request.Photo2;
        if (request.Photo3 is not null) product.Photo3 = request.Photo3 == "" ? null : request.Photo3;
        var oldVat = product.VatRate;
        if (request.VatRate.HasValue) product.VatRate = request.VatRate.Value;
        if (request.PurchaseAccount is not null) product.PurchaseAccount = request.PurchaseAccount == "" ? null : request.PurchaseAccount;
        if (request.SaleAccount is not null) product.SaleAccount = request.SaleAccount == "" ? null : request.SaleAccount;
        if (request.InventoryAccount is not null) product.InventoryAccount = request.InventoryAccount == "" ? null : request.InventoryAccount;

        // Capturar precios viejos antes de aplicar cambios (para detectar propagacion a hijos)
        var oldCost = product.CostPrice;
        var oldRetail = product.RetailPrice;

        // Si el producto tiene base, los precios e IVA SIEMPRE se heredan del padre, ignoramos el request.
        if (newBase is not null)
        {
            product.CostPrice = newBase.CostPrice;
            product.RetailPrice = newBase.RetailPrice;
            product.VatRate = newBase.VatRate;
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
        if (request.IsBase.HasValue) product.IsBase = request.IsBase.Value;
        if (request.IsService.HasValue) product.IsService = request.IsService.Value;

        if (request.ClearUnitsPerPack == true)
            product.UnitsPerPack = null;
        else if (request.UnitsPerPack.HasValue)
            product.UnitsPerPack = request.UnitsPerPack.Value;

        product.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // --- Propagar precio/IVA a hijos si este producto es padre y algo cambio ---
        var costChanged = product.CostPrice != oldCost;
        var retailChanged = product.RetailPrice != oldRetail;
        var vatChanged = product.VatRate != oldVat;
        if (costChanged || retailChanged || vatChanged)
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
                    if (vatChanged) child.VatRate = product.VatRate;
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
