using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Historial de avisos ("bitácora"). Cada fila es UNA notificación concreta que el robot de
/// alertas emitió: un correo nuevo que entró, o una alerta (Shell/Banco/Cheque/Fecha) que se
/// disparó. A diferencia de Mis_Alertas (que guarda el ESTADO actual "está disparada sí/no"),
/// esta tabla es un registro histórico: cada aviso aparece de a uno, en orden, aunque la alerta
/// ya estuviera disparada por otro motivo.
///
/// Se guarda un "snapshot" del Tipo, Mensaje y Alcance (roles que la ven) para que el historial
/// sobreviva aunque después se edite o borre la alerta original. Por eso AlertaId no tiene FK.
///
/// Pedido del usuario 2026-07-13: "que cada notificación aparezca de a una, y en Telegram lo mismo".
/// </summary>
[Table("Mis_Alertas_Historial")]
public class MisAlertaHistorial
{
    public int Id { get; set; }

    /// <summary>Id de la alerta que lo originó (referencia suelta, sin FK: el historial no se borra
    /// si se borra la alerta).</summary>
    public int? AlertaId { get; set; }

    /// <summary>Snapshot del tipo (EMAIL_REMITENTE | SHELL_BAJO | BANCO_BAJO | CHEQUE_VENCE | FECHA_MES...).</summary>
    [Required, MaxLength(30)]
    public string Tipo { get; set; } = "";

    /// <summary>Snapshot del mensaje configurado en la alerta.</summary>
    [Required, MaxLength(300)]
    public string Mensaje { get; set; } = "";

    /// <summary>Detalle puntual del aviso (ej: "De María Contadora · 'prueba'" o "1 cheque por $8.500.000").</summary>
    [MaxLength(500)]
    public string? Detalle { get; set; }

    /// <summary>Snapshot del CSV de roles que ven la alerta (para filtrar el historial por rol).</summary>
    [Required, MaxLength(100)]
    public string Alcance { get; set; } = "admin,oficina";

    /// <summary>Solo para avisos de correo: el remitente del mail.</summary>
    [MaxLength(300)]
    public string? RemitenteEmail { get; set; }

    /// <summary>Solo para avisos de correo: link para abrir el mail en Gmail.</summary>
    [MaxLength(800)]
    public string? GmailLink { get; set; }

    /// <summary>La alerta pedía aviso por Telegram.</summary>
    public bool PorTelegram { get; set; }

    /// <summary>Se logró efectivamente mandar el Telegram (false si el bot estaba caído o sin vincular).</summary>
    public bool EnviadoTelegram { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
