using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Deposito / sucursal / lugar fisico donde se guarda mercaderia.
/// Por ahora la cantidad real por deposito todavia no se separa: se asume que
/// el campo Products.Stock representa el stock total (en el deposito default,
/// que es "9 de abril"). Se usa principalmente para etiquetar los movimientos
/// de stock y permitir reportes por deposito.
/// </summary>
[Table("Warehouses")]
public class Warehouse
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(30)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Address { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Cada movimiento de stock manual (ajuste, ingreso, rotura, merma, etc).
/// Sirve como audit log: que se cambio, cuanto, por que, quien y cuando.
/// </summary>
[Table("StockMovements")]
public class StockMovement
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int ProductId { get; set; }
    [ForeignKey(nameof(ProductId))]
    public Product? Product { get; set; }

    public int WarehouseId { get; set; }
    [ForeignKey(nameof(WarehouseId))]
    public Warehouse? Warehouse { get; set; }

    /// <summary>'ingreso' | 'egreso' | 'ajuste' | 'rotura' | 'merma' | 'devolucion' | 'conteo' | 'otro'</summary>
    [Required]
    [MaxLength(30)]
    public string MovementType { get; set; } = "ajuste";

    /// <summary>Positivo (entra al stock) o negativo (sale del stock).</summary>
    public int DeltaQuantity { get; set; }

    public int StockBefore { get; set; }
    public int StockAfter { get; set; }

    [MaxLength(150)]
    public string? Reason { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    [MaxLength(100)]
    public string? OperatorName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
