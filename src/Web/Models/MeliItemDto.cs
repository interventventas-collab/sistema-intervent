namespace Web.Models;

public class MeliItemDto
{
    public int Id { get; set; }
    public string MeliItemId { get; set; } = string.Empty;
    public int MeliAccountId { get; set; }
    public string AccountNickname { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? CategoryId { get; set; }
    public string? CategoryPath { get; set; }
    public decimal Price { get; set; }
    public decimal? OriginalPrice { get; set; }
    public string CurrencyId { get; set; } = "ARS";
    public int AvailableQuantity { get; set; }
    public int SoldQuantity { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Condition { get; set; }
    public string? ListingTypeId { get; set; }
    public string? InstallmentTag { get; set; }
    public bool FreeShipping { get; set; }
    public string? Thumbnail { get; set; }
    public string? Permalink { get; set; }
    public string? Sku { get; set; }
    public string? UserProductId { get; set; }
    public string? FamilyId { get; set; }
    public string? FamilyName { get; set; }
    public DateTime? DateCreated { get; set; }
    public DateTime? LastUpdated { get; set; }
    public int? ProductId { get; set; }
    public string? ProductTitle { get; set; }
    public int? ProductCriticalStock { get; set; }
}

public class MeliItemsResponse
{
    public List<MeliItemDto> Items { get; set; } = new();
    public int Total { get; set; }
}

public class MeliItemSyncResult
{
    public int TotalSynced { get; set; }
    public int TotalErrors { get; set; }
    public List<string> Errors { get; set; } = new();
    public string? ProgressId { get; set; }
}

public class UpdateMeliItemRequest
{
    public string? Title { get; set; }
    public decimal? Price { get; set; }
    public int? AvailableQuantity { get; set; }
    public string? Status { get; set; }
}

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
