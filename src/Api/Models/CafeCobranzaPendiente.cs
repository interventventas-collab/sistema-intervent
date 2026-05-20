using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Cobranza precargada por un repartidor desde la pantalla mobile /repartidor/{token}.
/// Queda en estado PENDIENTE hasta que el admin la apruebe en /cafe/cobranzas-pendientes,
/// momento en el cual se crea una CafeCobranza real (con todos los medios). El admin
/// puede tambien rechazarla — queda como RECHAZADA y no impacta en nada.
///
/// Siempre es EFECTIVO (los otros medios los maneja el usuario desde central). Pedido 2026-05-19.
/// </summary>
[Table("Cafe_CobranzasPendientes")]
public class CafeCobranzaPendiente
{
    public int Id { get; set; }

    public int VentaId { get; set; }
    [ForeignKey(nameof(VentaId))] public CafeVenta? Venta { get; set; }

    public int RepartidorId { get; set; }
    [ForeignKey(nameof(RepartidorId))] public CafeRepartidor? Repartidor { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Importe { get; set; }

    /// <summary>Si el repartidor tildo "entregue" — se refleja en la venta tambien (campo
    /// EntregadoPorRepartidorId) y, si la venta estaba en el flujo de Preparacion, pasa
    /// automaticamente a ENTREGADO.</summary>
    public bool MarcadoEntregado { get; set; }

    [MaxLength(500)]
    public string? Notas { get; set; }

    /// <summary>PENDIENTE | APROBADA | RECHAZADA</summary>
    [Required, MaxLength(20)]
    public string Estado { get; set; } = "PENDIENTE";

    /// <summary>Id de la cobranza real creada al aprobar (linkea para auditoria).</summary>
    public int? CobranzaCreadaId { get; set; }

    [MaxLength(120)]
    public string? RechazadaMotivo { get; set; }

    [MaxLength(120)]
    public string? RevisadaPor { get; set; }

    public DateTime? RevisadaAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
