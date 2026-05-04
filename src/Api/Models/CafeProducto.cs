using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_Productos")]
public class CafeProducto
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Nombre { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? Sku { get; set; }

    [MaxLength(100)]
    public string? Barcode { get; set; }

    [Required, MaxLength(20)]
    public string Categoria { get; set; } = "CAFE"; // CAFE | OTROS

    [MaxLength(100)]
    public string? Marca { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Costo { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? PrecioPorKg { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? Pvp1 { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? Pvp2 { get; set; }

    /// <summary>Solo OTROS: % sobre costo para clientes BAR. NULL = BAR paga PVP (Pvp2).</summary>
    [Column(TypeName = "decimal(7,2)")]
    public decimal? BarPctSobreCosto { get; set; }

    /// <summary>Unidades por bulto (informativo, solo OTROS).</summary>
    public int? UxB { get; set; }

    [Column(TypeName = "decimal(18,3)")]
    public decimal StockGramos { get; set; }

    public int StockUnidades { get; set; }

    [MaxLength(500)]
    public string? Notas { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
