using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Correo detectado por una alerta de tipo EMAIL_REMITENTE. El robot guarda acá los datos de
/// cada mail sin leer que dispara una alerta, para mostrarlos en la card "Correos importantes"
/// del Dashboard (remitente, asunto, adelanto del texto, si tiene adjuntos, link a Gmail).
///
/// Se mantiene en sincronía con la bandeja: si el mail se lee en Gmail (deja de estar "no leído"),
/// el robot borra su fila. Clave de deduplicación: MessageId (Message-ID del correo).
///
/// Pedido del usuario 2026-07-10.
/// </summary>
[Table("Mis_Alertas_Correos")]
public class MisAlertaCorreo
{
    public int Id { get; set; }

    public int AlertaId { get; set; }
    [ForeignKey(nameof(AlertaId))]
    public MisAlerta? Alerta { get; set; }

    /// <summary>Message-ID del correo (sin &lt; &gt;). Clave para no duplicar.</summary>
    [Required, MaxLength(400)]
    public string MessageId { get; set; } = "";

    [MaxLength(300)]
    public string? Remitente { get; set; }

    [MaxLength(300)]
    public string? RemitenteEmail { get; set; }

    [MaxLength(500)]
    public string? Asunto { get; set; }

    /// <summary>Adelanto del cuerpo (texto plano, recortado).</summary>
    [MaxLength(1000)]
    public string? Adelanto { get; set; }

    public DateTime? FechaRecibido { get; set; }

    public bool TieneAdjuntos { get; set; }
    /// <summary>Nombres de los adjuntos separados por coma (recortado).</summary>
    [MaxLength(500)]
    public string? Adjuntos { get; set; }

    /// <summary>Link para abrir el mail directo en Gmail.</summary>
    [MaxLength(800)]
    public string? GmailLink { get; set; }

    public DateTime DetectadoAt { get; set; } = DateTime.UtcNow;
}
