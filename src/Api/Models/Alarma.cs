using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-08-26: una alarma del reloj. "A las 15:00 acordate de llamar a Qualitat".
///
/// DE QUIÉN ES CADA ALARMA (<see cref="Duenio"/>), que fue lo más discutido:
///   • Pantallas de admin (los tres hermanos comparten el usuario `admin` y `OFICINA`):
///     la alarma es de la PERSONA, identificada por el PIN → "op:OSMAR", "op:GERMAN"…
///     Así Osmar ve las suyas tanto en `admin` como en `OFICINA`, y no las de los otros.
///   • Depósito y Contadora: UNA sola lista por pantalla → "cuenta:DEPOSITO".
///     Ahí también hay PIN (Alexis, Walter…), pero el dueño pidió expresamente una sola
///     lista para el depósito: la alarma es del TURNO, no de la persona.
///
/// NO SE PIERDE: la alarma no la dispara ningún robot, la hace sonar el reloj de la pantalla
/// cuando el dueño está presente. Si a la hora no había nadie (o estaba firmado otro), la fila
/// queda PENDIENTE y suena apenas esa persona vuelve, avisando que era para más temprano.
/// Es a propósito: una alarma que se traga en silencio es peor que no tenerla.
/// </summary>
[Table("Reloj_Alarmas")]
public class Alarma
{
    public int Id { get; set; }

    /// <summary>"op:NOMBRE" (persona del PIN) o "cuenta:USUARIO" (pantalla compartida).</summary>
    [Required, MaxLength(60)] public string Duenio { get; set; } = "";

    /// <summary>UTC. Cuándo tiene que sonar. La pantalla la elige en hora argentina.</summary>
    public DateTime Cuando { get; set; }

    /// <summary>Qué hay que hacer. Es la mitad de la alarma: la hora sin la nota no sirve.</summary>
    [MaxLength(300)] public string? Nota { get; set; }

    /// <summary>Clave del sonido (las mismas de window.waSounds del chat: despertador, chime…).</summary>
    [MaxLength(40)] public string Sonido { get; set; } = "despertador";

    /// <summary>PENDIENTE | APAGADA</summary>
    [Required, MaxLength(12)] public string Estado { get; set; } = EstadoPendiente;

    /// <summary>UTC. Cuándo la apagó una persona (sonó y la atendieron, o la cancelaron).</summary>
    public DateTime? ApagadaAt { get; set; }

    /// <summary>Nombre lindo de quien la dejó puesta, para mostrar en pantalla.</summary>
    [MaxLength(60)] public string? CreadaPor { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public const string EstadoPendiente = "PENDIENTE";
    public const string EstadoApagada = "APAGADA";
}
