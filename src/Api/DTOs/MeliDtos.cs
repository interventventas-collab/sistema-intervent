namespace Api.DTOs;

public record MeliAccountDto(
    int Id,
    long MeliUserId,
    string Nickname,
    string? Email,
    bool TokenValid,
    DateTime CreatedAt
);

public record MeliAuthUrlResponse(string AuthUrl);

public record MeliCallbackRequest(string Code);

public record MeliOrderDto(
    int Id,
    long MeliOrderId,
    int MeliAccountId,
    string AccountNickname,
    string Status,
    DateTime DateCreated,
    DateTime? DateClosed,
    decimal TotalAmount,
    string CurrencyId,
    long BuyerId,
    string BuyerNickname,
    string ItemId,
    string ItemTitle,
    int Quantity,
    decimal UnitPrice,
    decimal? FullUnitPrice,
    long? ShippingId,
    long? PackId,
    string? ItemThumbnailUrl,
    string? ShippingStatus,
    string? ShippingSubstatus
);

public record MeliOrdersResponse(List<MeliOrderDto> Orders, int Total);

public record MeliOrderSyncResult(int TotalSynced, int TotalErrors, List<string> Errors);

// --- Order Detail DTOs (fetched from MeLi API in real-time) ---

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

    // Buyer
    public long BuyerId { get; set; }
    public string BuyerNickname { get; set; } = string.Empty;
    public string? BuyerFirstName { get; set; }
    public string? BuyerLastName { get; set; }

    // Items
    public List<MeliOrderItemDetail> Items { get; set; } = new();

    // Payments
    public List<MeliPaymentDetail> Payments { get; set; } = new();

    // Totals
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

// --- MeliItem DTOs ---

public record MeliItemDto(
    int Id,
    string MeliItemId,
    int MeliAccountId,
    string AccountNickname,
    string Title,
    string? CategoryId,
    string? CategoryPath,
    decimal Price,
    decimal? OriginalPrice,
    string CurrencyId,
    int AvailableQuantity,
    int SoldQuantity,
    string Status,
    string? Condition,
    string? ListingTypeId,
    string? InstallmentTag,
    bool FreeShipping,
    string? Thumbnail,
    string? Permalink,
    string? Sku,
    string? UserProductId,
    string? FamilyId,
    string? FamilyName,
    DateTime? DateCreated,
    DateTime? LastUpdated,
    int? ProductId,
    string? ProductTitle,
    int? ProductCriticalStock
);

public record MeliItemsResponse(List<MeliItemDto> Items, int Total);

public record MeliItemSyncResult(int TotalSynced, int TotalErrors, List<string> Errors);

public record UpdateMeliItemRequest(string? Title, decimal? Price, int? AvailableQuantity, string? Status);

public record LinkItemToProductRequest(int ProductId);

// --- Item Promotion DTOs ---

public class ItemPromotionDto
{
    public string PromotionId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime? StartDate { get; set; }
    public DateTime? FinishDate { get; set; }
    public decimal? MeliPercentage { get; set; }
    public decimal? SellerPercentage { get; set; }
    public decimal? PromotionPrice { get; set; }
    public decimal? OriginalPrice { get; set; }
}

// --- Cost Simulator DTOs ---

public class ListingCostDto
{
    public decimal Price { get; set; }
    public string CurrencyId { get; set; } = "ARS";
    public string? ListingTypeId { get; set; }
    public string? ListingTypeName { get; set; }
    public decimal SaleFeeAmount { get; set; }
    public decimal ListingFeeAmount { get; set; }
    public decimal FixedFee { get; set; }
    public decimal PercentageFee { get; set; }
    public decimal FinancingFee { get; set; }
    public decimal ShippingCost { get; set; }
    public decimal TaxesEstimated { get; set; }
    public decimal NetAmount { get; set; }
}

public record MeliItemDetailsDto(
    List<string> Pictures,
    string? Description
);

// --- Publish DTOs ---

public record PredictCategoryRequest(string Title);

public class CategoryPredictionDto
{
    public string CategoryId { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public string CategoryPath { get; set; } = "";
    public double Probability { get; set; }
}

public class CategoryAttributeDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ValueType { get; set; } = "";
    public bool Required { get; set; }
    public List<AttributeValueOption> Values { get; set; } = new();
    public string? DefaultValue { get; set; }
    public string? SuggestedValue { get; set; }
}

public class AttributeValueOption
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public record SuggestAttributesRequest(
    string Title,
    string? Description,
    string? Brand,
    string? Model,
    string CategoryId,
    string CategoryName,
    List<CategoryAttributeDto> Attributes
);

public class SuggestedAttributeDto
{
    public string AttributeId { get; set; } = "";
    public string? ValueId { get; set; }
    public string? ValueName { get; set; }
}

public class PublishItemRequest
{
    public int ProductId { get; set; }
    public int MeliAccountId { get; set; }
    public string CategoryId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public int AvailableQuantity { get; set; }
    public string Condition { get; set; } = "new";
    public string ListingTypeId { get; set; } = "gold_special";
    public bool FreeShipping { get; set; } = true;
    public List<PublishAttributeDto> Attributes { get; set; } = new();
    public List<string> PictureUrls { get; set; } = new();
    public string? FamilyName { get; set; }
    // Internal: prevents infinite retry loops
    internal bool AiRetried { get; set; }
    internal bool TitleTruncated { get; set; }
    internal bool ValuesStripped { get; set; }
    internal bool GtinBypassed { get; set; }
    internal string? OriginalBrand { get; set; }
}

public class PublishAttributeDto
{
    public string Id { get; set; } = "";
    public string? ValueId { get; set; }
    public string? ValueName { get; set; }
}

public class PublishItemResponse
{
    public bool Success { get; set; }
    public string? MeliItemId { get; set; }
    public string? Permalink { get; set; }
    public string? Error { get; set; }
}
public class BulkCreateProductsRequest
{
    public List<int> Ids { get; set; } = new();
}

public class BulkCreateProductResult
{
    public int Created { get; set; }
    public int Skipped { get; set; }
    public List<string> SkippedMessages { get; set; } = new();
}

public class BulkPublishRequest
{
    public List<int> ProductIds { get; set; } = new();
    public int MeliAccountId { get; set; }
    public string ListingTypeId { get; set; } = "gold_special";
    public string PriceMode { get; set; } = "markup"; // "markup" o "pvp"
    public decimal MarkupPercent { get; set; }
    public string Condition { get; set; } = "new";
    public bool FreeShipping { get; set; }
}

public class BulkPublishResponse
{
    public int Total { get; set; }
    public int Successful { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public List<BulkPublishItemResult> Results { get; set; } = new();
}

public class BulkPublishItemResult
{
    public int ProductId { get; set; }
    public string ProductTitle { get; set; } = "";
    public bool Success { get; set; }
    public bool Skipped { get; set; }
    public string? SkipReason { get; set; }
    public string? MeliItemId { get; set; }
    public string? Permalink { get; set; }
    public string? Error { get; set; }
}
