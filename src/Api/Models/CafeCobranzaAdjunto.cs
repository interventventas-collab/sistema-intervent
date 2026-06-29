using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-06-25: Archivos adjuntos a una cobranza (comprobante de retenciones,
/// comprobante de transferencia, etc.). Se guardan en /data/files/cobranzas/{cobranzaId}/.</summary>
[Table("Cafe_CobranzaAdjuntos")]
public class CafeCobranzaAdjunto
{
    [Key]
    public int Id { get; set; }

    public int CobranzaId { get; set; }
    [ForeignKey(nameof(CobranzaId))]
    public CafeCobranza? Cobranza { get; set; }

    /// <summary>RETENCION | TRANSFERENCIA | OTRO</summary>
    [Required, MaxLength(30)]
    public string Tipo { get; set; } = "OTRO";

    /// <summary>Path relativo dentro de /data/files (ej: cobranzas/123/abc.pdf)</summary>
    [Required, MaxLength(500)]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Nombre original con el que el usuario subio el archivo.</summary>
    [Required, MaxLength(255)]
    public string NombreOriginal { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? MimeType { get; set; }

    /// <summary>Tamano en bytes.</summary>
    public long Tamano { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
