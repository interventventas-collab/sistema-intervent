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

    /// <summary>Ratio del precio publicado en MeLi sobre el precio NETO+IVA del sistema.
    /// Captura: cuotas absorbidas + envio gratis + comision MeLi + margen del vendedor.
    /// Se usa al pushear precios: precio_meli = precio_neto_sistema × 1.21 × ratio.
    /// Capturado por el script de inicializacion 2026-05-21.</summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "decimal(8,4)")]
    public decimal? PriceRatioOverIva { get; set; }
    public DateTime? PriceRatioCapturedAt { get; set; }

    public int? CafeComboId { get; set; }   // Promo de cafe fraccionado (Cafe_Combos)
    public int? CafeKitId { get; set; }     // Kit compuesto / BOM (Cafe_Kits)
    public CafeKit? CafeKit { get; set; }

    /// <summary>2026-05-29: ajuste opcional para el push de precio (Contabilium-style).
    /// Si está cargado, se aplica al precio base: precio_final = base * (1+Pct/100) + Pesos -> redondear.
    /// Persiste por dispositivo (se ve desde cualquier compu del usuario).</summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "decimal(18,4)")]
    public decimal? AjustePctOverride { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "decimal(18,2)")]
    public decimal? AjustePesosOverride { get; set; }
    /// <summary>"" / "99" / "999" / "000" — terminación al redondear hacia arriba.</summary>
    public string? AjusteRedondeoOverride { get; set; }

    public MeliAccount? MeliAccount { get; set; }
}
