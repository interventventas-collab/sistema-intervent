namespace Web.Models;

public class StockBatchDto
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public DateTime ExpiryDate { get; set; }
    public int DaysUntilExpiry { get; set; }
    public bool IsExpired { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class CreateStockBatchRequest
{
    public int Quantity { get; set; }
    public DateTime ExpiryDate { get; set; }
    public string? Notes { get; set; }
}

public class UpdateStockBatchRequest
{
    public int? Quantity { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string? Notes { get; set; }
}
