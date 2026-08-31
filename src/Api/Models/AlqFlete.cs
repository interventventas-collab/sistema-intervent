using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Precios de flete por zona/localidad para cotizar alquileres. 2026-08-31.
/// Es una lista propia (no depende de las zonas de reparto): el usuario la carga
/// y edita desde Alquileres -> Fletes, y la calculadora la ofrece al cotizar.
/// </summary>
[Table("Alq_Fletes")]
public class AlqFlete
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Zona o localidad: "Escobar", "CABA", "Pilar centro"...</summary>
    [Required, MaxLength(120)]
    public string Zona { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Precio { get; set; }

    /// <summary>Aclaracion opcional: "ida y vuelta", "hasta 200 sillas", etc.</summary>
    [MaxLength(300)]
    public string? Notas { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
