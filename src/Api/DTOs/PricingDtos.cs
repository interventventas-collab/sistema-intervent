namespace Api.DTOs;

public record CompanyDto(
    int Id,
    string Code,
    string Name,
    bool CanSell,
    int SortOrder,
    bool IsActive
);

public record ProductCompanyPriceDto(
    int Id,
    int ProductId,
    int CompanyId,
    string CompanyCode,
    string CompanyName,
    decimal RetailPrice,
    DateTime UpdatedAt
);

public record SetProductCompanyPriceRequest(
    int ProductId,
    int CompanyId,
    decimal RetailPrice
);

public record BrandCompanyMarkupDto(
    int Id,
    int BrandId,
    int CompanyId,
    string CompanyCode,
    string CompanyName,
    decimal MarkupPercent,
    string PriceMode,           // "PERCENT" o "PVP"
    DateTime UpdatedAt
);

public record SetBrandCompanyMarkupRequest(
    int BrandId,
    int CompanyId,
    decimal MarkupPercent,
    string? PriceMode           // "PERCENT" (default) o "PVP"
);

/// <summary>
/// Resultado del calculo de precio en cascada (3 niveles).
/// </summary>
public record ResolvedPriceDto(
    decimal RetailPrice,        // Precio sin IVA resuelto
    string Source,              // "product_override" | "brand_markup" | "default"
    int? CompanyId,
    string? CompanyCode
);
