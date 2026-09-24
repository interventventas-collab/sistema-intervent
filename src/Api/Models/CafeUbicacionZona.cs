using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-09-24: zona del depósito dentro de una planta. El código corto (TOST) es lo que
/// se ve en el celu y en las etiquetas; el nombre largo (Tostadero) es para el que es nuevo.</summary>
[Table("Cafe_UbicacionZonas")]
public class CafeUbicacionZona
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(10)]
    public string Planta { get; set; } = "";

    [Required, MaxLength(30)]
    public string Codigo { get; set; } = "";

    [MaxLength(100)]
    public string? Nombre { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
