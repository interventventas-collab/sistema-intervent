using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Cobros/pagos recibidos por Mercado Pago, traidos de la API oficial /v1/payments/search.
/// Etapa "lo cobrado por MP": ingresos que entraron a la cuenta. Sirve para verlos y, mas
/// adelante, conciliarlos con ventas/reservas (por eso VentaIdAsociada + CobranzaUsadaId,
/// mismo criterio que Cafe_ExtractoMovimiento).
///
/// El saldo NETO real de la cuenta va por otro lado (Reportes "Dinero en la cuenta"), porque
/// Mercado Pago deprecó el endpoint directo de balance. Pedido de Osmar 2026-07-05.
/// </summary>
[Table("Mp_Pagos")]
public class MpPago
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>ID del pago en Mercado Pago (unico — se usa para dedup al re-sincronizar).</summary>
    public long MpPaymentId { get; set; }

    /// <summary>Fecha de acreditacion (date_approved) o de creacion si no hay.</summary>
    public DateTime Fecha { get; set; }

    /// <summary>approved | pending | in_process | rejected | refunded | cancelled | charged_back</summary>
    [MaxLength(30)]
    public string? Estado { get; set; }

    /// <summary>Detalle del estado (accredited, etc).</summary>
    [MaxLength(50)]
    public string? EstadoDetalle { get; set; }

    /// <summary>Monto bruto de la operacion (transaction_amount).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Monto { get; set; }

    /// <summary>Monto neto recibido despues de comisiones (net_received_amount), si viene.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? MontoNeto { get; set; }

    [MaxLength(300)]
    public string? Descripcion { get; set; }

    /// <summary>Email de quien pago.</summary>
    [MaxLength(200)]
    public string? PayerEmail { get; set; }

    /// <summary>Nombre de quien pago (si viene).</summary>
    [MaxLength(200)]
    public string? PayerNombre { get; set; }

    /// <summary>Medio de pago (payment_method_id: visa, master, account_money, etc).</summary>
    [MaxLength(50)]
    public string? MedioPago { get; set; }

    /// <summary>Tipo de operacion (operation_type: regular_payment, pos_payment, etc).</summary>
    [MaxLength(50)]
    public string? TipoOperacion { get; set; }

    /// <summary>Referencia externa que manda el vendedor (external_reference). Clave para conciliar
    /// con una venta/reserva nuestra si algun dia generamos cobros con esa referencia.</summary>
    [MaxLength(200)]
    public string? ReferenciaExterna { get; set; }

    // --- Conciliacion (mismo criterio que Cafe_ExtractoMovimiento) ---
    public int? VentaIdAsociada { get; set; }
    public int? CobranzaUsadaId { get; set; }
    [MaxLength(120)]
    public string? AsociadoPor { get; set; }
    public DateTime? AsociadoAt { get; set; }

    public DateTime ImportadoAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
