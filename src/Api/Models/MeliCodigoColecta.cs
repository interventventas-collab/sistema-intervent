using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-07-17: Código de autorización del día para colectas o devoluciones de MercadoLibre.
/// MeLi manda un mail todas las mañanas (asunto "Código de autorización del día para colectas o
/// devoluciones") con un código alfanumérico que cambia cada 24 hs y que el transporte pide cuando
/// viene a buscar los paquetes (colecta) o a traer una devolución.
///
/// El robot MeliCodigoColectaBackgroundService lee la casilla (IMAP, solo lectura), saca el código
/// y guarda UNA fila por día. La card del Dashboard muestra el más reciente y el bot de Telegram lo
/// avisa una sola vez por día (EnviadoTelegram evita repetir).
/// </summary>
[Table("Meli_CodigoColecta")]
public class MeliCodigoColecta
{
    public int Id { get; set; }

    /// <summary>El código en sí (ej: "DF54B074"). Alfanumérico, 6-12 caracteres.</summary>
    [Required, MaxLength(20)]
    public string Codigo { get; set; } = "";

    /// <summary>Día (hora Argentina) al que corresponde el código. Una fila por día.</summary>
    public DateTime FechaCodigo { get; set; }

    /// <summary>Cuándo llegó el mail de MeLi (fecha interna del correo, UTC).</summary>
    public DateTime? FechaMail { get; set; }

    /// <summary>Message-Id del mail, por si hace falta rastrearlo.</summary>
    [MaxLength(400)]
    public string? MessageId { get; set; }

    /// <summary>Horario (franja) de la colecta de ese día, ej "17 a 19 hs". MeLi lo manda en varios
    /// mails distintos y lo cambia; guardamos el del mail más reciente que aplica a ese día.</summary>
    [MaxLength(60)]
    public string? HorarioColecta { get; set; }

    /// <summary>La colecta del día quedó cancelada (mail "No podremos recolectar…").</summary>
    public bool ColectaCancelada { get; set; }

    /// <summary>Fecha del mail que fijó el horario (para que el más nuevo pise al viejo).</summary>
    public DateTime? HorarioMailAt { get; set; }

    /// <summary>Firma del último horario/cancelación avisado por Telegram (ej "17 a 19 hs" o
    /// "CANCELADA"). Sirve para no repetir el aviso si el horario no cambió.</summary>
    [MaxLength(80)]
    public string? HorarioAvisado { get; set; }

    /// <summary>Ya se avisó por Telegram (para no repetir el aviso del mismo día).</summary>
    public bool EnviadoTelegram { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
