using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

// Regla de precio por tipo cliente + categoria (con override opcional por marca).
// MarcaId NULL = regla general; con valor = override puntual.
[Table("Cafe_ReglasPrecios")]
public class CafeReglaPrecio
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(20)]
    public string TipoCliente { get; set; } = "OTRO";

    [Required, MaxLength(20)]
    public string Categoria { get; set; } = "OTROS"; // CAFE | OTROS

    public int? MarcaId { get; set; }

    [ForeignKey(nameof(MarcaId))]
    public CafeMarca? MarcaNav { get; set; }

    [Column(TypeName = "decimal(7,2)")]
    public decimal DescuentoPct { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
