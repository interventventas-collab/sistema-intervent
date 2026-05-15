using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_Productos")]
public class CafeProducto
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Nombre { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? Sku { get; set; }

    [MaxLength(100)]
    public string? Barcode { get; set; }

    [Required, MaxLength(20)]
    public string Categoria { get; set; } = "CAFE"; // CAFE | OTROS

    [MaxLength(100)]
    public string? Marca { get; set; }

    /// <summary>FK a Cafe_Marcas. Reemplaza progresivamente el campo de texto Marca.</summary>
    public int? MarcaId { get; set; }

    [ForeignKey(nameof(MarcaId))]
    public CafeMarca? MarcaNav { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Costo { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? PrecioPorKg { get; set; }

    /// <summary>PVP 1 — clientes BAR. Se guarda SIN IVA. El precio con IVA se calcula con IvaPct.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? Pvp1 { get; set; }

    /// <summary>PVP 2 — otros clientes. Se guarda SIN IVA. El precio con IVA se calcula con IvaPct.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? Pvp2 { get; set; }

    /// <summary>IVA % aplicable al producto. Default 21, opcional 10.5 para alimentos.</summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal IvaPct { get; set; } = 21m;

    /// <summary>Solo OTROS: % sobre costo para clientes BAR. NULL = BAR paga PVP (Pvp2).
    /// LEGACY: queda por compatibilidad con productos viejos, pero el modelo nuevo (PrecioBar/PrecioOtro)
    /// es el que se usa cuando están cargados.</summary>
    [Column(TypeName = "decimal(7,2)")]
    public decimal? BarPctSobreCosto { get; set; }

    /// <summary>SOLO productos categoría OTROS — modelo nuevo de precios directos.
    /// Precio sin IVA que paga un cliente tipo OTRO (consumidor final / venta por fuera).
    /// Si está cargado (no null), el motor de precios lo usa directo, ignorando la matriz
    /// Cafe_ReglasPrecios y la lógica de Pvp2. Si es null, cae al modelo legacy.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? PrecioOtro { get; set; }

    /// <summary>SOLO productos categoría OTROS — modelo nuevo de precios directos.
    /// Precio sin IVA que paga un cliente tipo BAR. Si está cargado, se usa directo.
    /// Si es null, cae al modelo legacy (PVP2 con matriz BAR -50% o costo×BarPct).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? PrecioBar { get; set; }

    /// <summary>Unidades por bulto (informativo, solo OTROS).</summary>
    public int? UxB { get; set; }

    /// <summary>Precio del bulto completo para clientes BAR. Si el cliente lleva >= UxB,
    /// el sistema le cobra (bultosCompletos × PrecioBulto + sueltas × PrecioBar) en vez de
    /// cantidad × PrecioBar. Permite descuento por volumen sin duplicar SKUs. Solo OTROS.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? PrecioBulto { get; set; }

    /// <summary>Precio del bulto completo para clientes OTRO. Misma lógica que PrecioBulto pero
    /// para tipoCliente != BAR.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? PrecioBultoOtro { get; set; }

    /// <summary>FK opcional al OEM origen del proveedor. 1 OEM puede alimentar a N variantes.</summary>
    public int? OemId { get; set; }

    [ForeignKey(nameof(OemId))]
    public CafeOem? OemNav { get; set; }

    [Column(TypeName = "decimal(18,3)")]
    public decimal StockGramos { get; set; }

    public int StockUnidades { get; set; }

    [MaxLength(500)]
    public string? Notas { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
