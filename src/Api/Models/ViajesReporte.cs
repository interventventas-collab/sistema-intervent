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

    // ── 19/10/2026: "pedir que me sumen una entrega" ─────────────────────────────────────
    // El repartidor pide desde el celu que le sumen una entrega que el mapa no contó. No suma
    // nada hasta que alguien de la oficina la aprueba: recién ahí se crea la entrega a mano.

    /// <summary>NULL = aviso común · "SUMA" = pide que le sumen una entrega.</summary>
    [MaxLength(10)]
    public string? Tipo { get; set; }

    /// <summary>Día de la entrega que pide sumar.</summary>
    [Column(TypeName = "date")]
    public DateTime? SumaFecha { get; set; }

    /// <summary>A quién / dónde fue (lo escribe él).</summary>
    [MaxLength(150)]
    public string? SumaCliente { get; set; }

    /// <summary>NULL = esperando · true = aprobada (ya suma) · false = rechazada.</summary>
    public bool? SumaAprobada { get; set; }

    /// <summary>La entrega a mano que se creó al aprobarla (Viajes_Entregas.Id).</summary>
    public int? SumaEntregaId { get; set; }

    [ForeignKey(nameof(EmpleadoId))]
    public ViajesEmpleado? Empleado { get; set; }
}
