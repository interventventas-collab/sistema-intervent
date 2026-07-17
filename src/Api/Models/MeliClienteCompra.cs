using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-07-17: Una fila por VENTA de un cliente MeLi (historial permanente de "que compro").
/// Se arma agrupando MeliOrders por MeliOrderId (una venta con varios productos son varias filas
/// en MeliOrders pero UNA sola compra aca). Dedup por MeliOrderId.
/// </summary>
[Table("MeliClienteCompras")]
public class MeliClienteCompra
{
    [Key]
    public int Id { get; set; }

    public int MeliClienteId { get; set; }
    [ForeignKey(nameof(MeliClienteId))]
    public MeliCliente? Cliente { get; set; }

    public long BuyerId { get; set; }
    /// <summary>Numero de venta de MeLi (unico en esta tabla).</summary>
    public long MeliOrderId { get; set; }

    public DateTime? Fecha { get; set; }
    [MaxLength(500)] public string? Items { get; set; }
    public int Cantidad { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Total { get; set; }

    /// <summary>Flex | ME1 | Correo — de donde salio (y si por eso tenemos o no telefono).</summary>
    [MaxLength(20)] public string? Canal { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
