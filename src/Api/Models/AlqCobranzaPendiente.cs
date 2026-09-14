using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Cobranza precargada por un repartidor desde la pantalla mobile /alquiler/{token}.
/// Queda PENDIENTE hasta que el admin la procese en /cafe/cobranzas-pendientes (desde 2026-09-14 con recibo;
/// la cobranza de Tesorería es la que suma a Alq_Reservas.MontoCobrado).
/// El admin tambien puede rechazarla. Espejo de Cafe_CobranzasPendientes. Pedido 2026-06-26.
/// </summary>
[Table("Alq_CobranzasPendientes")]
public class AlqCobranzaPendiente
{
    public int Id { get; set; }

    public int ReservaId { get; set; }
    [ForeignKey(nameof(ReservaId))] public AlqReserva? Reserva { get; set; }

    public int RepartidorId { get; set; }
    [ForeignKey(nameof(RepartidorId))] public CafeRepartidor? Repartidor { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Importe { get; set; }

    /// <summary>Momento del cobro: entrega | retiro.</summary>
    [Required, MaxLength(20)]
    public string Tipo { get; set; } = "entrega";

    /// <summary>El repartidor tildo "entregue los equipos" junto con el cobro.</summary>
    public bool MarcadoEntregado { get; set; }

    /// <summary>El repartidor tildo "retire los equipos" junto con el cobro.</summary>
    public bool MarcadoRetirado { get; set; }

    [MaxLength(500)]
    public string? Notas { get; set; }

    /// <summary>PENDIENTE | APROBADA | RECHAZADA</summary>
    [Required, MaxLength(20)]
    public string Estado { get; set; } = "PENDIENTE";

    [MaxLength(120)]
    public string? RechazadaMotivo { get; set; }

    [MaxLength(120)]
    public string? RevisadaPor { get; set; }

    public DateTime? RevisadaAt { get; set; }

    /// <summary>2026-09-14: la cobranza de Tesorería que se hizo con este cobro ("Procesar cobranza →",
    /// igual que las de ventas). Null = se aprobó con el botón viejo, que solo bajaba el saldo de la
    /// reserva sin recibo ni caja.</summary>
    public int? CobranzaCreadaId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
