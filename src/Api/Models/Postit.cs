using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Postits")]
public class Postit
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    public string Texto { get; set; } = string.Empty;

    [MaxLength(20)]
    public string Color { get; set; } = "amarillo";

    [MaxLength(100)]
    public string? CreadoPor { get; set; }

    /// <summary>Tablero al que pertenece el postit. "dashboard" (default), "nominas", etc.</summary>
    [MaxLength(50)]
    public string Scope { get; set; } = "dashboard";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
