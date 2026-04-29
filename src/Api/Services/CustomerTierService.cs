using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// CRUD de listas de precios + helper para calcular el precio de un producto
/// segun la lista de un cliente. La logica de calculo es:
/// 1) Si hay un ProductPriceOverride para (productoId, tierId), gana esa fila.
/// 2) Si no, se aplica el AdjustmentPercent de la lista sobre Product.RetailPrice.
/// </summary>
public class CustomerTierService
{
    private readonly AppDbContext _db;

    public CustomerTierService(AppDbContext db) { _db = db; }

    public async Task<List<CustomerTierDto>> GetAllAsync()
    {
        var tiers = await _db.CustomerTiers
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .ToListAsync();

        // Conteos para mostrar en la UI
        var clientCounts = await _db.Clients
            .Where(c => c.CustomerTierId != null)
            .GroupBy(c => c.CustomerTierId!.Value)
            .Select(g => new { TierId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TierId, x => x.Count);

        var overrideCounts = await _db.ProductPriceOverrides
            .GroupBy(o => o.CustomerTierId)
            .Select(g => new { TierId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TierId, x => x.Count);

        return tiers.Select(t => ToDto(t,
            clientCounts.GetValueOrDefault(t.Id, 0),
            overrideCounts.GetValueOrDefault(t.Id, 0))).ToList();
    }

    public async Task<CustomerTierDto?> GetByIdAsync(int id)
    {
        var t = await _db.CustomerTiers.FindAsync(id);
        if (t is null) return null;
        var clientCount = await _db.Clients.CountAsync(c => c.CustomerTierId == id);
        var ovrCount = await _db.ProductPriceOverrides.CountAsync(o => o.CustomerTierId == id);
        return ToDto(t, clientCount, ovrCount);
    }

    public async Task<CustomerTierDto> CreateAsync(CreateCustomerTierRequest req)
    {
        var code = NormalizeCode(req.Code);
        if (await _db.CustomerTiers.AnyAsync(x => x.Code == code))
            throw new InvalidOperationException($"Ya existe una lista con el codigo '{code}'.");

        var tier = new CustomerTier
        {
            Name = req.Name.Trim(),
            Code = code,
            AdjustmentPercent = req.AdjustmentPercent,
            IsDefault = req.IsDefault,
            IsActive = true,
            SortOrder = req.SortOrder,
            Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(),
            Companies = NormalizeCompanies(req.Companies),
            CreatedAt = DateTime.UtcNow
        };
        _db.CustomerTiers.Add(tier);

        if (req.IsDefault)
        {
            // Si esta nueva es default, quitar default de las demas
            await UnsetDefaultExceptAsync(0); // 0 -> ninguna existente todavia
        }

        await _db.SaveChangesAsync();

        if (req.IsDefault)
        {
            await UnsetDefaultExceptAsync(tier.Id);
            tier.IsDefault = true;
            await _db.SaveChangesAsync();
        }

        return (await GetByIdAsync(tier.Id))!;
    }

    public async Task<CustomerTierDto?> UpdateAsync(int id, UpdateCustomerTierRequest req)
    {
        var tier = await _db.CustomerTiers.FindAsync(id);
        if (tier is null) return null;

        if (req.Name is not null && !string.IsNullOrWhiteSpace(req.Name)) tier.Name = req.Name.Trim();
        if (req.AdjustmentPercent.HasValue) tier.AdjustmentPercent = req.AdjustmentPercent.Value;
        if (req.SortOrder.HasValue) tier.SortOrder = req.SortOrder.Value;
        if (req.Notes is not null) tier.Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
        if (req.IsActive.HasValue) tier.IsActive = req.IsActive.Value;
        if (req.Companies is not null) tier.Companies = NormalizeCompanies(req.Companies);

        if (req.IsDefault.HasValue && req.IsDefault.Value && !tier.IsDefault)
        {
            await UnsetDefaultExceptAsync(tier.Id);
            tier.IsDefault = true;
        }
        else if (req.IsDefault.HasValue && !req.IsDefault.Value && tier.IsDefault)
        {
            // No permitir quedarnos sin ninguna default. Si quieren cambiar la default,
            // que pasen por marcar otra como default (esa lo desmarca automaticamente).
            throw new InvalidOperationException(
                "No se puede desmarcar la lista por defecto. Marca otra como default y se reemplaza automaticamente.");
        }

        tier.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await GetByIdAsync(tier.Id);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var tier = await _db.CustomerTiers.FindAsync(id);
        if (tier is null) return false;
        if (tier.IsDefault)
            throw new InvalidOperationException("No se puede eliminar la lista por defecto. Marca otra como default primero.");

        // Los clientes que apunten a esta lista quedan en NULL (FK con ON DELETE SET NULL).
        // Los overrides se borran en cascada.
        _db.CustomerTiers.Remove(tier);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Calcula el precio (sin IVA) de un producto para una lista de precios.
    /// Si tierId es null, devuelve el RetailPrice tal cual (sin ajuste).
    /// </summary>
    public async Task<decimal> GetPriceForTierAsync(int productId, int? tierId)
    {
        var product = await _db.Products.FindAsync(productId);
        if (product is null) return 0m;
        if (tierId is null) return product.RetailPrice;

        // 1) Override puntual?
        var ovr = await _db.ProductPriceOverrides
            .Where(o => o.ProductId == productId && o.CustomerTierId == tierId.Value)
            .Select(o => (decimal?)o.Price)
            .FirstOrDefaultAsync();
        if (ovr.HasValue) return ovr.Value;

        // 2) Calculo automatico segun la lista
        var tier = await _db.CustomerTiers.FindAsync(tierId.Value);
        if (tier is null || !tier.IsActive) return product.RetailPrice;

        var rate = tier.AdjustmentPercent / 100m;
        return Math.Round(product.RetailPrice * (1m + rate), 2);
    }

    /// <summary>
    /// Devuelve el precio del producto en TODAS las listas activas, marcando si es override
    /// o calculo automatico. Sirve para mostrar la mini-tabla en el detalle del producto.
    /// </summary>
    public async Task<List<ProductTierPriceDto>> GetPricesForProductAsync(int productId)
    {
        var product = await _db.Products.FindAsync(productId);
        if (product is null) return new();

        var tiers = await _db.CustomerTiers.Where(t => t.IsActive)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync();

        var overrides = await _db.ProductPriceOverrides
            .Where(o => o.ProductId == productId)
            .ToDictionaryAsync(o => o.CustomerTierId);

        var vatRate = (product.VatRate ?? 0m) / 100m;

        return tiers.Select(t =>
        {
            decimal price;
            bool isOverride = false;
            int? ovrId = null;
            string? ovrNotes = null;

            if (overrides.TryGetValue(t.Id, out var ovr))
            {
                price = ovr.Price;
                isOverride = true;
                ovrId = ovr.Id;
                ovrNotes = ovr.Notes;
            }
            else
            {
                var rate = t.AdjustmentPercent / 100m;
                price = Math.Round(product.RetailPrice * (1m + rate), 2);
            }

            var priceWithVat = Math.Round(price * (1m + vatRate), 2);

            return new ProductTierPriceDto(
                t.Id, t.Name, t.Code, t.AdjustmentPercent,
                price, priceWithVat, isOverride, ovrId, ovrNotes);
        }).ToList();
    }

    public async Task<ProductTierPriceDto> SetPriceOverrideAsync(SetProductPriceOverrideRequest req)
    {
        var product = await _db.Products.FindAsync(req.ProductId)
            ?? throw new InvalidOperationException("Producto no encontrado.");
        var tier = await _db.CustomerTiers.FindAsync(req.CustomerTierId)
            ?? throw new InvalidOperationException("Lista de precios no encontrada.");

        var existing = await _db.ProductPriceOverrides
            .FirstOrDefaultAsync(o => o.ProductId == req.ProductId && o.CustomerTierId == req.CustomerTierId);

        if (existing is null)
        {
            existing = new ProductPriceOverride
            {
                ProductId = req.ProductId,
                CustomerTierId = req.CustomerTierId,
                Price = req.Price,
                Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(),
                CreatedAt = DateTime.UtcNow
            };
            _db.ProductPriceOverrides.Add(existing);
        }
        else
        {
            existing.Price = req.Price;
            existing.Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        var vatRate = (product.VatRate ?? 0m) / 100m;
        var priceWithVat = Math.Round(req.Price * (1m + vatRate), 2);

        return new ProductTierPriceDto(
            tier.Id, tier.Name, tier.Code, tier.AdjustmentPercent,
            req.Price, priceWithVat, true, existing.Id, existing.Notes);
    }

    public async Task<bool> DeletePriceOverrideAsync(int productId, int tierId)
    {
        var ovr = await _db.ProductPriceOverrides
            .FirstOrDefaultAsync(o => o.ProductId == productId && o.CustomerTierId == tierId);
        if (ovr is null) return false;
        _db.ProductPriceOverrides.Remove(ovr);
        await _db.SaveChangesAsync();
        return true;
    }

    // === Helpers ===

    private async Task UnsetDefaultExceptAsync(int keepTierId)
    {
        var others = await _db.CustomerTiers.Where(t => t.IsDefault && t.Id != keepTierId).ToListAsync();
        foreach (var t in others) { t.IsDefault = false; t.UpdatedAt = DateTime.UtcNow; }
    }

    private static string NormalizeCode(string code)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(c)) throw new InvalidOperationException("El codigo es obligatorio.");
        return c;
    }

    /// <summary>
    /// Normaliza el CSV de empresas: trim, uppercase, dedupe, vacio -> null.
    /// </summary>
    private static string? NormalizeCompanies(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var items = csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();
        return items.Count == 0 ? null : string.Join(",", items);
    }

    private static CustomerTierDto ToDto(CustomerTier t, int clientCount, int overrideCount) => new(
        t.Id, t.Name, t.Code, t.AdjustmentPercent, t.IsDefault, t.IsActive,
        t.SortOrder, t.Notes, clientCount, overrideCount, t.Companies);
}
