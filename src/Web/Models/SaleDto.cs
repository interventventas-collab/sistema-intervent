namespace Web.Models;

public class SaleItemDto
{
    public int Id { get; set; }
    public int? ProductId { get; set; }
    public string? Code { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal? VatRate { get; set; }
    public decimal BonifPercent { get; set; }
    public decimal LineTotal { get; set; }
    /// <summary>Precio unit. base sin lista (snapshot al momento de la venta).</summary>
    public decimal BasePrice { get; set; }
    /// <summary>% que aplicó la lista de precios (-50, +15, etc). 0 si no hubo lista.</summary>
    public decimal TierAdjustmentPercent { get; set; }
}

public class SaleDto
{
    public int Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }
    public int? ClientId { get; set; }
    public string? ClientCode { get; set; }
    public string? ClientNameSnapshot { get; set; }
    public string? ClientAddressSnapshot { get; set; }
    public string? ClientDeliveryAddressSnapshot { get; set; }
    public string? ClientCityLocationSnapshot { get; set; }
    public string? ClientCuitSnapshot { get; set; }
    public string? PaymentCondition { get; set; }
    public string? IvaCondition { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Discount { get; set; }
    public decimal Total { get; set; }
    public string? AmountInWords { get; set; }
    public string? Notes { get; set; }
    public bool IsCancelled { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelledByOperator { get; set; }
    public string? WeekDays { get; set; }
    public bool IsPaid { get; set; }
    public string? CompanyNameSnapshot { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public List<SaleItemDto> Items { get; set; } = new();
    public string ComprobanteType { get; set; } = "X"; // 'X' por default; FACTURA_A/B/C a futuro
    public string? VendedorName { get; set; }
}

public class UpdateSaleFlagsRequest
{
    public string? WeekDays { get; set; }
    public bool? IsPaid { get; set; }
}

public class DeleteSaleRequest
{
    public string Password { get; set; } = string.Empty;
}

public class DeleteSaleSettingsDto
{
    public string AllowedOperator { get; set; } = "OSMAR";
    public string Hint { get; set; } = string.Empty;
}

public class CreateSaleItemRequest
{
    public int? ProductId { get; set; }
    public string? Code { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal? VatRate { get; set; }
    public decimal BonifPercent { get; set; }
}

public class CreateSaleRequest
{
    public DateTime? Date { get; set; }
    public DateTime? DueDate { get; set; }
    public int? ClientId { get; set; }
    public string? ClientNameOverride { get; set; }
    public string? PaymentCondition { get; set; }
    public string? IvaCondition { get; set; }
    public decimal Discount { get; set; }
    public string? Notes { get; set; }
    public string? WeekDays { get; set; }
    public bool? IsPaid { get; set; }
    public string? CompanyNameOverride { get; set; }
    public List<CreateSaleItemRequest> Items { get; set; } = new();
    public string? ComprobanteType { get; set; } // null/vacio => 'X'
    public string? VendedorName { get; set; }
}

public class UpdateSaleRequest
{
    public DateTime? Date { get; set; }
    public DateTime? DueDate { get; set; }
    public int? ClientId { get; set; }
    public string? ClientNameOverride { get; set; }
    public string? PaymentCondition { get; set; }
    public string? IvaCondition { get; set; }
    public decimal? Discount { get; set; }
    public string? Notes { get; set; }
    public string? WeekDays { get; set; }
    public bool? IsPaid { get; set; }
    public string? CompanyNameOverride { get; set; }
    public List<CreateSaleItemRequest>? Items { get; set; }
    public string? VendedorName { get; set; }
}

public class RelinkOrphansReportDto
{
    public int LinkedBySku { get; set; }
    public int LinkedByOem { get; set; }
    public int RemainingOrphans { get; set; }
}

public class TopProductByClientDto
{
    public int ProductId { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public decimal RetailPrice { get; set; }
    public decimal Stock { get; set; }
    public string StockUnit { get; set; } = "unidad";
    public int TimesOrdered { get; set; }
    public decimal TotalQuantity { get; set; }
    public DateTime LastPurchase { get; set; }
}

public class CompanyInfoDto
{
    public string Name { get; set; } = string.Empty;
    public string Cuit { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Web { get; set; } = string.Empty;
    public string IvaCondition { get; set; } = string.Empty;
    public string Iibb { get; set; } = string.Empty;
    public string ActivityStart { get; set; } = string.Empty;
    public string PointOfSale { get; set; } = "0001";
}
