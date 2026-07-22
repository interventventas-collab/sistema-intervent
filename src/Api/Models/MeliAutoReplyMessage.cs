using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Un mensaje de la lista de respuestas automáticas de preguntas MeLi.
/// El texto NO incluye la firma (esa se agrega aparte, configurable).
/// El robot elige uno al azar entre los que estén activos.
/// </summary>
[Table("MeliAutoReplyMessages")]
public class MeliAutoReplyMessage
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Cuerpo del mensaje, sin la firma.</summary>
    [Required]
    public string Body { get; set; } = string.Empty;

    /// <summary>Si está apagado, el robot no lo usa (pero no se borra).</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}
