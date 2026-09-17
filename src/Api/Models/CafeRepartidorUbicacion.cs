using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-09-17: rastro de ubicacion del repartidor mientras reparte. Lo pidio Gabriel
/// ("que su recorrido funcione como GPS real") y se usa desde la oficina: en el mapa,
/// el boton "Por donde van" muestra donde esta cada repartidor y por donde fue hoy.
///
/// El celu manda un punto cada 15 minutos Y cada vez que el repartidor toca la pantalla
/// (abre /mis-pedidos/{token}, marca una entrega, escanea un QR). Es asi porque el
/// navegador le corta el GPS a las paginas que no estan a la vista: si el repartidor
/// bloquea el celu, deja de mandar. Por eso el mapa muestra SIEMPRE el "hace cuanto".
///
/// Solo se guarda si el repartidor tiene SeguirUbicacion=1 (lo prende la oficina en
/// Administracion -> Repartidores). Los puntos de mas de 30 dias se borran solos.
/// </summary>
[Table("Cafe_RepartidorUbicaciones")]
public class CafeRepartidorUbicacion
{
    public int Id { get; set; }

    public int RepartidorId { get; set; }
    [ForeignKey(nameof(RepartidorId))]
    public CafeRepartidor? Repartidor { get; set; }

    [Column(TypeName = "decimal(9,6)")]
    public decimal Lat { get; set; }

    [Column(TypeName = "decimal(9,6)")]
    public decimal Lng { get; set; }

    /// <summary>Precision en metros que informa el celu (mas chico = mejor). Null si no la dio.</summary>
    public int? Accuracy { get; set; }

    /// <summary>Que lo disparo: 'auto' (cada 15 min), 'apertura', 'entrega', 'escaneo'. Informativo.</summary>
    [MaxLength(20)]
    public string? Fuente { get; set; }

    /// <summary>UTC. Para mostrar en pantalla se pasa a hora argentina.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
