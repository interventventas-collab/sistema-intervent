using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-09-17 — Una entrada al sistema: "el 17/09 a las 16:40 esta persona entró desde esta compu".
///
/// POR QUÉ EXISTE, además de <see cref="UserSession"/>. La pantalla muestra APARATOS, un renglón por
/// compu o celu, para no llenarse de renglones repetidos de la misma máquina. Pero el dueño preguntó
/// justamente lo contrario: "si entro 5 veces, ¿aparecen mis 5 entradas?". Con la tabla de sesiones
/// sola, no: las 5 entradas pisaban el mismo renglón y no quedaba rastro de las anteriores.
///
/// Entonces son dos cosas distintas y las dos hacen falta:
///   • Users_Sesiones        → el estado de AHORA: quién está adentro y desde qué aparato.
///   • Users_SesionesEntradas → la HISTORIA: cada vez que alguien puso usuario y clave (o el dedo).
///
/// Se guarda el nombre además del vínculo con la sesión: si mañana se borra el renglón del aparato,
/// la historia tiene que seguir siendo legible por sí sola.
/// </summary>
[Table("Users_SesionesEntradas")]
public class UserSessionEntrada
{
    public int Id { get; set; }

    /// <summary>El aparato desde el que entró (fila de Users_Sesiones).</summary>
    public int SesionId { get; set; }

    [Required, MaxLength(100)] public string Nombre { get; set; } = "";

    /// <summary>UTC. Cuándo entró.</summary>
    public DateTime CuandoAt { get; set; } = DateTime.UtcNow;

    [MaxLength(60)] public string? Ip { get; set; }

    /// <summary>WEB = usuario y clave · HUELLA = tocó el dedo en el celu.</summary>
    [Required, MaxLength(10)] public string Tipo { get; set; } = UserSession.TipoWeb;
}
