using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

// Producto compuesto / kit (BOM). Stock virtual = MIN(stock componente / cantidad).
// Vender un kit descuenta stock de cada componente, en una transaccion.
// Distinto de CafeCombo (que son promos de cafe fraccionado).
[Table("Cafe_Kits")]
public class CafeKit
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Sku { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Nombre { get; set; } = string.Empty;

    public string? Descripcion { get; set; }

    [Required, MaxLength(20)]
    public string Categoria { get; set; } = "OTROS"; // CAFE | OTROS

    [MaxLength(100)]
    public string? Marca { get; set; } // texto legacy

    public int? MarcaId { get; set; }

    [ForeignKey(nameof(MarcaId))]
    public CafeMarca? MarcaNav { get; set; }

    [Column(TypeName = "decimal(18,2)")] public decimal? Pvp1 { get; set; } // BARES (sin IVA)
    [Column(TypeName = "decimal(18,2)")] public decimal? Pvp2 { get; set; } // SUGERIDO (sin IVA)

    [Column(TypeName = "decimal(5,2)")]
    public decimal IvaPct { get; set; } = 21m;

    [MaxLength(500)]
    public string? Notas { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<CafeKitItem> Items { get; set; } = new List<CafeKitItem>();
}

[Table("Cafe_KitItems")]
public class CafeKitItem
{
    [Key]
    public int Id { get; set; }

    public int KitId { get; set; }

    [ForeignKey(nameof(KitId))]
    public CafeKit? Kit { get; set; }

    public int ProductoId { get; set; }

    [ForeignKey(nameof(ProductoId))]
    public CafeProducto? Producto { get; set; }

    [Column(TypeName = "decimal(18,3)")]
    public decimal Cantidad { get; set; } = 1m;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
