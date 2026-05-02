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

    /// <summary>PVP alternativo (slot 2). Opcional. Si esta vacio y la regla de marca lo pide, cae a PVP 1.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? RetailPrice2 { get; set; }

    /// <summary>Porcentaje de markup sobre costo para calcular el PVP 3 (slot 3). Opcional.</summary>
    [Column(TypeName = "decimal(8,2)")]
    public decimal? Pvp3MarkupPercent { get; set; }

    /// <summary>
    /// Cantidad en stock. Si StockUnit = "unidad" (default), es entero conceptualmente.
    /// Si StockUnit = "kg", representa kg con decimales (ej: 108.5 kg).
    /// </summary>
    [Column(TypeName = "decimal(18,3)")]
    public decimal Stock { get; set; }

    /// <summary>
    /// Unidad del stock: "unidad" (default, productos contables) o "kg" (productos a granel).
    /// Cuando un padre tiene "kg", los hijos NO llevan stock propio: al venderlos, se descuenta
    /// del padre la cantidad = quantity * Fraction (ej: 1 paquete de 1/2 kg = 0.5 kg del padre).
    /// </summary>
    [MaxLength(10)]
    public string StockUnit { get; set; } = "unidad";

    public int CriticalStock { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    // Marca explicita "es producto base": independiente de si tiene derivados o no.
    // Setear a true cuando se carga desde la solapa "Productos base".
    public bool IsBase { get; set; } = false;

    // Producto base del que hereda costo y PVP. Si es null, es un producto independiente (o base).
    public int? BaseProductId { get; set; }

    [ForeignKey(nameof(BaseProductId))]
    public Product? BaseProduct { get; set; }

    public ICollection<Product> DerivedProducts { get; set; } = new List<Product>();

    // Marca asociada (clasificacion). Opcional.
    public int? BrandId { get; set; }

    [ForeignKey(nameof(BrandId))]
    public Brand? BrandNav { get; set; }

    // Si es true, se trata de un servicio (sin stock, infinitamente disponible).
    // Aparece en la pagina de Servicios, no en Productos.
    public bool IsService { get; set; } = false;

    // Unidades por bulto (UxB). Informativo, util para armar pedidos.
    public int? UnitsPerPack { get; set; }

    /// <summary>
    /// Fraccion del padre que representa este producto. Solo aplica a hijos (con BaseProductId).
    /// Default 1.0 (= mismo precio que el padre). Ej: cafe 1/2 kg => 0.5; cafe 1/4 kg => 0.25.
    /// Formula: precio_hijo = precio_padre * Fraction + MarkupAmount
    /// </summary>
    [Column(TypeName = "decimal(10,4)")]
    public decimal Fraction { get; set; } = 1.0m;

    /// <summary>
    /// Recargo fijo (en pesos, sin IVA) que se suma al precio proporcional del hijo.
    /// Sirve para cobrar el costo de fraccionar/envasar (ej: +$1.000 por cada paquete chico).
    /// Solo aplica a RetailPrice. CostPrice se propaga proporcional sin markup.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal MarkupAmount { get; set; } = 0m;

    public ICollection<MeliItem> MeliItems { get; set; } = new List<MeliItem>();
}
