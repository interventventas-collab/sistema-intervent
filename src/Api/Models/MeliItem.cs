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
    // Cuando una publicacion tiene variantes, hay una fila por variante. Null si la
    // publicacion no tiene variantes (item simple).
    public string? VariationId { get; set; }
    public string? VariationAttributes { get; set; }
    public DateTime? DateCreated { get; set; }
    public DateTime? LastUpdated { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public int? ProductId { get; set; }
    public Product? Product { get; set; }

    // Una publicacion puede vincularse a un Producto O a un Combo (mutuamente excluyente).
    public int? ComboId { get; set; }
    public Combo? Combo { get; set; }

    // Vinculo nuevo al modulo Cafe (sistema actual). Coexiste con ProductId/ComboId del sistema legacy.
    public int? CafeProductoId { get; set; }
    public CafeProducto? CafeProducto { get; set; }

    /// <summary>Formato del cafe que representa esta publicacion: 1KG | MEDIO | CUARTO. Null = no aplica.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(10)]
    public string? CafeFormato { get; set; }

    /// <summary>Tipo de logistica MeLi: fulfillment (Full) | drop_off | cross_docking | xd_drop_off | not_specified. Null = todavia no consultado.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(30)]
    public string? LogisticType { get; set; }

    public int? CafeComboId { get; set; }   // Promo de cafe fraccionado (Cafe_Combos)
    public int? CafeKitId { get; set; }     // Kit compuesto / BOM (Cafe_Kits)
    public CafeKit? CafeKit { get; set; }

    public MeliAccount? MeliAccount { get; set; }
}
