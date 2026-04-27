namespace Web.Models;

public class MeliOrderDto
{
    public int Id { get; set; }
    public long MeliOrderId { get; set; }
    public int MeliAccountId { get; set; }
    public string AccountNickname { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime DateCreated { get; set; }
    public DateTime? DateClosed { get; set; }
    public decimal TotalAmount { get; set; }
    public string CurrencyId { get; set; } = string.Empty;
    public long BuyerId { get; set; }
    public string BuyerNickname { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string ItemTitle { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal? FullUnitPrice { get; set; }
    public long? ShippingId { get; set; }
    public long? PackId { get; set; }
    public string? ItemThumbnailUrl { get; set; }
    public string? ShippingStatus { get; set; }
    public string? ShippingSubstatus { get; set; }
}

public class MeliOrdersResponse
{
    public List<MeliOrderDto> Orders { get; set; } = new();
    public int Total { get; set; }
}

public class MeliOrderSyncResult
{
    public int TotalSynced { get; set; }
    public int TotalErrors { get; set; }
    public List<string> Errors { get; set; } = new();
}

// --- Order Detail Models ---

public class MeliOrderDetailResponse
{
    public List<MeliOrderDetailDto> Orders { get; set; } = new();
}

public class MeliOrderDetailDto
{
    public long MeliOrderId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime DateCreated { get; set; }
    public DateTime? DateClosed { get; set; }
    public decimal TotalAmount { get; set; }
    public string CurrencyId { get; set; } = "ARS";
    public long? PackId { get; set; }

    public long BuyerId { get; set; }
    public string BuyerNickname { get; set; } = string.Empty;
    public string? BuyerFirstName { get; set; }
    public string? BuyerLastName { get; set; }

    public List<MeliOrderItemDetail> Items { get; set; } = new();
    public List<MeliPaymentDetail> Payments { get; set; } = new();

    public decimal? ShippingCost { get; set; }
    public decimal? TotalSaleFee { get; set; }
    public decimal? TaxesAmount { get; set; }
}

public class MeliOrderItemDetail
{
    public string ItemId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal? SaleFee { get; set; }
}

public class MeliPaymentDetail
{
    public long PaymentId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? PaymentType { get; set; }
    public string? PaymentMethodId { get; set; }
    public decimal TransactionAmount { get; set; }
    public decimal TotalPaidAmount { get; set; }
    public decimal? ShippingCost { get; set; }
    public decimal? TaxesAmount { get; set; }
    public DateTime? DateApproved { get; set; }
    public int? Installments { get; set; }
}
