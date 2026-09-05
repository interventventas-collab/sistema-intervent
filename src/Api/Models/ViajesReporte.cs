using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Un aviso que manda el repartidor desde su celular cuando algo de su cuenta no le cierra
/// ("me falta un viaje del jueves", "este pago no lo recibí").
///
/// Pedido del dueño el 05/09/2026: que Nacho pueda ver todo su detalle y, si ve un error,
/// avisar sin tener que llamar por teléfono.
/// </summary>
[Table("Viajes_Reportes")]
public class ViajesReporte
{
    [Key]
    public int Id { get; set; }

    public int EmpleadoId { get; set; }

    [Required, MaxLength(500)]
    public string Texto { get; set; } = string.Empty;

    /// <summary>NUEVO | VISTO</summary>
    [Required, MaxLength(10)]
    public string Estado { get; set; } = "NUEVO";

    /// <summary>Lo que le contestó el dueño (05/09/2026). El repartidor lo ve en su celu.</summary>
    [MaxLength(500)]
    public string? Respuesta { get; set; }

    public DateTime? RespuestaAt { get; set; }

    [MaxLength(100)]
    public string? RespuestaPor { get; set; }

    /// <summary>Cuándo leyó la respuesta. NULL = todavía no la vio: se le avisa con un cartel.</summary>
    public DateTime? RespuestaVistaAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? VistoAt { get; set; }

    [MaxLength(100)]
    public string? VistoPor { get; set; }

    [ForeignKey(nameof(EmpleadoId))]
    public ViajesEmpleado? Empleado { get; set; }
}
