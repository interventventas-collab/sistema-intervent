using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 27/10/2026: historial de una venta (pedido del dueño: "que se sepa quién la entregó de verdad,
/// qué se modificó y quién"). Lo escribe solo AppDbContext al guardar (VentaHistorialRecorder):
/// cambios de la venta y sus renglones, a quién se asignó / se pasó, entregas, anular/recuperar.
/// Lo que ya tenía su propia tabla (armado, cobranzas, envíos) se sigue leyendo de ahí.
/// Sin FK a Cafe_Ventas a propósito: si la venta se borra, su historial no se pierde.
/// </summary>
[Table("Cafe_VentaHistorial")]
public class CafeVentaHistorial
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int VentaId { get; set; }

    /// <summary>EDICION | REPARTO | ENTREGA | ESTADO</summary>
    [Required, MaxLength(20)]
    public string Tipo { get; set; } = "";

    /// <summary>Operador de la compu (OSMAR), repartidor del link (german) o "sistema".</summary>
    [MaxLength(100)]
    public string? Quien { get; set; }

    /// <summary>compu | celu | mapeo | sistema</summary>
    [MaxLength(20)]
    public string? Desde { get; set; }

    /// <summary>Texto para leer. Si hubo varios cambios en el mismo guardado, uno por renglón.</summary>
    [Required, MaxLength(2000)]
    public string Detalle { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
