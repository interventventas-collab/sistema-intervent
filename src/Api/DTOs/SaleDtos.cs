using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record SaleItemDto(
    int Id,
    int? ProductId,
    string? Code,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal? VatRate,
    decimal BonifPercent,
    decimal LineTotal,
    decimal BasePrice,                  // Precio unit. sin lista (snapshot al momento de la venta)
    decimal TierAdjustmentPercent       // % aplicado por la lista (-50, +15, etc). 0 si no hubo lista.
);

public record SaleDto(
    int Id,
    string Number,
    DateTime Date,
    DateTime? DueDate,
    DateTime? PeriodFrom,
    DateTime? PeriodTo,
    int? ClientId,
    string? ClientCode,
    string? ClientNameSnapshot,
    string? ClientAddressSnapshot,
    string? ClientCityLocationSnapshot,
    string? ClientCuitSnapshot,
    string? PaymentCondition,
    string? IvaCondition,
    decimal Subtotal,
    decimal Discount,
    decimal Total,
    string? AmountInWords,
    string? Notes,
    bool IsCancelled,
    DateTime? CancelledAt,
    string? CancelledByOperator,
    string? WeekDays,
    bool IsPaid,
    string? CompanyNameSnapshot,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    List<SaleItemDto> Items,
    string ComprobanteType,    // 'X' por default; 'FACTURA_A'/'FACTURA_B'/'FACTURA_C' a futuro
    string? VendedorName       // Snapshot del usuario que emitio
);

public record UpdateSaleFlagsRequest(
    string? WeekDays,
    bool? IsPaid
);

// Editar un comprobante existente (cambia datos + items + recalcula totales).
public record UpdateSaleRequest(
    DateTime? Date,
    DateTime? DueDate,
    int? ClientId,
    [MaxLength(200)] string? ClientNameOverride,
    [MaxLength(50)] string? PaymentCondition,
    [MaxLength(50)] string? IvaCondition,
    decimal? Discount,
    string? Notes,
    [MaxLength(40)] string? WeekDays,
    bool? IsPaid,
    [MaxLength(100)] string? CompanyNameOverride,
    [MinLength(1)] List<CreateSaleItemRequest>? Items,
    [MaxLength(150)] string? VendedorName
);

public record DeleteSaleRequest(string Password);

public record DeleteSaleSettingsDto(string AllowedOperator, string Hint);

public record CreateSaleItemRequest(
    int? ProductId,
    [MaxLength(100)] string? Code,
    [Required][MaxLength(500)] string Description,
    [Range(0.01, double.MaxValue)] decimal Quantity,
    [Range(0, double.MaxValue)] decimal UnitPrice,
    decimal? VatRate,
    decimal BonifPercent
);

public record CreateSaleRequest(
    DateTime? Date,
    DateTime? DueDate,
    int? ClientId,
    [MaxLength(200)] string? ClientNameOverride,
    [MaxLength(50)] string? PaymentCondition,
    [MaxLength(50)] string? IvaCondition,
    decimal Discount,
    string? Notes,
    [MaxLength(40)] string? WeekDays,
    bool? IsPaid,
    [MaxLength(100)] string? CompanyNameOverride,
    [MinLength(1)] List<CreateSaleItemRequest> Items,
    [MaxLength(20)] string? ComprobanteType,    // null/vacio => 'X' por default
    [MaxLength(150)] string? VendedorName       // null => null
);

// Top-N productos mas comprados por un cliente.
public record TopProductByClientDto(
    int ProductId,
    string Sku,
    string Title,
    decimal RetailPrice,
    decimal Stock,
    string StockUnit,
    int TimesOrdered,
    decimal TotalQuantity,
    DateTime LastPurchase
);

// Datos de la empresa que aparecen en el comprobante.
public record CompanyInfoDto(
    string Name,
    string Cuit,
    string Address,
    string Phone,
    string Email,
    string Web,
    string IvaCondition,
    string Iibb,
    string ActivityStart,
    string PointOfSale
);
