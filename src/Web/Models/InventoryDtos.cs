namespace Web.Models;

public class WarehouseDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
}

public class StockMovementDto
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string? ProductSku { get; set; }
    public string ProductTitle { get; set; } = string.Empty;
    public int WarehouseId { get; set; }
    public string WarehouseName { get; set; } = string.Empty;
    public string MovementType { get; set; } = "ajuste";
    public decimal DeltaQuantity { get; set; }
    public decimal StockBefore { get; set; }
    public decimal StockAfter { get; set; }
    public string? Reason { get; set; }
    public string? Notes { get; set; }
    public string? OperatorName { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AdjustStockRequest
{
    public int ProductId { get; set; }
    public int WarehouseId { get; set; }
    public string MovementType { get; set; } = "ajuste";
    public decimal Quantity { get; set; }
    public string? Reason { get; set; }
    public string? Notes { get; set; }
    public string? OperatorName { get; set; }
}
