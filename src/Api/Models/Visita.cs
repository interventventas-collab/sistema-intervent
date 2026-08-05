using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Recibo de VISITA (2026-08-05). Similar a una venta pero centrado en una descripcion
/// libre: sirve para registrar una visita a un cliente / un cambio de producto / una
/// garantia, generar un recibo con QR, que el cliente firme, y despues hacer seguimiento
/// (Etapa 2: escanear el QR y marcar "Visita realizada") y sumarla al mapa (Etapa 3).
/// </summary>
[Table("Visitas")]
public class Visita
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Cliente asociado (Cafe_Clientes.Id). Opcional: se puede cargar una visita suelta.</summary>
    public int? ClienteId { get; set; }

    /// <summary>Snapshot del nombre del cliente al momento de la visita.</summary>
    [Required]
    [MaxLength(200)]
    public string ClienteNombre { get; set; } = string.Empty;

    /// <summary>Snapshot del domicilio (entrega o fiscal) del cliente.</summary>
    [MaxLength(500)]
    public string? Direccion { get; set; }

    [MaxLength(150)]
    public string? Localidad { get; set; }

    [MaxLength(50)]
    public string? Telefono { get; set; }

    /// <summary>El corazon de la visita: motivo / que se hizo / que hay que hacer. Texto libre.</summary>
    [Required]
    public string Descripcion { get; set; } = string.Empty;

    /// <summary>"pendiente" (default) o "realizada".</summary>
    [MaxLength(20)]
    public string Estado { get; set; } = "pendiente";

    /// <summary>Firma del cliente (imagen PNG en base64 data URL). Opcional.</summary>
    public string? FirmaBase64 { get; set; }

    /// <summary>Nombre de quien firmo el recibo.</summary>
    [MaxLength(200)]
    public string? NombreFirmante { get; set; }

    /// <summary>Token aleatorio (~22 chars) para el link publico /visita/{token} y el QR.</summary>
    [MaxLength(64)]
    public string? PublicToken { get; set; }

    /// <summary>Comentario que deja quien la marca como realizada (Etapa 2, al escanear el QR).</summary>
    public string? ComentarioResolucion { get; set; }

    /// <summary>Fecha/hora en que se marco "Visita realizada".</summary>
    public DateTime? RealizadaAt { get; set; }

    /// <summary>Coordenadas (snapshot del cliente) para sumarla al mapa en la Etapa 3.</summary>
    [Column(TypeName = "decimal(10,7)")]
    public decimal? MapeoLat { get; set; }

    [Column(TypeName = "decimal(10,7)")]
    public decimal? MapeoLng { get; set; }

    [MaxLength(100)]
    public string? CreadoPor { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
