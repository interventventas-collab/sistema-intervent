using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_Depositos")]
public class CafeDeposito
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Nombre { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Direccion { get; set; }

    [MaxLength(500)]
    public string? Notas { get; set; }

    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public int Orden { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

[Table("Cafe_StockPorDeposito")]
public class CafeStockPorDeposito
{
    [Key]
    public int Id { get; set; }

    public int ProductoId { get; set; }
    [ForeignKey(nameof(ProductoId))]
    public CafeProducto? Producto { get; set; }

    public int DepositoId { get; set; }
    [ForeignKey(nameof(DepositoId))]
    public CafeDeposito? Deposito { get; set; }

    [Column(TypeName = "decimal(18,3)")]
    public decimal StockGramos { get; set; }

    public int StockUnidades { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
