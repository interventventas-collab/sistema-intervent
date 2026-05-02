using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Markup % que aplica una empresa sobre el costo de los productos de una marca.
/// Se usa cuando NO hay override de precio del producto en esa empresa.
/// PVP_calculado = CostPrice * (1 + MarkupPercent / 100)
/// </summary>
[Table("BrandCompanyMarkups")]
public class BrandCompanyMarkup
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int BrandId { get; set; }
    [ForeignKey(nameof(BrandId))]
    public Brand? Brand { get; set; }

    public int CompanyId { get; set; }
    [ForeignKey(nameof(CompanyId))]
    public Company? Company { get; set; }

    [Column(TypeName = "decimal(8,2)")]
    public decimal MarkupPercent { get; set; }

    /// <summary>
    /// Modo de calculo del precio para esta marca en esta empresa:
    ///   'PERCENT' = precio = costo × (1 + MarkupPercent / 100)
    ///   'PVP' = precio = el RetailPrice cargado en el producto (ignora MarkupPercent)
    /// </summary>
    [MaxLength(20)]
    public string PriceMode { get; set; } = "PERCENT";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
