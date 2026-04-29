namespace Web.Models;

public class CustomerTierDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public decimal AdjustmentPercent { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
    public string? Notes { get; set; }
    public int ClientCount { get; set; }
    public int OverrideCount { get; set; }
    public string? Companies { get; set; } // CSV. null/vacio = todas.
}

public class CreateCustomerTierRequest
{
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public decimal AdjustmentPercent { get; set; }
    public bool IsDefault { get; set; }
    public int SortOrder { get; set; }
    public string? Notes { get; set; }
    public string? Companies { get; set; }
}

public class UpdateCustomerTierRequest
{
    public string? Name { get; set; }
    public decimal? AdjustmentPercent { get; set; }
    public bool? IsDefault { get; set; }
    public bool? IsActive { get; set; }
    public int? SortOrder { get; set; }
    public string? Notes { get; set; }
    public string? Companies { get; set; }
}

public class ProductTierPriceDto
{
    public int CustomerTierId { get; set; }
    public string TierName { get; set; } = string.Empty;
    public string TierCode { get; set; } = string.Empty;
    public decimal AdjustmentPercent { get; set; }
    public decimal Price { get; set; }
    public decimal PriceWithVat { get; set; }
    public bool Override { get; set; }
    public int? OverrideId { get; set; }
    public string? OverrideNotes { get; set; }
}

public class SetProductPriceOverrideRequest
{
    public int ProductId { get; set; }
    public int CustomerTierId { get; set; }
    public decimal Price { get; set; }
    public string? Notes { get; set; }
}
