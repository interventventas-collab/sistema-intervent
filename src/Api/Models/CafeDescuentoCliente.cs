using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

// Descuento aplicable a un tipo de cliente (BAR / OTRO).
// MarcaId NULL = descuento general (para todas las marcas que no tengan override puntual).
// MarcaId con valor = override solo para esa marca.
[Table("Cafe_DescuentosCliente")]
public class CafeDescuentoCliente
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(20)]
    public string TipoCliente { get; set; } = "OTRO";

    public int? MarcaId { get; set; }

    [ForeignKey(nameof(MarcaId))]
    public CafeMarca? MarcaNav { get; set; }

    [Column(TypeName = "decimal(7,2)")]
    public decimal DescuentoPct { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
