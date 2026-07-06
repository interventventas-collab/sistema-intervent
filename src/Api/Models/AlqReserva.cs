using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Alq_Reservas")]
public class AlqReserva
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(20)]
    public string Numero { get; set; } = string.Empty;

    // 2026-07-02: ClienteId apunta a la base de clientes GENERAL (Cafe_Clientes), unificado con ventas.
    public int ClienteId { get; set; }

    [ForeignKey(nameof(ClienteId))]
    public CafeCliente? ClienteNav { get; set; }

    public DateTime FechaEntrega { get; set; }
    public DateTime FechaRetiro { get; set; }

    /// <summary>Día del evento en sí (la fiesta). Suele caer entre la entrega y el retiro. Opcional.</summary>
    public DateTime? FechaEvento { get; set; }

    [MaxLength(8)]
    public string? HoraInicio { get; set; }

    [MaxLength(8)]
    public string? HoraFin { get; set; }

    [MaxLength(300)]
    public string? DireccionEvento { get; set; }

    /// <summary>Link de Google Maps del lugar del evento (pin exacto). Opcional. 2026-07-02.</summary>
    [MaxLength(500)]
    public string? MapeoLink { get; set; }

    [Column(TypeName = "decimal(10,7)")]
    public decimal? LatitudEvento { get; set; }

    [Column(TypeName = "decimal(10,7)")]
    public decimal? LongitudEvento { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal MontoTotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Sena { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Descuento { get; set; }

    [MaxLength(30)]
    public string Estado { get; set; } = "reservado";

    [MaxLength(1000)]
    public string? Notas { get; set; }

    /// <summary>Forma de pago de la reserva (Efectivo, Transferencia, etc.). 2026-07-04.</summary>
    [MaxLength(30)]
    public string? FormaPago { get; set; }

    // ===== QR + Repartidor (2026-06-26) =====
    /// <summary>Token publico para el QR del comprobante: lleva a /alquiler/{token}.</summary>
    [MaxLength(64)]
    public string? PublicToken { get; set; }

    /// <summary>Monto cobrado en mano por repartidores (cobranzas aprobadas). Baja el saldo.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal MontoCobrado { get; set; }

    public int? EntregadoPorRepartidorId { get; set; }
    [ForeignKey(nameof(EntregadoPorRepartidorId))]
    public CafeRepartidor? EntregadoPorRepartidor { get; set; }
    public DateTime? EntregadoAt { get; set; }
    [MaxLength(500)]
    public string? ComentarioEntrega { get; set; }

    public int? RetiradoPorRepartidorId { get; set; }
    [ForeignKey(nameof(RetiradoPorRepartidorId))]
    public CafeRepartidor? RetiradoPorRepartidor { get; set; }
    public DateTime? RetiradoAt { get; set; }
    [MaxLength(500)]
    public string? ComentarioRetiro { get; set; }

    // ============================================================
    // ARCA — facturación electrónica de la reserva (2026-07-04)
    // Mismo esquema que CafeVenta. Aditivo: una reserva sin facturar queda en "no_aplica".
    // ============================================================
    /// <summary>Tipo de comprobante: "X" (sin facturar/remito) | FA | FB | FC.</summary>
    [MaxLength(10)]
    public string TipoComprobante { get; set; } = "X";

    /// <summary>Condición IVA del cliente receptor: CF | RI | MO | EX.</summary>
    [MaxLength(20)]
    public string CondicionIva { get; set; } = "CF";

    /// <summary>Concepto AFIP: 1=Productos, 2=Servicios (default alquiler), 3=Mixto.</summary>
    public int Concepto { get; set; } = 2;

    /// <summary>"no_aplica" (X) | "pendiente" | "autorizado" | "rechazado".</summary>
    [MaxLength(20)]
    public string ArcaEstado { get; set; } = "no_aplica";

    [MaxLength(20)]
    public string? ArcaCae { get; set; }
    public DateTime? ArcaCaeVto { get; set; }
    /// <summary>Fecha de emisión que ARCA registró (para el PDF y el QR fiscal).</summary>
    public DateTime? ArcaFecha { get; set; }
    public int? ArcaPtoVta { get; set; }

    /// <summary>Certificado/CUIT (ArcaWebserviceAccount) con el que se emitió.</summary>
    public int? ArcaWebserviceAccountId { get; set; }

    public int? ArcaCbteNro { get; set; }
    /// <summary>1=FA, 6=FB, 11=FC.</summary>
    public int? ArcaCbteTipoNum { get; set; }

    [MaxLength(1000)]
    public string? ArcaError { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? ArcaImpNeto { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? ArcaImpIVA { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? ArcaImpTotal { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<AlqReservaItem> Items { get; set; } = new List<AlqReservaItem>();
}

[Table("Alq_ReservaItems")]
public class AlqReservaItem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int ReservaId { get; set; }

    [ForeignKey(nameof(ReservaId))]
    public AlqReserva? ReservaNav { get; set; }

    // 2026-07-06: nullable — un item puede ser de "descripción libre" (texto), sin equipo del catálogo.
    public int? EquipoId { get; set; }

    [ForeignKey(nameof(EquipoId))]
    public AlqEquipo? EquipoNav { get; set; }

    /// <summary>Texto libre cuando el item NO es un equipo del catálogo (ej: "Flete especial", "Mantelería"). 2026-07-06.</summary>
    [MaxLength(300)]
    public string? Descripcion { get; set; }

    public int Cantidad { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal PrecioUnitario { get; set; }
}
