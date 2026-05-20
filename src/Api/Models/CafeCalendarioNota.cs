using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Notas/eventos manuales que el usuario agrega al calendario del dashboard (sueldos,
/// comisiones, vencimientos, recordatorios, etc). Pedido 2026-05-19.
///
/// Esto NO reemplaza los eventos automaticos (cheques por pagar, cuotas) — esos vienen
/// de sus tablas correspondientes. Esta tabla solo guarda lo que el usuario tipea a mano.
/// </summary>
[Table("Cafe_CalendarioNotas")]
public class CafeCalendarioNota
{
    public int Id { get; set; }

    [Column(TypeName = "date")]
    public DateTime Fecha { get; set; }

    [Required, MaxLength(150)]
    public string Titulo { get; set; } = "";

    [MaxLength(500)]
    public string? Descripcion { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? Importe { get; set; }

    /// <summary>Color hex opcional para visualizacion (#FFAA00). Si null, usa el default.</summary>
    [MaxLength(20)]
    public string? Color { get; set; }

    [MaxLength(120)]
    public string? CreadoPor { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
