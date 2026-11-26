using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-11-26: el tostadero tocó "Café listo" en la tablet (/tostadero) para un pedido.
/// Tipo = "VENTA" (Ref = CafeVentas.Id) o "MELI" (Ref = número de envío / pack / orden, el mismo
/// "numero" que usa /api/meli-deposito/ordenes). El depósito lo ve como "☕ Café listo" en su tarjeta.
/// Si lo desmarcan, se borra la fila.
/// </summary>
[Table("Tostadero_CafeListo")]
public class TostaderoCafeListo
{
    public int Id { get; set; }
    [MaxLength(10)] public string Tipo { get; set; } = "";
    public long Ref { get; set; }
    public DateTime ListoAt { get; set; } = DateTime.UtcNow;
    [MaxLength(60)] public string? Quien { get; set; }
}
