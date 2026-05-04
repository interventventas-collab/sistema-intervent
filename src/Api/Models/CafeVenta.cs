using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_Ventas")]
public class CafeVenta
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(20)]
    public string Numero { get; set; } = string.Empty;

    public DateTime Fecha { get; set; }

    public int? ClienteId { get; set; }

    [ForeignKey(nameof(ClienteId))]
    public CafeCliente? ClienteNav { get; set; }

    [MaxLength(200)]
    public string? ClienteNombreSnapshot { get; set; }

    [MaxLength(20)]
    public string? ClienteTipoSnapshot { get; set; } // BAR | OTRO

    [MaxLength(50)]
    public string? ClienteTelefonoSnapshot { get; set; }

    [Column(TypeName = "decimal(18,2)")] public decimal Subtotal { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Descuento { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Total { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal CostoTotal { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Margen { get; set; }

    [MaxLength(500)]
    public string? Observaciones { get; set; }

    [MaxLength(20)]
    public string Estado { get; set; } = "emitido"; // emitido | anulado

    /// <summary>Tipo de comprobante: X (cotizacion/remito interno), FA, FB, FC.</summary>
    [MaxLength(10)]
    public string TipoComprobante { get; set; } = "X";

    /// <summary>Condicion IVA del cliente: CF (consumidor final), RI (responsable inscripto), MO (monotributo), EX (exento).</summary>
    [MaxLength(20)]
    public string CondicionIva { get; set; } = "CF";

    /// <summary>Condicion de pago: EFECTIVO, TRANSFERENCIA, DEBITO, CREDITO, CTA_CORRIENTE, CHEQUE.</summary>
    [MaxLength(20)]
    public string CondicionPago { get; set; } = "EFECTIVO";

    /// <summary>CSV con dias de la semana de visita/reparto (LUN,MAR,MIE,...). Aparece al pie del comprobante.</summary>
    [MaxLength(50)]
    public string? WeekDays { get; set; }

    /// <summary>Si esta marcado como pagado (estampa el sello en el PDF).</summary>
    public bool IsPaid { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<CafeVentaItem> Items { get; set; } = new List<CafeVentaItem>();
}

[Table("Cafe_VentaItems")]
public class CafeVentaItem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int VentaId { get; set; }

    [ForeignKey(nameof(VentaId))]
    public CafeVenta? VentaNav { get; set; }

    public int ProductoId { get; set; }

    [ForeignKey(nameof(ProductoId))]
    public CafeProducto? ProductoNav { get; set; }

    [Required, MaxLength(200)]
    public string ProductoNombreSnapshot { get; set; } = string.Empty;

    [Required, MaxLength(20)]
    public string Categoria { get; set; } = "CAFE"; // CAFE | OTROS

    [Required, MaxLength(20)]
    public string Formato { get; set; } = "1KG"; // 1KG | MEDIO | CUARTO | UNIT

    public int Cantidad { get; set; }

    [Column(TypeName = "decimal(18,2)")] public decimal PrecioUnitario { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal CostoUnitario { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Subtotal { get; set; }
    [Column(TypeName = "decimal(18,3)")] public decimal GramosDescontados { get; set; }

    /// <summary>Tipo de molienda (solo CAFE): "EN GRANOS" | "MOLIDO FILTRO" | "MOLIDO ESPRESS" | null = sin especificar.</summary>
    [MaxLength(30)]
    public string? Molienda { get; set; }

    /// <summary>Si el producto va en envase doy pack. Aparece como 'd.p.' al lado del nombre en el comprobante.</summary>
    public bool EsDoyPack { get; set; }
}
