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
    decimal LineTotal
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
    string? WeekDays,
    bool IsPaid,
    string? CompanyNameSnapshot,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    List<SaleItemDto> Items
);

public record UpdateSaleFlagsRequest(
    string? WeekDays,
    bool? IsPaid
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
    [MinLength(1)] List<CreateSaleItemRequest> Items
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
