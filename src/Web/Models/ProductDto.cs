namespace Web.Models;

public class ProductDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? Sku { get; set; }
    public string? Photo1 { get; set; }
    public string? Photo2 { get; set; }
    public string? Photo3 { get; set; }
    public decimal CostPrice { get; set; }
    public decimal RetailPrice { get; set; }
    public int Stock { get; set; }
    public int CriticalStock { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public List<ProductAccountLinkDto> LinkedAccounts { get; set; } = new();
    public int? BaseProductId { get; set; }
    public string? BaseProductSku { get; set; }
    public string? BaseProductTitle { get; set; }
    public int DerivedCount { get; set; }
    public int? BrandId { get; set; }
    public string? BrandName { get; set; }
}

public class CreateProductRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? Sku { get; set; }
    public string? Photo1 { get; set; }
    public string? Photo2 { get; set; }
    public string? Photo3 { get; set; }
    public decimal CostPrice { get; set; }
    public decimal RetailPrice { get; set; }
    public int Stock { get; set; }
    public int CriticalStock { get; set; }
    public int? BaseProductId { get; set; }
    public int? BrandId { get; set; }
}

public class UpdateProductRequest
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? Sku { get; set; }
    public string? Photo1 { get; set; }
    public string? Photo2 { get; set; }
    public string? Photo3 { get; set; }
    public decimal? CostPrice { get; set; }
    public decimal? RetailPrice { get; set; }
    public int? Stock { get; set; }
    public int? CriticalStock { get; set; }
    public bool? IsActive { get; set; }
    public int? BaseProductId { get; set; }
    public bool? ClearBaseProduct { get; set; }
    public int? BrandId { get; set; }
    public bool? ClearBrand { get; set; }
}

public class ProductAccountLinkDto
{
    public string Nickname { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class BulkCreateProductResult
{
    public int Created { get; set; }
    public int Skipped { get; set; }
    public List<string> SkippedMessages { get; set; } = new();
}
