using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-11-04: "Asignar vencimiento". Una cantidad del producto con su fecha de vencimiento.
/// Lo que QUEDA de cada fecha no se guarda: lo calcula <see cref="Api.Services.VencimientosService"/>
/// con el stock actual, suponiendo que siempre se saca primero lo que vence antes.
/// Café: Cantidad en kg (su stock está en gramos).
/// </summary>
[Table("Cafe_ProductoVencimientos")]
public class CafeProductoVencimiento
{
    [Key]
    public int Id { get; set; }

    public int ProductoId { get; set; }

    [Column(TypeName = "decimal(18,3)")]
    public decimal Cantidad { get; set; }

    [Column(TypeName = "date")]
    public DateTime FechaVence { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(200)]
    public string? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
