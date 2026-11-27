using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-11-26: qué producto está guardado en cada estante de un rack del plano
/// (rack + posición + nivel). Cantidad es texto libre ("12 cajas").</summary>
[Table("Deposito_EstanteProductos")]
public class DepositoEstanteProducto
{
    [Key]
    public int Id { get; set; }

    public int ElementoId { get; set; }
    /// <summary>2026-11-27: de qué lado del rack (1 o 2, ver DepositoPlanoElemento.Calle1).</summary>
    public int Cara { get; set; } = 1;
    public int Posicion { get; set; }
    public int Nivel { get; set; }
    public int ProductoId { get; set; }

    [MaxLength(50)]
    public string? Cantidad { get; set; }

    [MaxLength(200)]
    public string? Nota { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string? Por { get; set; }
}
