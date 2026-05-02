namespace Web.Models;

public class CompanyDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool CanSell { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

public class ProductCompanyPriceDto
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public int CompanyId { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public decimal RetailPrice { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class SetProductCompanyPriceRequest
{
    public int ProductId { get; set; }
    public int CompanyId { get; set; }
    public decimal RetailPrice { get; set; }
}

public class BrandCompanyMarkupDto
{
    public int Id { get; set; }
    public int BrandId { get; set; }
    public int CompanyId { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public decimal MarkupPercent { get; set; }
    public string PriceMode { get; set; } = "PERCENT"; // "PERCENT" | "PVP"
    public DateTime UpdatedAt { get; set; }
}

public class SetBrandCompanyMarkupRequest
{
    public int BrandId { get; set; }
    public int CompanyId { get; set; }
    public decimal MarkupPercent { get; set; }
    public string? PriceMode { get; set; } // "PERCENT" o "PVP". Default: PERCENT
}

public class ResolvedPriceDto
{
    public decimal RetailPrice { get; set; }
    public string Source { get; set; } = "default"; // "product_override" | "brand_markup" | "default"
    public int? CompanyId { get; set; }
    public string? CompanyCode { get; set; }
}
