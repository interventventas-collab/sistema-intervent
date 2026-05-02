using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Sales")]
public class Sale
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string Number { get; set; } = string.Empty;

    public DateTime Date { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }

    public int? ClientId { get; set; }
    [ForeignKey(nameof(ClientId))]
    public Client? Client { get; set; }

    [MaxLength(200)]
    public string? ClientNameSnapshot { get; set; }

    [MaxLength(500)]
    public string? ClientAddressSnapshot { get; set; }

    /// <summary>Domicilio de entrega snapshotado al momento de emitir el comprobante.</summary>
    [MaxLength(500)]
    public string? ClientDeliveryAddressSnapshot { get; set; }

    /// <summary>FK explicito a la empresa que emitio el comprobante. Reemplaza el rol del CompanyNameSnapshot.</summary>
    public int? CompanyId { get; set; }
    [ForeignKey(nameof(CompanyId))]
    public Company? Company { get; set; }

    [MaxLength(200)]
    public string? ClientCityLocationSnapshot { get; set; }

    [MaxLength(20)]
    public string? ClientCuitSnapshot { get; set; }

    [MaxLength(50)]
    public string? PaymentCondition { get; set; }

    [MaxLength(50)]
    public string? IvaCondition { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Subtotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Discount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Total { get; set; }

    [MaxLength(500)]
    public string? AmountInWords { get; set; }

    public string? Notes { get; set; }

    public bool IsCancelled { get; set; }
    public DateTime? CancelledAt { get; set; }

    [MaxLength(50)]
    public string? CancelledByOperator { get; set; }

    // Lista de dias visibles abajo del comprobante: "LUN,MIE,VIE" (CSV).
    [MaxLength(40)]
    public string? WeekDays { get; set; }

    public bool IsPaid { get; set; }

    // Indica si el stock de los items ya fue descontado del inventario.
    // Se setea en true al crear la venta y se vuelve a false al anular,
    // para evitar descontar dos veces ante reintentos o ediciones.
    public bool StockDiscounted { get; set; }

    /// <summary>
    /// Tipo de comprobante: 'X' (cotizacion / remito interno, no fiscal, sin IVA) por default.
    /// A futuro: 'FACTURA_A', 'FACTURA_B', 'FACTURA_C' cuando se enlace ARCA.
    /// Cada tipo lleva su propio Punto de Venta y numeracion independiente.
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string ComprobanteType { get; set; } = "X";

    /// <summary>Nombre del vendedor que emitio el comprobante (snapshot del usuario logueado).</summary>
    [MaxLength(150)]
    public string? VendedorName { get; set; }

    // Snapshot del nombre/marca de la empresa que aparece en el comprobante.
    // Si es null, se usa el valor actual de AppSettings("company.name").
    [MaxLength(100)]
    public string? CompanyNameSnapshot { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<SaleItem> Items { get; set; } = new List<SaleItem>();
}

[Table("SaleItems")]
public class SaleItem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int SaleId { get; set; }
    [ForeignKey(nameof(SaleId))]
    public Sale? Sale { get; set; }

    public int? ProductId { get; set; }
    [ForeignKey(nameof(ProductId))]
    public Product? Product { get; set; }

    [MaxLength(100)]
    public string? Code { get; set; }

    [Required]
    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Quantity { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal UnitPrice { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? VatRate { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal BonifPercent { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal LineTotal { get; set; }

    /// <summary>
    /// Precio unitario base (sin lista de precios aplicada) snapshotado al momento de la venta.
    /// Sirve para mostrar el descuento original en el comprobante aunque el precio del producto
    /// cambie despues. Si no hubo lista, BasePrice == UnitPrice y TierAdjustmentPercent == 0.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal BasePrice { get; set; }

    /// <summary>Porcentaje de ajuste aplicado por la lista de precios al momento de la venta (ej -50.00).</summary>
    [Column(TypeName = "decimal(6,2)")]
    public decimal TierAdjustmentPercent { get; set; }
}
