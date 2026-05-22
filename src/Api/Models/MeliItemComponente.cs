using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Mapeo "publicación MeLi → productos sueltos del sistema". Sirve para que cuando entra
/// una orden de un MeliItem que es un COMBO en MeLi (ej: COL1000-BR-1000 = 1 × FR2 + 1 × FR4),
/// el sistema sepa qué productos sueltos descontar y cuántos.
///
/// Los combos NUNCA viven en CafeProductos — solo en esta tabla intermedia. Decision del
/// usuario 2026-05-20: el sistema solo maneja productos puros, sin combos.
///
/// Para items MeLi que mapean a 1 solo producto suelto (sin combo), se guarda 1 fila con
/// Cantidad=1 (o Cantidad=N si MeLi vende packs hechos en MeLi como X6, etc).
/// </summary>
[Table("MeliItemComponentes")]
public class MeliItemComponente
{
    [Key]
    public int Id { get; set; }

    /// <summary>El MeliItemId (string MLA-xxx) de la publicación MeLi.</summary>
    [Required]
    [MaxLength(50)]
    public string MeliItemId { get; set; } = "";

    /// <summary>VariationId de la publicacion (cuando es multi-variante: ej colores, tamaños).
    /// Null = publicacion sin variantes O componente que aplica a TODAS las variantes (legacy).
    /// Agregado 2026-05-22 para arreglar el bug de variantes (publicaciones multi-color que
    /// descontaban TODOS los colores en vez de la variante vendida).</summary>
    [MaxLength(40)]
    public string? MeliVariationId { get; set; }

    /// <summary>Id del producto suelto en CafeProductos al que se descuenta stock.</summary>
    public int CafeProductoId { get; set; }
    [ForeignKey(nameof(CafeProductoId))]
    public CafeProducto? Producto { get; set; }

    /// <summary>Cuántas unidades del producto se descuentan por cada unidad vendida del MeliItem.</summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal Cantidad { get; set; } = 1m;

    /// <summary>Formato (solo para café): 1KG / MEDIO / CUARTO. Null = no aplica (productos OTROS).</summary>
    [MaxLength(20)]
    public string? Formato { get; set; }

    /// <summary>De dónde salió este linkeo: 'manual', 'meli-sku-directo', 'combo-contabilium', 'cafe-pattern'.</summary>
    [MaxLength(40)]
    public string? Source { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Snapshot diario del stock que reporta Contabilium para los productos importados.
/// Se actualiza nightly por un job, y la pantalla /cafe/stock-comparado lo usa para mostrar
/// la diferencia entre el stock del sistema y el de Contabilium.
/// </summary>
[Table("StockSnapshots")]
public class StockSnapshot
{
    [Key]
    public int Id { get; set; }

    /// <summary>SKU del producto (= CafeProducto.Sku, normalizado upper).</summary>
    [Required]
    [MaxLength(100)]
    public string Sku { get; set; } = "";

    [Required]
    public DateTime Fecha { get; set; }

    /// <summary>Stock que reporta Contabilium en la fecha del snapshot.</summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal StockContabilium { get; set; }

    /// <summary>Source = 'contabilium-api', 'manual', etc.</summary>
    [MaxLength(40)]
    public string? Source { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
