using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Override de precio para un producto en una empresa especifica.
/// Si existe, gana sobre el calculo por marca y sobre el RetailPrice default del producto.
/// </summary>
[Table("ProductCompanyPrices")]
public class ProductCompanyPrice
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int ProductId { get; set; }
    [ForeignKey(nameof(ProductId))]
    public Product? Product { get; set; }

    public int CompanyId { get; set; }
    [ForeignKey(nameof(CompanyId))]
    public Company? Company { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal RetailPrice { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
