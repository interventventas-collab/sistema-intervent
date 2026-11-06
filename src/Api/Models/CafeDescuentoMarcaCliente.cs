using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-11-06 — Descuento fijo de un CLIENTE con una MARCA (pedido de Osmar: "que Dulce Lugar
/// tenga siempre un X% con Cremuccino"). Se carga en Ventas → Precios y descuentos especiales.
///
/// Una fila con ProductoId NULL = vale para TODA la marca. Si no, hay una fila por cada
/// producto elegido de esa marca (todas con el mismo %). La tabla la crea la API sola
/// (CafeDescuentosMarcaController.AsegurarTablaAsync), no init.sql.
/// </summary>
[Table("Cafe_DescuentosMarcaCliente")]
public class CafeDescuentoMarcaCliente
{
    [Key]
    public int Id { get; set; }
    public int ClienteId { get; set; }
    public int MarcaId { get; set; }
    public int? ProductoId { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal DescuentoPct { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string? CreatedBy { get; set; }
}
