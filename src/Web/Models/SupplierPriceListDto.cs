namespace Web.Models;

public class SupplierPriceListDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? SupplierId { get; set; }
    public string? SupplierName { get; set; }
    public string? Notes { get; set; }
    public DateTime? LastUploadAt { get; set; }
    public int ItemsCount { get; set; }
    public int LinkedProductsCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class SupplierPriceListItemDto
{
    public int Id { get; set; }
    public int PriceListId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal CostPrice { get; set; }
    public decimal? SuggestedRetailPrice { get; set; }
    public string? Notes { get; set; }
    public int LinkedProductsCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class CreateSupplierPriceListRequest
{
    public string Name { get; set; } = string.Empty;
    public int? SupplierId { get; set; }
    public string? Notes { get; set; }
}

public class UpdateSupplierPriceListRequest
{
    public string? Name { get; set; }
    public int? SupplierId { get; set; }
    public string? Notes { get; set; }
}

public class CreatePriceListItemRequest
{
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal CostPrice { get; set; }
    public decimal? SuggestedRetailPrice { get; set; }
    public string? Notes { get; set; }
}

public class UpdatePriceListItemRequest
{
    public string? Code { get; set; }
    public string? Description { get; set; }
    public decimal? CostPrice { get; set; }
    public decimal? SuggestedRetailPrice { get; set; }
    public string? Notes { get; set; }
}

public class PriceListImportResult
{
    public int TotalRows { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int LinkedProductsUpdated { get; set; }
    public List<string> Errors { get; set; } = new();
}
