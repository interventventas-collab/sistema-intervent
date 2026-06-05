using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-06-05: Catalogo de servicios (envio, mano de obra, instalacion, etc).
/// No tiene stock. Se cobra como item adicional en la venta. Se cargan desde
/// /cafe/servicios y aparecen en el dropdown de "Servicio" en Nueva Venta.
/// </summary>
[Table("Cafe_Servicios")]
public class CafeServicio
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(120)]
    public string Nombre { get; set; } = "";

    [MaxLength(400)]
    public string? Descripcion { get; set; }

    /// <summary>Precio sin IVA (consistente con el resto del sistema).</summary>
    [Column(TypeName = "decimal(14,2)")]
    public decimal Precio { get; set; }

    /// <summary>IVA aplicable (default 21%).</summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal IvaPct { get; set; } = 21m;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
