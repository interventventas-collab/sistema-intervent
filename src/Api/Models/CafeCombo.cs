using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_Combos")]
public class CafeCombo
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Nombre { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Descripcion { get; set; }

    public bool IsActive { get; set; } = true;

    // === Columnas agregadas 2026-05-22 para clone de Contabilium ===
    // Permiten identificar combos importados desde Contabilium por SKU.
    // Los combos legacy (promos de cafe fraccionado) dejan estos campos en null.

    /// <summary>SKU del combo (solo combos importados desde Contabilium). Null = combo manual legacy.</summary>
    [MaxLength(80)]
    public string? Sku { get; set; }

    [MaxLength(80)]
    public string? Marca { get; set; }

    /// <summary>CAFE | OTROS. Default OTROS (combos importados).</summary>
    [Required, MaxLength(40)]
    public string Categoria { get; set; } = "OTROS";

    /// <summary>Precio de referencia (solo informativo, los combos del clone se cobran via expansion a productos).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal PrecioReferencia { get; set; }

    /// <summary>Ej: "CONTABILIUM_CLONE_2026_05_22". Null = combo manual.</summary>
    [MaxLength(80)]
    public string? ImportSource { get; set; }

    public string? Notas { get; set; }

    /// <summary>2026-06-01: si es true, este "combo" se vende como un Producto Compuesto.
    /// Aparece también en la pestaña "Producto" del buscador de venta (no solo en "Combo"),
    /// para acelerar la venta de items individuales armados (cesto C9172NEG = recipiente + tapa).
    /// Internamente el comportamiento es el mismo que combo: se expande en sus items al agregar.</summary>
    public bool EsCompuesto { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<CafeComboItem> Items { get; set; } = new List<CafeComboItem>();
}

[Table("Cafe_ComboItems")]
public class CafeComboItem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int ComboId { get; set; }

    [ForeignKey(nameof(ComboId))]
    public CafeCombo? ComboNav { get; set; }

    public int ProductoId { get; set; }

    [ForeignKey(nameof(ProductoId))]
    public CafeProducto? ProductoNav { get; set; }

    [Required, MaxLength(20)]
    public string Formato { get; set; } = "1KG";

    public int Cantidad { get; set; } = 1;

    [MaxLength(30)]
    public string? Molienda { get; set; }

    public bool EsDoyPack { get; set; }

    /// <summary>Si el producto del combo va en envase plateado. Default false = envase negro.</summary>
    public bool EsEnvasePlateado { get; set; }

    public int SortOrder { get; set; }
}
