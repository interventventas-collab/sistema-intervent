using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-06-12: feriados nacionales/locales que NO se cuentan como horas esperadas.
/// Si un empleado no carga horario ese día → no se contabiliza como "falta".
/// Si carga horario → todas las horas trabajadas son extras (feriado se paga doble en gral).
/// </summary>
[Table("HorasExtras_Feriados")]
public class HorasExtrasFeriado
{
    public int Id { get; set; }

    [Required]
    public DateTime Fecha { get; set; }

    [Required, MaxLength(120)]
    public string Descripcion { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
