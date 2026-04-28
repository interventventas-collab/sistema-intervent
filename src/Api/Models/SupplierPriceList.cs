using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("SupplierPriceLists")]
public class SupplierPriceList
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    public int? SupplierId { get; set; }
    [ForeignKey(nameof(SupplierId))]
    public Supplier? Supplier { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public DateTime? LastUploadAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<SupplierPriceListItem> Items { get; set; } = new List<SupplierPriceListItem>();
}

[Table("SupplierPriceListItems")]
public class SupplierPriceListItem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int PriceListId { get; set; }
    [ForeignKey(nameof(PriceListId))]
    public SupplierPriceList? PriceList { get; set; }

    [Required, MaxLength(100)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal CostPrice { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? SuggestedRetailPrice { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
