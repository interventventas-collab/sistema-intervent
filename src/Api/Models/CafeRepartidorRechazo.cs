using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-08-12: cuando un repartidor RECHAZA un envío que le asignaron (desde el celu, en
/// /mis-pedidos), queda una fila acá con el motivo obligatorio. Sirve para:
///   1) Sacar ese envío de la lista del repartidor (las 3 listas lo excluyen).
///   2) Que el dueño reciba el aviso "ENVIO_RECHAZADO" (Mis Alertas: campanita/Telegram/WhatsApp).
/// El envío NO se pierde: vuelve a estar disponible para reasignar desde el panel del admin.
/// </summary>
[Table("Cafe_RepartidorRechazos")]
public class CafeRepartidorRechazo
{
    public int Id { get; set; }

    public int RepartidorId { get; set; }
    [ForeignKey(nameof(RepartidorId))]
    public CafeRepartidor? Repartidor { get; set; }

    /// <summary>De dónde salía el envío rechazado: "venta_cafe" (venta de café),
    /// "me1" (Flex/ME1 de MercadoLibre) o "mapeo" (parada de la ruta del mapa).</summary>
    [Required, MaxLength(20)]
    public string Origen { get; set; } = "";

    /// <summary>Id del registro rechazado según el origen:
    /// venta_cafe = Cafe_Ventas.Id · me1 = MeliShipments.Id · mapeo = MapeoStops.Id.</summary>
    public int ReferenciaId { get; set; }

    /// <summary>Motivo escrito por el repartidor (obligatorio).</summary>
    [Required, MaxLength(500)]
    public string Motivo { get; set; } = "";

    /// <summary>Foto/descripción del envío al momento de rechazarlo (cliente, nº, dirección)
    /// para que el aviso y el historial digan de qué se trataba sin tener que ir a buscarlo.</summary>
    [MaxLength(300)]
    public string? Descripcion { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
