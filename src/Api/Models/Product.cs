using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Products")]
public class Product
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    [MaxLength(100)]
    public string? Brand { get; set; }

    [MaxLength(100)]
    public string? Model { get; set; }

    [MaxLength(100)]
    public string? Sku { get; set; }

    [MaxLength(50)]
    public string? Barcode { get; set; }

    [MaxLength(100)]
    public string? OemCode { get; set; }

    [MaxLength(1000)]
    public string? ImageUrl { get; set; }

    public string? Photo1 { get; set; }
    public string? Photo2 { get; set; }
    public string? Photo3 { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? VatRate { get; set; }

    [MaxLength(100)]
    public string? PurchaseAccount { get; set; }

    [MaxLength(100)]
    public string? SaleAccount { get; set; }

    [MaxLength(100)]
    public string? InventoryAccount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal CostPrice { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal RetailPrice { get; set; }

    public int Stock { get; set; }
    public int CriticalStock { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    // Producto base del que hereda costo y PVP. Si es null, es un producto independiente (o base).
    public int? BaseProductId { get; set; }

    [ForeignKey(nameof(BaseProductId))]
    public Product? BaseProduct { get; set; }

    public ICollection<Product> DerivedProducts { get; set; } = new List<Product>();

    // Marca asociada (clasificacion). Opcional.
    public int? BrandId { get; set; }

    [ForeignKey(nameof(BrandId))]
    public Brand? BrandNav { get; set; }

    public ICollection<MeliItem> MeliItems { get; set; } = new List<MeliItem>();
}
