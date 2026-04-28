using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Brands")]
public class Brand
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(30)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    // Si es true, los productos de esta marca manejan stock por lotes con fecha de vencimiento.
    public bool HasExpiry { get; set; } = false;

    // Empresas en las que la marca se muestra (CSV). NULL o "" = visible para TODAS las empresas.
    // Valores posibles separados por coma: INTERVENT, INTEREVENTOS, FRIKAF, PALANICA
    [MaxLength(200)]
    public string? Companies { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}
