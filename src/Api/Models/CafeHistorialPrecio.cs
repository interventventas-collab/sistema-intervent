using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_HistorialPrecios")]
public class CafeHistorialPrecio
{
    [Key]
    public int Id { get; set; }

    public int ProductoId { get; set; }

    [ForeignKey(nameof(ProductoId))]
    public CafeProducto? Producto { get; set; }

    [Column(TypeName = "decimal(18,2)")] public decimal? Pvp1Anterior { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? Pvp2Anterior { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? CostoAnterior { get; set; }
    [Column(TypeName = "decimal(5,2)")]  public decimal? IvaPctAnterior { get; set; }

    [Column(TypeName = "decimal(18,2)")] public decimal? Pvp1Nuevo { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? Pvp2Nuevo { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? CostoNuevo { get; set; }
    [Column(TypeName = "decimal(5,2)")]  public decimal? IvaPctNuevo { get; set; }

    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string? ChangedBy { get; set; }

    [MaxLength(500)]
    public string? Motivo { get; set; }
}
