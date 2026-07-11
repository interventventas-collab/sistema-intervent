using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Bot de Telegram para avisos al celular (y consultas simples desde el celu).
/// El usuario crea un bot con @BotFather, pega el "token" acá, y el sistema con eso:
///   - le manda avisos (ventas nuevas de MeLi, alertas de "Mis Alertas") a su Telegram
///   - le contesta consultas simples ("ventas", "saldo", "alertas") que le escriba al bot
///
/// Es una tabla de UNA sola fila (mismo molde que MpAccount/EzvizAccount): se hace siempre
/// OrderBy(Id).FirstOrDefault(). El token se guarda en texto (lo que protege esto es el acceso
/// a la DB, igual criterio que Mercado Pago/EZVIZ). Nunca se expone el token al frontend.
///
/// Pedido de Osmar 2026-07-10: "ya que no tenemos WhatsApp, hacer algo con Telegram para
/// notificaciones al celu y dar ordenes desde el celu".
/// </summary>
[Table("TelegramAccounts")]
public class TelegramAccount
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Para qué sirve este bot. "AVISOS" = notificaciones + consultas (el bot principal).
    /// "PREVENTAS" = chat aparte donde el dueño solo carga preventas. Así no se mezcla todo.
    /// Puede haber una fila por propósito.</summary>
    [MaxLength(20)]
    public string Proposito { get; set; } = "AVISOS";

    /// <summary>Token del bot que devuelve @BotFather (formato "123456789:AA....").</summary>
    [Required]
    public string BotToken { get; set; } = string.Empty;

    /// <summary>Chat de Telegram del dueño (a donde se mandan los avisos). Se captura solo cuando
    /// el usuario le escribe al bot (getUpdates) o con el botón "Vincular mi Telegram".</summary>
    public long? ChatId { get; set; }

    /// <summary>Usuario del bot (ej "intervent_avisos_bot"), para mostrarlo en la pantalla.</summary>
    [MaxLength(120)]
    public string? BotUsername { get; set; }

    /// <summary>Código de seguridad para VINCULAR el bot la primera vez. Cuando el chat todavía no
    /// está vinculado (ChatId null), el bot NO le hace caso a nadie hasta que le manden este código.
    /// Así un desconocido que descubra el bot no puede "adueñárselo". Se genera solo y se muestra en
    /// la pantalla del sistema. Pedido de Osmar 2026-07-11.</summary>
    [MaxLength(20)]
    public string? VinculacionCode { get; set; }

    public bool IsActive { get; set; } = true;

    // --- Qué avisos mandar (tildes que elige el usuario) ---
    /// <summary>Avisar cada venta nueva de MercadoLibre.</summary>
    public bool NotifVentas { get; set; } = true;
    /// <summary>Avisar cuando salta una alerta de "Mis Alertas". OJO: desde 2026-07-10 el aviso de
    /// alertas se elige por-alerta (MisAlerta.CanalTelegram); este flag quedó como interruptor
    /// histórico y ya no controla el envío. Se mantiene para no romper datos existentes.</summary>
    public bool NotifAlertas { get; set; } = true;

    /// <summary>Avisar cada fichada de empleado (entrada/salida), con la plata a rendir al salir.</summary>
    public bool NotifFichadas { get; set; } = true;

    /// <summary>Cursor del poll de mensajes entrantes (update_id ya procesado). Evita reprocesar
    /// y saltear mensajes viejos. Null = arrancar desde el próximo mensaje.</summary>
    public long? LastUpdateId { get; set; }

    // --- Estado de la conversación de PREVENTA (solo el bot PREVENTAS lo usa) ---
    /// <summary>Paso actual de la carga de preventa: null | CLIENTE | MENU (menú de productos) |
    /// CANT (esperando cantidad de un producto) | BUSCAR (esperando texto de búsqueda) |
    /// LIBRE (esperando el pedido escrito a mano).</summary>
    [MaxLength(20)]
    public string? ConvEstado { get; set; }
    /// <summary>Cliente elegido para la preventa en curso (null = venta suelta).</summary>
    public int? ConvClienteId { get; set; }
    [MaxLength(200)]
    public string? ConvClienteNombre { get; set; }

    /// <summary>Carrito de la preventa en curso: JSON con los productos ya agregados
    /// [{ProductoId, Sku, Nombre, Cantidad}]. Al terminar se copia al pedido.</summary>
    public string? ConvItemsJson { get; set; }
    /// <summary>Producto elegido esperando que el usuario diga la cantidad (estado CANT).</summary>
    public int? ConvPendProductoId { get; set; }
    [MaxLength(200)]
    public string? ConvPendProductoNombre { get; set; }
    [MaxLength(60)]
    public string? ConvPendProductoSku { get; set; }

    // --- Último intento de conexión ---
    public bool LastSyncOk { get; set; } = false;
    [MaxLength(500)]
    public string? LastError { get; set; }
    public DateTime? LastSyncAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
