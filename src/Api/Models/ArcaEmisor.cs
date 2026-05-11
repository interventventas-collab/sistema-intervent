using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Datos legales del emisor (razón social, domicilio, IIBB, etc.) que van en
/// el header de los PDFs de los comprobantes ARCA. Una fila por CUIT.
/// Cualquier certificado .pfx que apunte a ese CUIT toma los datos de acá
/// cuando genera un PDF.
/// </summary>
[Table("ArcaEmisores")]
public class ArcaEmisor
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(11)]
    public string Cuit { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? RazonSocial { get; set; }

    [MaxLength(50)]
    public string CondicionIva { get; set; } = "Responsable Inscripto";

    [MaxLength(300)]
    public string? Domicilio { get; set; }

    /// <summary>"CM" (Convenio Multilateral) o "Local" (provincial), o null si no aplica.</summary>
    [MaxLength(20)]
    public string? IIBBTipo { get; set; }

    [MaxLength(30)]
    public string? IIBBNumero { get; set; }

    public DateTime? InicioActividades { get; set; }

    /// <summary>Path relativo (FileStorageService) del logo subido. Null si no hay logo.</summary>
    [MaxLength(500)]
    public string? LogoPath { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
