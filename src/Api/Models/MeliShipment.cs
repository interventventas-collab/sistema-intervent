using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("MeliShipments")]
public class MeliShipment
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public long MeliShipmentId { get; set; }
    public int MeliAccountId { get; set; }

    [ForeignKey(nameof(MeliAccountId))]
    public MeliAccount? MeliAccount { get; set; }

    public long? MeliOrderId { get; set; }

    [MaxLength(40)] public string? Status { get; set; }
    [MaxLength(40)] public string? Substatus { get; set; }
    [MaxLength(30)] public string? LogisticType { get; set; }
    /// <summary>Modo de envio segun MeLi: me1 (manda el vendedor), me2, custom, not_specified.</summary>
    [MaxLength(30)] public string? Mode { get; set; }
    [MaxLength(60)] public string? TrackingNumber { get; set; }

    [MaxLength(200)] public string? ReceiverName { get; set; }
    [MaxLength(50)] public string? ReceiverPhone { get; set; }
    /// <summary>Nickname del comprador en MeLi (para cotejar con el panel).</summary>
    [MaxLength(100)] public string? BuyerNickname { get; set; }

    [MaxLength(300)] public string? AddressLine { get; set; }
    [MaxLength(200)] public string? StreetName { get; set; }
    [MaxLength(20)] public string? StreetNumber { get; set; }
    [MaxLength(150)] public string? Neighborhood { get; set; }
    [MaxLength(150)] public string? City { get; set; }
    [MaxLength(150)] public string? State { get; set; }
    [MaxLength(20)] public string? ZipCode { get; set; }

    [Column(TypeName = "decimal(10,7)")] public decimal? Latitude { get; set; }
    [Column(TypeName = "decimal(10,7)")] public decimal? Longitude { get; set; }
    [MaxLength(50)] public string? GeolocationType { get; set; }

    [MaxLength(500)] public string? Comment { get; set; }
    [MaxLength(500)] public string? ItemsSummary { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? OrderTotal { get; set; }

    public DateTime? DateCreated { get; set; }
    public DateTime? DateReadyToShip { get; set; }
    public DateTime? DateShipped { get; set; }
    public DateTime? DateDelivered { get; set; }
    public DateTime? EstimatedDeliveryFinal { get; set; }
    public DateTime? EstimatedDeliveryLimit { get; set; }

    /// <summary>Estado interno para la pantalla de mapeo: pending | en_ruta | entregado | no_encontrado | reprogramado.</summary>
    [MaxLength(30)] public string InternalStatus { get; set; } = "pending";

    public string? Notes { get; set; }

    public DateTime LastSyncedAt { get; set; } = DateTime.UtcNow;

    /// <summary>2026-06-16: cuando posteamos el link de Google Maps como nota interna en la
    /// orden de MeLi (para el repartidor). Si esta seteado no volvemos a postear (evita duplicar).</summary>
    public DateTime? MapsNoteSentAt { get; set; }
}
