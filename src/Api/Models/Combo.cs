using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Combos")]
public class Combo
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Sku { get; set; }

    public string? Description { get; set; }

    public string? Photo { get; set; }

    // Modo de calculo del precio:
    //   "auto"    -> precio = suma de (cantidad * PVP) de los items.
    //   "manual"  -> precio fijo definido por el usuario (ManualPrice).
    //   "percent" -> precio = suma * (1 + PercentAdjustment/100).
    [Required]
    [MaxLength(10)]
    public string PriceMode { get; set; } = "auto";

    [Column(TypeName = "decimal(18,2)")]
    public decimal? ManualPrice { get; set; }

    [Column(TypeName = "decimal(8,2)")]
    public decimal? PercentAdjustment { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<ComboItem> Items { get; set; } = new List<ComboItem>();
}

[Table("ComboItems")]
public class ComboItem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int ComboId { get; set; }

    [ForeignKey(nameof(ComboId))]
    public Combo? Combo { get; set; }

    public int ProductId { get; set; }

    [ForeignKey(nameof(ProductId))]
    public Product? Product { get; set; }

    public int Quantity { get; set; } = 1;
}
