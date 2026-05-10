using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Certificado .pfx para autenticarse contra los webservices de ARCA (ex AFIP).
/// Cada CUIT puede tener varios certificados (ej: producción + homologación,
/// distintos ambientes/aliases). Cada certificado se guarda en disco como un
/// archivo .pfx; en la DB queda solo el path relativo.
/// </summary>
[Table("ArcaWebserviceAccounts")]
public class ArcaWebserviceAccount
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>CUIT al que pertenece el certificado (11 dígitos, sin guiones).</summary>
    [Required, MaxLength(11)]
    public string Cuit { get; set; } = string.Empty;

    /// <summary>Alias opcional (ej: "Facturación electrónica producción").</summary>
    [MaxLength(100)]
    public string? Alias { get; set; }

    /// <summary>Nombre original del archivo (con extensión .pfx).</summary>
    [Required, MaxLength(255)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>Path relativo al root de FileStorageService (ej: "Certificados ARCA/30715938091/cert.pfx").</summary>
    [Required, MaxLength(500)]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Contraseña del .pfx (si tiene). Algunos certificados se exportan sin
    /// password, en ese caso queda null. Se guarda en texto porque ARCA la
    /// necesita en runtime para firmar requests.
    /// </summary>
    [MaxLength(500)]
    public string? Password { get; set; }

    /// <summary>"production" | "homologation".</summary>
    [Required, MaxLength(20)]
    public string Environment { get; set; } = "production";

    /// <summary>Vencimiento del certificado leído del .pfx. NULL si no se pudo parsear.</summary>
    public DateTime? ExpiresAt { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
