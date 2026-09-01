using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Alq_Equipos")]
public class AlqEquipo
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(50)]
    public string Sku { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Nombre { get; set; } = string.Empty;

    [MaxLength(80)]
    public string? Categoria { get; set; }

    [MaxLength(500)]
    public string? Descripcion { get; set; }

    public int StockTotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal PrecioDiario { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? PrecioReposicion { get; set; }

    /// <summary>
    /// En qué lugar de la lista aparece al cotizar. 2026-08-31: antes salían por orden alfabético
    /// y lo que más se alquila (sillas, mesas) quedaba mezclado. Se ordena con las flechitas de
    /// Alquileres → Equipos. Los que tienen 0 caen al final, ordenados por nombre.
    /// </summary>
    public int Orden { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
