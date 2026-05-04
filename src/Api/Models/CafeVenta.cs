using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_Ventas")]
public class CafeVenta
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(20)]
    public string Numero { get; set; } = string.Empty;

    public DateTime Fecha { get; set; }

    public int? ClienteId { get; set; }

    [ForeignKey(nameof(ClienteId))]
    public CafeCliente? ClienteNav { get; set; }

    [MaxLength(200)]
    public string? ClienteNombreSnapshot { get; set; }

    [MaxLength(20)]
    public string? ClienteTipoSnapshot { get; set; } // BAR | OTRO

    [Column(TypeName = "decimal(18,2)")] public decimal Subtotal { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Descuento { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Total { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal CostoTotal { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Margen { get; set; }

    [MaxLength(500)]
    public string? Observaciones { get; set; }

    [MaxLength(20)]
    public string Estado { get; set; } = "emitido"; // emitido | anulado

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<CafeVentaItem> Items { get; set; } = new List<CafeVentaItem>();
}

[Table("Cafe_VentaItems")]
public class CafeVentaItem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int VentaId { get; set; }

    [ForeignKey(nameof(VentaId))]
    public CafeVenta? VentaNav { get; set; }

    public int ProductoId { get; set; }

    [ForeignKey(nameof(ProductoId))]
    public CafeProducto? ProductoNav { get; set; }

    [Required, MaxLength(200)]
    public string ProductoNombreSnapshot { get; set; } = string.Empty;

    [Required, MaxLength(20)]
    public string Categoria { get; set; } = "CAFE"; // CAFE | OTROS

    [Required, MaxLength(20)]
    public string Formato { get; set; } = "1KG"; // 1KG | MEDIO | CUARTO | UNIT

    public int Cantidad { get; set; }

    [Column(TypeName = "decimal(18,2)")] public decimal PrecioUnitario { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal CostoUnitario { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Subtotal { get; set; }
    [Column(TypeName = "decimal(18,3)")] public decimal GramosDescontados { get; set; }
}
