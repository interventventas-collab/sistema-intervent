using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-07-31: documentación personal adjunta de un empleado (DNI, contrato, certificados, etc.).
/// Se pueden subir varios por empleado. El contenido se guarda EN LA BASE (varbinary) a propósito,
/// para que entre en los backups automáticos de la DB — son documentos importantes que no se deben perder.
/// Se borra en cascada si se borra el empleado. Mismo patrón que Nom_NominaArchivos.</summary>
[Table("Nom_EmpleadoArchivos")]
public class NomEmpleadoArchivo
{
    [Key]
    public int Id { get; set; }

    public int EmpleadoId { get; set; }
    [ForeignKey(nameof(EmpleadoId))]
    public NomEmpleado? EmpleadoNav { get; set; }

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
