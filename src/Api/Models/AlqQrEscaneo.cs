using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Registro de cuando un repartidor "carga" (escanea el QR) o actua sobre una reserva de alquiler.
/// La asignacion sigue la regla de ventas: el dueño del pedido es el repartidor del ULTIMO
/// escaneo con Accion='cargado'. Espejo de Cafe_QrEscaneos. Pedido 2026-06-26.
/// </summary>
[Table("Alq_QrEscaneos")]
public class AlqQrEscaneo
{
    public int Id { get; set; }

    public int ReservaId { get; set; }
    [ForeignKey(nameof(ReservaId))] public AlqReserva? Reserva { get; set; }

    public int RepartidorId { get; set; }
    [ForeignKey(nameof(RepartidorId))] public CafeRepartidor? Repartidor { get; set; }

    /// <summary>cargado | entregado | retirado | cobrado</summary>
    [Required, MaxLength(20)]
    public string Accion { get; set; } = "cargado";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(64)]
    public string? Ip { get; set; }
}
