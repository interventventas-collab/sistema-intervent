using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-09-02 — La lista de "para pedir". Cuando el stock de un producto cruza por
/// DEBAJO de su StockIdeal, se le anota un renglon aca y QUEDA ANOTADO hasta que alguien lo
/// marque como pedido. A proposito NO se borra solo cuando vuelve a entrar stock: el caso real
/// es que entran 5 unidades sueltas, el producto se cae de la lista de faltantes del momento,
/// y nadie se acuerda de pedirlo. Mientras esta PENDIENTE, deposito y oficina lo ven.
///
/// El enganche lo hace StockFaltantesBackgroundService (cada 10 min) y tambien la pantalla
/// al entrar. Un producto no puede tener dos renglones PENDIENTE a la vez (indice unico filtrado).</summary>
[Table("Cafe_StockFaltantes")]
public class CafeStockFaltante
{
    public int Id { get; set; }

    public int ProductoId { get; set; }

    [ForeignKey(nameof(ProductoId))]
    public CafeProducto? Producto { get; set; }

    /// <summary>Cuando se detecto que quedo por debajo del ideal (UTC).</summary>
    public DateTime DetectadoAt { get; set; } = DateTime.UtcNow;

    /// <summary>Foto del momento en que entro a la lista: cuanto habia y cuanto se queria tener.
    /// Sirve para ver despues "cuando lo anotamos tenia 3 y ahora tiene 5".
    /// En unidades, salvo el cafe que va en kilos (ver StockIdealController).</summary>
    [Column(TypeName = "decimal(18,3)")]
    public decimal StockAlDetectar { get; set; }

    public int IdealAlDetectar { get; set; }

    /// <summary>PENDIENTE = falta pedirlo · PEDIDO = ya se pidio · DESCARTADO = lo sacaron a mano.</summary>
    [MaxLength(20)]
    public string Estado { get; set; } = "PENDIENTE";

    /// <summary>Cuando lo marcaron como pedido (o descartado). Null si sigue pendiente.</summary>
    public DateTime? ResueltoAt { get; set; }

    /// <summary>Quien lo marco (nombre del usuario que estaba logueado).</summary>
    [MaxLength(120)]
    public string? ResueltoPor { get; set; }

    /// <summary>Cuanto se pidio finalmente, si lo anotaron. Opcional.</summary>
    [Column(TypeName = "decimal(18,3)")]
    public decimal? CantidadPedida { get; set; }
}
