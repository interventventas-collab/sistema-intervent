using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record ComboItemDto(
    int Id,
    int ProductId,
    string ProductTitle,
    string? ProductSku,
    int Quantity,
    decimal UnitPrice,        // PVP del producto sin IVA
    decimal LineTotal,         // Quantity * UnitPrice (sin IVA)
    decimal? VatRate,          // IVA del producto (ej: 21)
    decimal UnitPriceWithVat,  // PVP con IVA aplicado
    decimal LineTotalWithVat   // Quantity * UnitPriceWithVat (con IVA)
);

public record ComboDto(
    int Id,
    string Name,
    string? Sku,
    string? Description,
    string? Photo,
    string PriceMode,          // "auto" | "manual" | "percent"
    decimal? ManualPrice,
    decimal? PercentAdjustment,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    List<ComboItemDto> Items,
    decimal SubtotalProductos, // suma de lineas SIN IVA
    decimal FinalPrice,        // precio final SIN IVA, segun PriceMode
    decimal SubtotalProductosWithVat, // suma de lineas CON IVA aplicado
    decimal FinalPriceWithVat        // precio final CON IVA, segun PriceMode
);

public record ComboItemRequest(
    [Required] int ProductId,
    [Range(1, 9999)] int Quantity
);

public record CreateComboRequest(
    [Required][MaxLength(200)] string Name,
    [MaxLength(100)] string? Sku,
    string? Description,
    string? Photo,
    [Required][MaxLength(10)] string PriceMode,
    decimal? ManualPrice,
    decimal? PercentAdjustment,
    [MinLength(1)] List<ComboItemRequest> Items
);

public record UpdateComboRequest(
    [MaxLength(200)] string? Name,
    [MaxLength(100)] string? Sku,
    string? Description,
    string? Photo,
    [MaxLength(10)] string? PriceMode,
    decimal? ManualPrice,
    decimal? PercentAdjustment,
    bool? IsActive,
    List<ComboItemRequest>? Items
);
