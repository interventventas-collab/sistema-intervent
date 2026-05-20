using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Metadata extra para archivos y carpetas del almacenamiento — color y emoji
/// personalizado. Se busca por Path (la ruta relativa al storage root). Si no
/// existe registro, se usan los defaults (color amarillo para carpetas, icono
/// segun extension para archivos).
/// </summary>
[Table("FileMetadata")]
public class FileMetadata
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(800)]
    public string Path { get; set; } = "";

    /// <summary>Color de la card. Valor del set: amarillo, azul, verde, rojo, morado, naranja, gris, blanco. Null = default.</summary>
    [MaxLength(20)]
    public string? Color { get; set; }

    /// <summary>Emoji que reemplaza el icono default (📁 para carpeta, segun extension para archivo). Null = default.</summary>
    [MaxLength(10)]
    public string? IconEmoji { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
