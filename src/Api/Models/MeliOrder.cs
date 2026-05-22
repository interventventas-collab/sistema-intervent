namespace Api.Models;

public class MeliOrder
{
    public int Id { get; set; }
    public long MeliOrderId { get; set; }
    public int MeliAccountId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime DateCreated { get; set; }
    public DateTime? DateClosed { get; set; }
    public decimal TotalAmount { get; set; }
    public string CurrencyId { get; set; } = string.Empty;
    public long BuyerId { get; set; }
    public string BuyerNickname { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    /// <summary>VariationId del item vendido (cuando la publicacion es multi-variante).
    /// Null = item sin variantes. Necesario para descontar la variante correcta (no todas).
    /// Agregado 2026-05-22.</summary>
    public string? VariationId { get; set; }
    public string ItemTitle { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal? FullUnitPrice { get; set; }
    public long? ShippingId { get; set; }
    public long? PackId { get; set; }
    public string? ShippingStatus { get; set; }
    public string? ShippingSubstatus { get; set; }
    /// <summary>Modo de envio segun MeLi: me1, me2, custom, not_specified. Util para marcar ordenes ME1 en la grilla.</summary>
    public string? ShippingMode { get; set; }
    public bool StockDiscounted { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public MeliAccount? MeliAccount { get; set; }
}
