using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_Compras")]
public class CafeCompra
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(20)]
    public string Numero { get; set; } = string.Empty;  // COMPRA-AAAA-NNNN (interno)

    public int? ProveedorId { get; set; }

    [ForeignKey(nameof(ProveedorId))]
    public CafeProveedor? ProveedorNav { get; set; }

    /// <summary>Snapshot del nombre del proveedor al momento de la compra. Aunque el proveedor
    /// cambie de nombre o se elimine, la compra mantiene el nombre original.</summary>
    [MaxLength(200)]
    public string? ProveedorNombreSnapshot { get; set; }

    public DateTime Fecha { get; set; }

    /// <summary>Numero de comprobante del proveedor (factura/remito). Texto libre, opcional.</summary>
    [MaxLength(50)]
    public string? NumeroComprobante { get; set; }

    /// <summary>Estados: BORRADOR, CONFIRMADA, PAGADA, ANULADA.</summary>
    [Required, MaxLength(20)]
    public string Estado { get; set; } = "BORRADOR";

    [Column(TypeName = "decimal(18,2)")]
    public decimal Total { get; set; }

    [MaxLength(500)]
    public string? Observaciones { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public DateTime? ConfirmadaAt { get; set; }
    public DateTime? PagadaAt { get; set; }
    public DateTime? AnuladaAt { get; set; }

    public ICollection<CafeCompraItem> Items { get; set; } = new List<CafeCompraItem>();
}

[Table("Cafe_CompraItems")]
public class CafeCompraItem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int CompraId { get; set; }

    [ForeignKey(nameof(CompraId))]
    public CafeCompra? CompraNav { get; set; }

    public int ProductoId { get; set; }

    [ForeignKey(nameof(ProductoId))]
    public CafeProducto? ProductoNav { get; set; }

    [Required, MaxLength(200)]
    public string ProductoNombreSnapshot { get; set; } = string.Empty;

    [Required, MaxLength(20)]
    public string Categoria { get; set; } = "OTROS";  // CAFE | OTROS — define como se interpreta Cantidad

    /// <summary>Para CAFE: kilogramos. Para OTROS: unidades. Decimal para permitir 0.5 kg, etc.</summary>
    [Column(TypeName = "decimal(18,3)")]
    public decimal Cantidad { get; set; }

    /// <summary>Para CAFE: $ por kg. Para OTROS: $ por unidad.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal CostoUnitario { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Subtotal { get; set; }
}
