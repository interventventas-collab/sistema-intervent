using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Horario del respondedor automático, una fila por día de la semana.
/// DayOfWeek usa la convención de .NET: 0=Domingo, 1=Lunes ... 6=Sábado.
/// El horario se interpreta en hora de Argentina (UTC-3).
/// Si la franja cruza la medianoche (ej. 21:00 a 06:00) cubre hasta la mañana siguiente.
/// </summary>
[Table("MeliAutoReplySchedule")]
public class MeliAutoReplySchedule
{
    /// <summary>0=Domingo .. 6=Sábado (System.DayOfWeek).</summary>
    [Key]
    public int DayOfWeek { get; set; }

    /// <summary>Si está apagado, ese día el robot no responde nunca.</summary>
    public bool IsActive { get; set; }

    /// <summary>Si está prendido, responde todo el día (ignora StartTime/EndTime).</summary>
    public bool AllDay { get; set; }

    /// <summary>Hora de inicio de la franja, formato "HH:mm" (hora Argentina).</summary>
    [MaxLength(5)]
    public string StartTime { get; set; } = "21:00";

    /// <summary>Hora de fin de la franja, formato "HH:mm" (hora Argentina).</summary>
    [MaxLength(5)]
    public string EndTime { get; set; } = "06:00";
}
