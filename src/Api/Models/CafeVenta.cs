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

    [MaxLength(200)]
    public string? ClienteRazonSocialSnapshot { get; set; }

    [MaxLength(500)]
    public string? ClienteDomicilioEntregaSnapshot { get; set; }

    public string? ClienteComentariosComprobante { get; set; }

    [MaxLength(50)]
    public string? ClienteCuitSnapshot { get; set; }

    [MaxLength(300)]
    public string? ClienteDireccionSnapshot { get; set; }

    [MaxLength(150)]
    public string? ClienteLocalidadSnapshot { get; set; }

    [MaxLength(150)]
    public string? ClienteCiudadSnapshot { get; set; }

    [MaxLength(20)]
    public string? ClienteCpSnapshot { get; set; }

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

    // ============================================================
    // ARCA — datos de la factura emitida (solo aplica si TipoComprobante in FA/FB/FC)
    // ============================================================
    /// <summary>"no_aplica" (X/PRO) | "pendiente" (rechazado o aún no emitido) | "autorizado" | "rechazado"</summary>
    [MaxLength(20)]
    public string ArcaEstado { get; set; } = "no_aplica";

    /// <summary>Código de Autorización Electrónica devuelto por ARCA (14 dígitos).</summary>
    [MaxLength(20)]
    public string? ArcaCae { get; set; }

    public DateTime? ArcaCaeVto { get; set; }

    /// <summary>Punto de venta usado para emitir (ej: 2).</summary>
    public int? ArcaPtoVta { get; set; }

    /// <summary>Número de comprobante asignado por ARCA (correlativo).</summary>
    public int? ArcaCbteNro { get; set; }

    /// <summary>1=Factura A, 6=Factura B, 11=Factura C — mapeado desde TipoComprobante.</summary>
    public int? ArcaCbteTipoNum { get; set; }

    [MaxLength(1000)]
    public string? ArcaError { get; set; }

    /// <summary>Nota tipo "post-it" del admin pegada a esta venta (interna, no se imprime).
    /// Útil para marcar ventas que requieren atención, dejarse recordatorios, etc.</summary>
    [MaxLength(1000)]
    public string? PinNota { get; set; }

    /// <summary>Token aleatorio (~20 chars) para link publico /comprobante/{token}. Permite
    /// que el operador comparta el comprobante por WhatsApp/Email sin attachments — el cliente
    /// abre el link y ve el comprobante online + boton para descargar PDF. Generado al crear
    /// la venta; en ventas migradas viejas puede ser null hasta el primer share.</summary>
    [MaxLength(64)]
    public string? PublicToken { get; set; }

    /// <summary>Importe Neto (sin IVA) que ARCA registró efectivamente. Guardamos lo que devuelve
    /// el FECAESolicitar para que el PDF reconstruya los totales sin lugar a interpretación.
    /// NULL en facturas viejas (pre-2026-05-15) que no guardaban esto — para esas se calcula
    /// fallback desde Subtotal/Total.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? ArcaImpNeto { get; set; }

    /// <summary>Importe IVA que ARCA registró (=0 para Factura C).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? ArcaImpIVA { get; set; }

    /// <summary>Importe Total (con IVA) que ARCA registró. Lo que el cliente declara en su CUIT.
    /// Es el valor que se imprime grande en el PDF del comprobante.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? ArcaImpTotal { get; set; }

    // ============================================================
    // Trazabilidad Proforma → Factura
    // ============================================================
    /// <summary>Si esta venta fue creada a partir de otra (típicamente una proforma convertida a
    /// factura), guardamos el Id de la venta origen para mantener el vínculo. Null = es una venta
    /// creada desde cero.</summary>
    public int? OrigenVentaId { get; set; }
    /// <summary>Si esta venta fue convertida a factura (PRO o X que se transformó en FA/FB/FC),
    /// guardamos el Id de la factura resultante. Null = no se convirtió todavía.</summary>
    public int? FacturadaComoVentaId { get; set; }

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

    /// <summary>FK a Cafe_Productos. Nullable porque los items de "concepto libre"
    /// (servicios, otros conceptos manuales que no están en el catálogo) no apuntan
    /// a un producto del inventario.</summary>
    public int? ProductoId { get; set; }

    [ForeignKey(nameof(ProductoId))]
    public CafeProducto? ProductoNav { get; set; }

    /// <summary>Si es true, este item es un "concepto libre" (descripción + precio cargado
    /// a mano, sin producto del catálogo). En ese caso ProductoId es null y no descuenta stock.</summary>
    public bool EsConceptoLibre { get; set; }

    [Required, MaxLength(200)]
    public string ProductoNombreSnapshot { get; set; } = string.Empty;

    [Required, MaxLength(20)]
    public string Categoria { get; set; } = "CAFE"; // CAFE | OTROS | LIBRE

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

    /// <summary>Si el producto va en envase plateado. Default: false = envase negro.
    /// Solo aplica si NO está marcado EsDoyPack (son mutuamente excluyentes en la UI).</summary>
    public bool EsEnvasePlateado { get; set; }

    /// <summary>Descuento porcentual aplicado a esta linea (0-100). 0 = sin descuento.</summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal DescuentoPct { get; set; }
}
