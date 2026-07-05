using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Movimientos de la cuenta de Mercado Pago, traídos del Reporte oficial "Dinero en la cuenta"
/// (settlement report / account money). Cada fila del CSV del reporte es un movimiento: ventas,
/// retiros, comisiones, etc. La columna SETTLEMENT_NET_AMOUNT es el impacto real en el saldo.
///
/// Sumando el neto se obtiene el "neto del período" (lo más cercano a un saldo — MP deprecó el
/// número de saldo directo). Con VentaIdAsociada/CobranzaUsadaId se concilia después.
/// Pedido de Osmar 2026-07-05 (Parte B).
/// </summary>
[Table("Mp_Movimientos")]
public class MpMovimiento
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>SOURCE_ID del reporte (id de la operación en MP, ej. el payment id). Puede repetirse por tipo.</summary>
    [MaxLength(60)]
    public string? SourceId { get; set; }

    /// <summary>Fecha de la transacción (TRANSACTION_DATE).</summary>
    public DateTime Fecha { get; set; }

    /// <summary>Fecha de liquidación/acreditación (SETTLEMENT_DATE), si viene.</summary>
    public DateTime? FechaLiquidacion { get; set; }

    /// <summary>Tipo de movimiento (TRANSACTION_TYPE: payment, refund, payout/withdrawal, fee, etc).</summary>
    [MaxLength(60)]
    public string? TipoTransaccion { get; set; }

    [MaxLength(300)]
    public string? Descripcion { get; set; }

    /// <summary>Monto bruto de la operación (TRANSACTION_AMOUNT).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal MontoBruto { get; set; }

    /// <summary>Comisión de Mercado Pago (FEE_AMOUNT), en negativo o positivo según el reporte.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Comision { get; set; }

    /// <summary>Impacto real en el saldo (SETTLEMENT_NET_AMOUNT). ESTA es la columna clave.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal MontoNeto { get; set; }

    [MaxLength(10)]
    public string? Moneda { get; set; }

    [MaxLength(60)]
    public string? MedioPago { get; set; }

    [MaxLength(200)]
    public string? ReferenciaExterna { get; set; }

    [MaxLength(60)]
    public string? OrderId { get; set; }

    /// <summary>Hash de las columnas clave para deduplicar al re-procesar reportes solapados.</summary>
    [Required, MaxLength(80)]
    public string HashUnico { get; set; } = "";

    // --- Conciliación (mismo criterio que Cafe_ExtractoMovimiento) ---
    public int? VentaIdAsociada { get; set; }
    public int? CobranzaUsadaId { get; set; }
    [MaxLength(120)]
    public string? AsociadoPor { get; set; }
    public DateTime? AsociadoAt { get; set; }

    [MaxLength(200)]
    public string? ReporteArchivo { get; set; }

    public DateTime ImportadoAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
