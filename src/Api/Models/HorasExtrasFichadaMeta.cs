using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-06-03: metadata por evento de fichada (entrada O salida). Cada vez que el empleado
/// marca, se inserta una fila aca con IP/GPS/huella verificada. Sirve para auditoria.
/// NO modifica el registro existente HorasExtras_Registros para no romper queries viejas.
/// </summary>
[Table("HorasExtras_FichadaMeta")]
public class HorasExtrasFichadaMeta
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int RegistroId { get; set; }
    [ForeignKey(nameof(RegistroId))]
    public HorasExtrasRegistro? Registro { get; set; }

    /// <summary>"ENTRADA" o "SALIDA" — distingue de cuál de las 2 marcaciones es.</summary>
    [Required, MaxLength(10)]
    public string Tipo { get; set; } = "";

    /// <summary>IP publica del cliente al momento de marcar.</summary>
    [MaxLength(64)]
    public string? Ip { get; set; }

    /// <summary>Si la IP era una de las autorizadas (WiFi 1 o 2). Si false: ¿por qué se permitio?
    /// (modo nuevo OFF o sin IPs configuradas).</summary>
    public bool IpAutorizada { get; set; }

    /// <summary>Coordenadas GPS si el browser las mando (no obligatorio, no bloqueante).</summary>
    [Column(TypeName = "decimal(10,7)")]
    public decimal? Lat { get; set; }
    [Column(TypeName = "decimal(10,7)")]
    public decimal? Lon { get; set; }
    public int? GpsAccuracyMeters { get; set; }

    /// <summary>Si esta fichada se verifico con WebAuthn (huella OK).</summary>
    public bool HuellaVerificada { get; set; }

    /// <summary>Si se uso PIN como fallback (sin huella). Util para reportes.</summary>
    public bool UsoFallbackPin { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
