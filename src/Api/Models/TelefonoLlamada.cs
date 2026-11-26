using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-11-26: registro de llamadas de la central telefonica (carpeta telefonia/).
/// La central avisa al cortar cada llamada (POST /api/telefono/registro). Sirve para
/// las "llamadas perdidas" del boton 📞: Estado distinto de ANSWER y Vista = false.
/// Estado = DIALSTATUS de Asterisk: ANSWER, NOANSWER, CANCEL, BUSY, NOCONTACT (no conectado)...
/// </summary>
[Table("Telefono_Llamadas")]
public class TelefonoLlamada
{
    public int Id { get; set; }
    [MaxLength(10)] public string De { get; set; } = "";
    [MaxLength(60)] public string? DeNombre { get; set; }
    [MaxLength(10)] public string A { get; set; } = "";
    [MaxLength(30)] public string? Busca { get; set; }
    [MaxLength(20)] public string Estado { get; set; } = "";
    public int Segundos { get; set; }
    public DateTime FechaUtc { get; set; } = DateTime.UtcNow;
    public bool Vista { get; set; }
}

/// <summary>
/// 2026-11-26: mensajes programados: a la hora indicada el sistema llama al interno
/// (o a todos los conectados) y lee el texto en voz alta (TelefonoMensajesBackgroundService).
/// Dias: letras L M X J V S D de los dias que se repite; vacio = una sola vez en Fecha.
/// Hora: "HH:mm" hora argentina.
/// </summary>
[Table("Telefono_Mensajes")]
public class TelefonoMensaje
{
    public int Id { get; set; }
    [MaxLength(500)] public string Texto { get; set; } = "";
    [MaxLength(10)] public string Destino { get; set; } = "";   // 201, 210, 101... o "todos"
    [MaxLength(5)] public string Hora { get; set; } = "09:00";
    [MaxLength(7)] public string Dias { get; set; } = "";
    public DateTime? Fecha { get; set; }
    public bool Activo { get; set; } = true;
    public DateTime? UltimoEnvioAr { get; set; }
    public DateTime? UltimoEscuchadoAr { get; set; }
    [MaxLength(50)] public string? CreadoPor { get; set; }
    public DateTime CreadoUtc { get; set; } = DateTime.UtcNow;
}
