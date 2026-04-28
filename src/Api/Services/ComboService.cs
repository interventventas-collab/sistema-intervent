using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class ComboService
{
    private readonly AppDbContext _db;
    private const string SkuPrefix = "COMBO-";
    private static readonly string[] AllowedPriceModes = new[] { "auto", "manual", "percent" };

    public ComboService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<ComboDto>> GetAllAsync()
    {
        var combos = await _db.Combos
            .Include(c => c.Items).ThenInclude(i => i.Product)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

        return combos.Select(BuildDto).ToList();
    }

    public async Task<ComboDto?> GetByIdAsync(int id)
    {
        var c = await _db.Combos
            .Include(x => x.Items).ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(x => x.Id == id);
        return c is null ? null : BuildDto(c);
    }

    public async Task<ComboDto> CreateAsync(CreateComboRequest request)
    {
        ValidatePriceMode(request.PriceMode);
        if (request.Items is null || request.Items.Count == 0)
            throw new InvalidOperationException("El combo debe tener al menos un producto.");
        ValidateItemsExist(request.Items);

        await EnsureProductsExistAsync(request.Items.Select(i => i.ProductId).ToList());

        var sku = string.IsNullOrWhiteSpace(request.Sku) ? await GenerateNextSkuAsync() : request.Sku.Trim();

        var combo = new Combo
        {
            Name = request.Name.Trim(),
            Sku = sku,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description,
            Photo = string.IsNullOrWhiteSpace(request.Photo) ? null : request.Photo,
            PriceMode = request.PriceMode,
            ManualPrice = request.PriceMode == "manual" ? request.ManualPrice : null,
            PercentAdjustment = request.PriceMode == "percent" ? request.PercentAdjustment : null,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            Items = request.Items.Select(i => new ComboItem
            {
                ProductId = i.ProductId,
                Quantity = Math.Max(1, i.Quantity)
            }).ToList()
        };

        _db.Combos.Add(combo);
        await _db.SaveChangesAsync();

        return (await GetByIdAsync(combo.Id))!;
    }

    public async Task<ComboDto?> UpdateAsync(int id, UpdateComboRequest request)
    {
        var combo = await _db.Combos
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (combo is null) return null;

        if (request.Name is not null) combo.Name = request.Name.Trim();
        if (request.Sku is not null) combo.Sku = string.IsNullOrWhiteSpace(request.Sku) ? null : request.Sku.Trim();
        if (request.Description is not null) combo.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description;
        if (request.Photo is not null) combo.Photo = string.IsNullOrWhiteSpace(request.Photo) ? null : request.Photo;
        if (request.PriceMode is not null)
        {
            ValidatePriceMode(request.PriceMode);
            combo.PriceMode = request.PriceMode;
        }
        // Limpiar campos que no aplican al modo elegido para evitar valores huerfanos.
        combo.ManualPrice = combo.PriceMode == "manual" ? (request.ManualPrice ?? combo.ManualPrice) : null;
        combo.PercentAdjustment = combo.PriceMode == "percent" ? (request.PercentAdjustment ?? combo.PercentAdjustment) : null;

        if (request.IsActive.HasValue) combo.IsActive = request.IsActive.Value;

        if (request.Items is not null)
        {
            if (request.Items.Count == 0)
                throw new InvalidOperationException("El combo debe tener al menos un producto.");
            ValidateItemsExist(request.Items);
            await EnsureProductsExistAsync(request.Items.Select(i => i.ProductId).ToList());

            // Reemplazo total: borra los existentes y agrega los nuevos.
            _db.ComboItems.RemoveRange(combo.Items);
            combo.Items = request.Items.Select(i => new ComboItem
            {
                ProductId = i.ProductId,
                Quantity = Math.Max(1, i.Quantity)
            }).ToList();
        }

        combo.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return await GetByIdAsync(combo.Id);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var c = await _db.Combos.FindAsync(id);
        if (c is null) return false;
        _db.Combos.Remove(c);
        await _db.SaveChangesAsync();
        return true;
    }

    // --- Helpers ---

    public async Task<string> GenerateNextSkuAsync()
    {
        var existing = await _db.Combos
            .Where(c => c.Sku != null && c.Sku.StartsWith(SkuPrefix))
            .Select(c => c.Sku!)
            .ToListAsync();

        int max = 0;
        foreach (var sku in existing)
        {
            var numPart = sku.Substring(SkuPrefix.Length);
            if (int.TryParse(numPart, out var num) && num > max) max = num;
        }
        return $"{SkuPrefix}{(max + 1):D3}";
    }

    private static void ValidatePriceMode(string mode)
    {
        if (!AllowedPriceModes.Contains(mode))
            throw new InvalidOperationException($"Modo de precio invalido: '{mode}'. Valores aceptados: auto, manual, percent.");
    }

    private static void ValidateItemsExist(List<ComboItemRequest> items)
    {
        if (items.Any(i => i.Quantity < 1))
            throw new InvalidOperationException("La cantidad de cada producto debe ser al menos 1.");
        var dupeIds = items.GroupBy(i => i.ProductId).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupeIds.Count > 0)
            throw new InvalidOperationException("No se puede agregar el mismo producto mas de una vez al combo. Aumenta la cantidad en su lugar.");
    }

    private async Task EnsureProductsExistAsync(List<int> productIds)
    {
        var existing = await _db.Products
            .Where(p => productIds.Contains(p.Id))
            .Select(p => p.Id).ToListAsync();
        var missing = productIds.Except(existing).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Algunos productos no existen: {string.Join(", ", missing)}");
    }

    private static ComboDto BuildDto(Combo c)
    {
        var items = c.Items
            .OrderBy(i => i.Id)
            .Select(i => new ComboItemDto(
                i.Id,
                i.ProductId,
                i.Product?.Title ?? "Producto eliminado",
                i.Product?.Sku,
                i.Quantity,
                i.Product?.RetailPrice ?? 0m,
                (i.Product?.RetailPrice ?? 0m) * i.Quantity
            )).ToList();

        var subtotal = items.Sum(x => x.LineTotal);
        var final = c.PriceMode switch
        {
            "manual" => c.ManualPrice ?? subtotal,
            "percent" => Math.Round(subtotal * (1m + (c.PercentAdjustment ?? 0m) / 100m), 2),
            _ => subtotal // auto
        };

        return new ComboDto(
            c.Id, c.Name, c.Sku, c.Description, c.Photo,
            c.PriceMode, c.ManualPrice, c.PercentAdjustment,
            c.IsActive, c.CreatedAt, c.UpdatedAt,
            items, subtotal, final
        );
    }
}
