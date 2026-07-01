using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-07-01: archivo adjunto de una liquidación (recibo, nómina, aguinaldo, etc.).
/// Se pueden subir varios por liquidación. El contenido se guarda EN LA BASE (varbinary) a propósito,
/// para que entre en los backups automáticos de la DB — son documentos importantes que no se deben perder.
/// Se borra en cascada si se borra la liquidación.</summary>
[Table("Nom_NominaArchivos")]
public class NomNominaArchivo
{
    [Key]
    public int Id { get; set; }

    public int LiquidacionId { get; set; }
    [ForeignKey(nameof(LiquidacionId))]
    public NomLiquidacion? LiquidacionNav { get; set; }

    [MaxLength(255)]
    public string FileName { get; set; } = "";

    [MaxLength(120)]
    public string ContentType { get; set; } = "application/pdf";

    public long FileSize { get; set; }

    /// <summary>El contenido binario del archivo (PDF o imagen).</summary>
    public byte[] Contenido { get; set; } = System.Array.Empty<byte>();

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(120)]
    public string? UploadedBy { get; set; }
}
