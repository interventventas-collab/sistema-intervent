namespace Api.Models;

public class MeliItem
{
    public int Id { get; set; }
    public string MeliItemId { get; set; } = string.Empty;
    public int MeliAccountId { get; set; }
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
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public int? ProductId { get; set; }
    public Product? Product { get; set; }

    public MeliAccount? MeliAccount { get; set; }
}
