using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-11-17: "Sumar tapas" en Nueva venta. Qué tapa va con cada vaso (un vaso puede tener varias:
/// negra / blanca, con pico / con respiradero). Los pares se cargan en init.sql por SKU.
/// </summary>
[Table("Cafe_ProductoTapas")]
public class CafeProductoTapa
{
    [Key]
    public int Id { get; set; }
    public int VasoProductoId { get; set; }
    public int TapaProductoId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
