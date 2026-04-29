using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Lista de precios que se aplica a un grupo de clientes (ej: Bares, Ventas, MercadoLibre).
/// Cada lista define un % de ajuste sobre el RetailPrice base del producto.
/// </summary>
[Table("CustomerTiers")]
public class CustomerTier
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(60)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Codigo interno corto (ej "bares", "ventas", "meli"). Unico.</summary>
    [Required]
    [MaxLength(20)]
    public string Code { get; set; } = string.Empty;

    /// <summary>Porcentaje de ajuste sobre el RetailPrice base. -10 = 10% mas barato, 15 = 15% mas caro.</summary>
    [Column(TypeName = "decimal(6,2)")]
    public decimal AdjustmentPercent { get; set; }

    /// <summary>Solo una lista puede ser default a la vez. Se usa para clientes sin lista asignada.</summary>
    public bool IsDefault { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    /// <summary>
    /// Empresas en las que se muestra esta lista. CSV (ej "FRIKAF,PALANICA").
    /// Vacio o null = visible en todas las empresas.
    /// </summary>
    [MaxLength(200)]
    public string? Companies { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Precio especial para un producto en una lista. Cuando existe una fila aca,
/// reemplaza el calculo automatico (RetailPrice * (1 + AdjustmentPercent/100)).
/// </summary>
[Table("ProductPriceOverrides")]
public class ProductPriceOverride
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int ProductId { get; set; }
    [ForeignKey(nameof(ProductId))]
    public Product? Product { get; set; }

    public int CustomerTierId { get; set; }
    [ForeignKey(nameof(CustomerTierId))]
    public CustomerTier? CustomerTier { get; set; }

    /// <summary>Precio sin IVA, mismo criterio que Products.RetailPrice.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Price { get; set; }

    [MaxLength(300)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
