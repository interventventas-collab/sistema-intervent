using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Stock_Operadores")]
public class StockOperador
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(120)]
    public string Nombre { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public int Orden { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("Stock_Movimientos")]
public class StockMovimiento
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int ProductoId { get; set; }

    [ForeignKey(nameof(ProductoId))]
    public CafeProducto? Producto { get; set; }

    public int? OperadorId { get; set; }

    [ForeignKey(nameof(OperadorId))]
    public StockOperador? Operador { get; set; }

    /// <summary>Snapshot del nombre del operador en el momento del movimiento (no lo borrás aunque
    /// se desactive el operador después).</summary>
    [MaxLength(120)]
    public string? OperadorNombreSnap { get; set; }

    /// <summary>FK opcional al depósito (Cafe_Depositos). Si null, era el depósito principal.</summary>
    public int? DepositoId { get; set; }

    [MaxLength(120)]
    public string? DepositoNombreSnap { get; set; }

    /// <summary>Tipo de movimiento. Casos:
    /// • SUMA / RESTA / SET → ajuste manual desde pantalla /stock-modificar (legacy)
    /// • VENTA_NUESTRA → venta cargada desde /cafe/ventas (descuenta stock)
    /// • VENTA_MELI → orden de MercadoLibre procesada por webhook/sync (descuenta stock)
    /// • AJUSTE_ADMIN → cambio desde pantalla admin de productos
    /// • SINCRO_CONTABILIUM → import nocturno o manual desde Contabilium
    /// • COMPRA_PROVEEDOR → entrada por compra registrada
    /// • CANCELACION → reversa por anulación de venta o devolución</summary>
    [Required, MaxLength(40)]
    public string TipoMov { get; set; } = "SUMA";

    /// <summary>Cantidad cargada por el operador (siempre positiva). Si TipoMov=RESTA, igual va positiva
    /// y el StockDespues = StockAntes - Cantidad. Si TipoMov=SET, StockDespues = Cantidad.</summary>
    public int Cantidad { get; set; }

    public int StockAntes { get; set; }
    public int StockDespues { get; set; }

    [MaxLength(500)]
    public string? Comentario { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
