using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Resolutor de precios por empresa. Implementa la cascada de 3 niveles:
///   1. ProductCompanyPrices (override por producto+empresa) — gana siempre
///   2. BrandCompanyMarkups (markup % por marca+empresa) — calcula cost*(1+%)
///   3. Product.RetailPrice (default) — fallback
/// </summary>
public class PricingService
{
    private readonly AppDbContext _db;

    public PricingService(AppDbContext db) { _db = db; }

    public async Task<List<CompanyDto>> GetCompaniesAsync()
        => await _db.Companies
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new CompanyDto(c.Id, c.Code, c.Name, c.CanSell, c.SortOrder, c.IsActive))
            .ToListAsync();

    public async Task<Company?> GetCompanyByCodeAsync(string code)
        => await _db.Companies.FirstOrDefaultAsync(c => c.Code.ToUpper() == code.ToUpper());

    // ===== ProductCompanyPrices =====

    public async Task<List<ProductCompanyPriceDto>> GetProductPricesAsync(int productId)
        => await _db.ProductCompanyPrices
            .Include(p => p.Company)
            .Where(p => p.ProductId == productId)
            .Select(p => new ProductCompanyPriceDto(
                p.Id, p.ProductId, p.CompanyId,
                p.Company!.Code, p.Company.Name,
                p.RetailPrice, p.UpdatedAt))
            .ToListAsync();

    public async Task<ProductCompanyPriceDto?> SetProductPriceAsync(SetProductCompanyPriceRequest req)
    {
        var existing = await _db.ProductCompanyPrices
            .FirstOrDefaultAsync(p => p.ProductId == req.ProductId && p.CompanyId == req.CompanyId);

        if (existing is null)
        {
            existing = new ProductCompanyPrice
            {
                ProductId = req.ProductId,
                CompanyId = req.CompanyId,
                RetailPrice = req.RetailPrice,
                UpdatedAt = DateTime.UtcNow
            };
            _db.ProductCompanyPrices.Add(existing);
        }
        else
        {
            existing.RetailPrice = req.RetailPrice;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return (await _db.ProductCompanyPrices
            .Include(p => p.Company)
            .Where(p => p.Id == existing.Id)
            .Select(p => new ProductCompanyPriceDto(
                p.Id, p.ProductId, p.CompanyId,
                p.Company!.Code, p.Company.Name,
                p.RetailPrice, p.UpdatedAt))
            .FirstOrDefaultAsync());
    }

    public async Task<bool> DeleteProductPriceAsync(int productId, int companyId)
    {
        var row = await _db.ProductCompanyPrices
            .FirstOrDefaultAsync(p => p.ProductId == productId && p.CompanyId == companyId);
        if (row is null) return false;
        _db.ProductCompanyPrices.Remove(row);
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== BrandCompanyMarkups =====

    public async Task<List<BrandCompanyMarkupDto>> GetBrandMarkupsAsync(int brandId)
        => await _db.BrandCompanyMarkups
            .Include(b => b.Company)
            .Where(b => b.BrandId == brandId)
            .Select(b => new BrandCompanyMarkupDto(
                b.Id, b.BrandId, b.CompanyId,
                b.Company!.Code, b.Company.Name,
                b.MarkupPercent, b.PriceMode, b.UpdatedAt))
            .ToListAsync();

    public async Task<BrandCompanyMarkupDto?> SetBrandMarkupAsync(SetBrandCompanyMarkupRequest req)
    {
        var mode = string.IsNullOrWhiteSpace(req.PriceMode)
            ? "PERCENT"
            : req.PriceMode.Trim().ToUpperInvariant();
        if (mode != "PERCENT" && mode != "PVP") mode = "PERCENT";

        var existing = await _db.BrandCompanyMarkups
            .FirstOrDefaultAsync(b => b.BrandId == req.BrandId && b.CompanyId == req.CompanyId);

        if (existing is null)
        {
            existing = new BrandCompanyMarkup
            {
                BrandId = req.BrandId,
                CompanyId = req.CompanyId,
                MarkupPercent = req.MarkupPercent,
                PriceMode = mode,
                UpdatedAt = DateTime.UtcNow
            };
            _db.BrandCompanyMarkups.Add(existing);
        }
        else
        {
            existing.MarkupPercent = req.MarkupPercent;
            existing.PriceMode = mode;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return (await _db.BrandCompanyMarkups
            .Include(b => b.Company)
            .Where(b => b.Id == existing.Id)
            .Select(b => new BrandCompanyMarkupDto(
                b.Id, b.BrandId, b.CompanyId,
                b.Company!.Code, b.Company.Name,
                b.MarkupPercent, b.PriceMode, b.UpdatedAt))
            .FirstOrDefaultAsync());
    }

    public async Task<bool> DeleteBrandMarkupAsync(int brandId, int companyId)
    {
        var row = await _db.BrandCompanyMarkups
            .FirstOrDefaultAsync(b => b.BrandId == brandId && b.CompanyId == companyId);
        if (row is null) return false;
        _db.BrandCompanyMarkups.Remove(row);
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Resolver de precios (cascada de 3 niveles) =====

    public async Task<ResolvedPriceDto> ResolvePriceAsync(int productId, int? companyId)
    {
        var product = await _db.Products.FindAsync(productId);
        if (product is null) return new ResolvedPriceDto(0, "default", null, null);

        // Si no se especifica empresa, usar el default del producto.
        if (!companyId.HasValue)
        {
            return new ResolvedPriceDto(product.RetailPrice, "default", null, null);
        }

        var company = await _db.Companies.FindAsync(companyId.Value);
        if (company is null)
        {
            return new ResolvedPriceDto(product.RetailPrice, "default", null, null);
        }

        // Nivel 1: override producto+empresa
        var productOverride = await _db.ProductCompanyPrices
            .FirstOrDefaultAsync(p => p.ProductId == productId && p.CompanyId == companyId.Value);
        if (productOverride is not null)
        {
            return new ResolvedPriceDto(productOverride.RetailPrice, "product_override", company.Id, company.Code);
        }

        // Nivel 2: regla por marca+empresa — elige slot (PVP1, PVP2, PVP3)
        if (product.BrandId.HasValue)
        {
            var brandRule = await _db.BrandCompanyMarkups
                .FirstOrDefaultAsync(b => b.BrandId == product.BrandId.Value && b.CompanyId == companyId.Value);
            if (brandRule is not null)
            {
                var mode = (brandRule.PriceMode ?? "PVP1").Trim().ToUpperInvariant();
                if (mode == "PVP2")
                {
                    // PVP 2: precio alternativo cargado en el producto. Si esta vacio, fallback a PVP1.
                    if (product.RetailPrice2.HasValue && product.RetailPrice2.Value > 0)
                        return new ResolvedPriceDto(product.RetailPrice2.Value, "pvp2", company.Id, company.Code);
                    return new ResolvedPriceDto(product.RetailPrice, "pvp2_fallback_pvp1", company.Id, company.Code);
                }
                if (mode == "PVP3")
                {
                    // PVP 3: cost × (1 + Pvp3MarkupPercent / 100). Si falta cost o %, fallback a PVP1.
                    if (product.CostPrice > 0 && product.Pvp3MarkupPercent.HasValue)
                    {
                        var calc = Math.Round(product.CostPrice * (1m + product.Pvp3MarkupPercent.Value / 100m), 2, MidpointRounding.AwayFromZero);
                        return new ResolvedPriceDto(calc, "pvp3", company.Id, company.Code);
                    }
                    return new ResolvedPriceDto(product.RetailPrice, "pvp3_fallback_pvp1", company.Id, company.Code);
                }
                // mode == "PVP1" o cualquier otro: usar PVP 1
                return new ResolvedPriceDto(product.RetailPrice, "pvp1", company.Id, company.Code);
            }
        }

        // Sin regla: default a PVP 1
        return new ResolvedPriceDto(product.RetailPrice, "default", company.Id, company.Code);
    }
}
