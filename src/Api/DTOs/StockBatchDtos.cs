using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record StockBatchDto(
    int Id,
    int ProductId,
    int Quantity,
    DateTime ExpiryDate,
    int DaysUntilExpiry,   // negativo si ya vencio
    bool IsExpired,
    string? Notes,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record CreateStockBatchRequest(
    [Range(1, int.MaxValue)] int Quantity,
    [Required] DateTime ExpiryDate,
    [MaxLength(500)] string? Notes
);

public record UpdateStockBatchRequest(
    [Range(0, int.MaxValue)] int? Quantity,
    DateTime? ExpiryDate,
    [MaxLength(500)] string? Notes
);
