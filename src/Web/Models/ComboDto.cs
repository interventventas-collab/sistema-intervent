namespace Web.Models;

public class ComboItemDto
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string ProductTitle { get; set; } = string.Empty;
    public string? ProductSku { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
}

public class ComboDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string? Description { get; set; }
    public string? Photo { get; set; }
    public string PriceMode { get; set; } = "auto";
    public decimal? ManualPrice { get; set; }
    public decimal? PercentAdjustment { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public List<ComboItemDto> Items { get; set; } = new();
    public decimal SubtotalProductos { get; set; }
    public decimal FinalPrice { get; set; }
}

public class ComboItemRequest
{
    public int ProductId { get; set; }
    public int Quantity { get; set; } = 1;
}

public class CreateComboRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string? Description { get; set; }
    public string? Photo { get; set; }
    public string PriceMode { get; set; } = "auto";
    public decimal? ManualPrice { get; set; }
    public decimal? PercentAdjustment { get; set; }
    public List<ComboItemRequest> Items { get; set; } = new();
}

public class UpdateComboRequest
{
    public string? Name { get; set; }
    public string? Sku { get; set; }
    public string? Description { get; set; }
    public string? Photo { get; set; }
    public string? PriceMode { get; set; }
    public decimal? ManualPrice { get; set; }
    public decimal? PercentAdjustment { get; set; }
    public bool? IsActive { get; set; }
    public List<ComboItemRequest>? Items { get; set; }
}
