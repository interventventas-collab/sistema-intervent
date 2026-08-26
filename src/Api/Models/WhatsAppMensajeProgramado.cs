using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-08-26: un mensaje de WhatsApp agendado para salir MÁS TARDE.
///
/// Por qué la espera vive acá y no en la pantalla: si los minutos los contara el navegador,
/// se moriría al cerrar la pestaña y el mensaje no saldría nunca. Anotado en la base
/// sobrevive incluso a un reinicio del servidor. Mismo criterio que Cafe_VentasEnvios.
///
/// La fila hace de COLA y de HISTORIAL a la vez: nace PENDIENTE y termina ENVIADO (con la
/// hora real) o ERROR (con el motivo en castellano). CANCELADO = lo frenó una persona.
///
/// Nota de entornos: desarrollo y producción tienen bases separadas, así que cada uno manda
/// SOLO lo que se agendó en él. Nada se agenda solo: siempre lo programa un operador.
/// </summary>
[Table("WhatsApp_MensajesProgramados")]
public class WhatsAppMensajeProgramado
{
    public int Id { get; set; }

    /// <summary>Destino, en el mismo formato que usa el resto del chat (o "ig:{IGSID}").</summary>
    [Required, MaxLength(40)] public string Numero { get; set; } = "";

    /// <summary>Desde qué línea sale (phone_id de Meta). Se guarda al agendar para que el
    /// mensaje salga por la MISMA línea del chat donde se escribió, y no por la default.</summary>
    [MaxLength(40)] public string? LineaPhoneId { get; set; }

    /// <summary>TEXTO | ADJUNTO | PLANTILLA</summary>
    [Required, MaxLength(10)] public string Tipo { get; set; } = TipoTexto;

    /// <summary>El texto del mensaje. En ADJUNTO es el pie de foto (puede ir vacío).</summary>
    public string? Texto { get; set; }

    // ===== solo ADJUNTO =====
    /// <summary>URL pública /api/whatsapp/twilio/files/{token} que va a bajar Meta al enviar.</summary>
    [MaxLength(500)] public string? MediaUrl { get; set; }

    /// <summary>Nombre ORIGINAL del archivo. Sin él no se puede saber si va como foto o como
    /// documento: la URL /files/{token} no tiene extensión.</summary>
    [MaxLength(255)] public string? MediaFilename { get; set; }

    /// <summary>Fila de WhatsApp_TwilioUploads. Se guarda para poder estirarle el vencimiento:
    /// los adjuntos vencen a las 24 hs y un programado para más adelante llegaría a un archivo
    /// muerto (Meta no lo podría bajar y el envío fallaría).</summary>
    public int? UploadId { get; set; }

    // ===== solo PLANTILLA =====
    /// <summary>Nombre de la plantilla aprobada en Meta.</summary>
    [MaxLength(120)] public string? Plantilla { get; set; }

    /// <summary>Idioma de la plantilla (ej "es_AR").</summary>
    [MaxLength(20)] public string? Idioma { get; set; }

    /// <summary>Las variables de la plantilla, como lista JSON (["Juan","15:00"]).</summary>
    public string? VariablesJson { get; set; }

    /// <summary>Cuerpo ya armado, para mostrar en pantalla qué le va a llegar al cliente.</summary>
    public string? CuerpoPreview { get; set; }

    // ===== cuándo y cómo terminó =====
    /// <summary>UTC. Cuándo le toca salir.</summary>
    public DateTime ProgramadoPara { get; set; }

    /// <summary>PENDIENTE | ENVIADO | ERROR | CANCELADO</summary>
    [Required, MaxLength(12)] public string Estado { get; set; } = EstadoPendiente;

    /// <summary>UTC. Cuándo salió de verdad.</summary>
    public DateTime? EnviadoAt { get; set; }

    /// <summary>Por qué no salió, en castellano y listo para mostrarle al operador.</summary>
    [MaxLength(400)] public string? Error { get; set; }

    public int Intentos { get; set; }

    /// <summary>Id del mensaje que quedó en el chat una vez enviado (WhatsApp_TwilioMensajes).</summary>
    public int? MensajeId { get; set; }

    public int? CreadoPorUserId { get; set; }
    [MaxLength(120)] public string? CreadoPorNombre { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public const string TipoTexto = "TEXTO";
    public const string TipoAdjunto = "ADJUNTO";
    public const string TipoPlantilla = "PLANTILLA";

    public const string EstadoPendiente = "PENDIENTE";
    public const string EstadoEnviado = "ENVIADO";
    public const string EstadoError = "ERROR";
    public const string EstadoCancelado = "CANCELADO";
}
