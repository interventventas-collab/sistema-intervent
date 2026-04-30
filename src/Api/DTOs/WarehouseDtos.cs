using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record WarehouseDto(
    int Id,
    string Code,
    string Name,
    string? Address,
    string? Notes,
    bool IsDefault,
    bool IsActive,
    int SortOrder
);

public record StockMovementDto(
    int Id,
    int ProductId,
    string? ProductSku,
    string ProductTitle,
    int WarehouseId,
    string WarehouseName,
    string MovementType,
    int DeltaQuantity,
    int StockBefore,
    int StockAfter,
    string? Reason,
    string? Notes,
    string? OperatorName,
    DateTime CreatedAt
);

/// <summary>Request para registrar un ajuste de stock manual.</summary>
public record AdjustStockRequest(
    [Required] int ProductId,
    [Required] int WarehouseId,
    /// <summary>Tipo: 'ingreso' | 'egreso' | 'ajuste' | 'rotura' | 'merma' | 'devolucion' | 'conteo' | 'otro'</summary>
    [Required][MaxLength(30)] string MovementType,
    /// <summary>
    /// Si MovementType es 'ajuste' o 'conteo': el valor absoluto al que dejar el stock.
    /// Si es 'ingreso' / 'egreso' / etc: la cantidad a sumar/restar (siempre positiva).
    /// </summary>
    [Range(0, int.MaxValue)] int Quantity,
    [MaxLength(150)] string? Reason,
    [MaxLength(500)] string? Notes,
    [MaxLength(100)] string? OperatorName
);
