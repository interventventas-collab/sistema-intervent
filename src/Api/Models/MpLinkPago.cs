using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Link de pago de Mercado Pago creado desde el sistema (Checkout Pro: POST /checkout/preferences).
/// El cliente abre el link y paga; el sistema busca el pago por ReferenciaExterna
/// ("AIML-LINK-{Id}") en /v1/payments/search y guarda lo que pagó y cuánto quedó neto.
/// Pedido 2026-10-13: "un botón con el logo de Mercado Pago para empezar a usarlo".
/// </summary>
[Table("Mp_LinksPago")]
public class MpLinkPago
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Monto { get; set; }

    /// <summary>Lo que ve el cliente en Mercado Pago (título del ítem).</summary>
    [MaxLength(200)]
    public string Concepto { get; set; } = string.Empty;

    /// <summary>Para quién es (solo para nosotros, el cliente no lo ve).</summary>
    [MaxLength(200)]
    public string? Cliente { get; set; }

    /// <summary>"1" = 1 pago sin efectivo · "3" = hasta 3 cuotas sin efectivo · "todo" = todo permitido.</summary>
    [MaxLength(10)]
    public string Opcion { get; set; } = "1";

    [MaxLength(100)]
    public string? MpPreferenceId { get; set; }

    [MaxLength(500)]
    public string? Link { get; set; }

    [MaxLength(60)]
    public string ReferenciaExterna { get; set; } = string.Empty;

    /// <summary>Pendiente | En proceso | Pagado | Devuelto</summary>
    [MaxLength(20)]
    public string Estado { get; set; } = "Pendiente";

    // --- Lo que pasó cuando pagaron (de /v1/payments) ---
    public long? MpPaymentId { get; set; }
    public DateTime? PagadoAt { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? MontoNeto { get; set; }
    [MaxLength(50)]
    public string? MedioPago { get; set; }
    [MaxLength(30)]
    public string? TipoMedio { get; set; }
    public int? Cuotas { get; set; }
    [MaxLength(200)]
    public string? PagadorNombre { get; set; }
    [MaxLength(200)]
    public string? UltimoIntento { get; set; }

    [MaxLength(120)]
    public string? CreadoPor { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevisadoAt { get; set; }
}
