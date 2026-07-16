using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Una PERSONA vinculada a un bot de Telegram (antes el bot tenía un solo dueño en
/// TelegramAccounts.ChatId; ahora puede haber varias personas por bot).
///
/// Cada persona se vincula sola: le escribe el código de seguridad al bot desde su Telegram
/// y queda agregada acá. El admin la ve en Integraciones → Telegram, le pone nombre, elige
/// qué avisos le llegan (tildes) y la puede quitar cuando quiera.
///
/// Pedido de Osmar 2026-07-16: "quiero poder ir dandole acceso a esas notificaciones a los
/// empleados de a poco". Por eso los que se vinculan después del primero arrancan con TODOS
/// los tildes apagados: no reciben nada hasta que el admin les prende algo.
/// </summary>
[Table("TelegramChats")]
public class TelegramChat
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>A qué bot está vinculada esta persona (fila de TelegramAccounts).</summary>
    public int TelegramAccountId { get; set; }

    /// <summary>Chat privado de Telegram de la persona (a donde se le manda todo).</summary>
    public long ChatId { get; set; }

    /// <summary>Nombre para reconocerla en la pantalla ("Osmar", "Germán"...). Al vincularse se
    /// toma el nombre de su perfil de Telegram; el admin lo puede corregir.</summary>
    [MaxLength(120)]
    public string? Nombre { get; set; }

    // --- Qué avisos le llegan a ESTA persona (solo aplica al bot de AVISOS) ---
    /// <summary>Le llegan las ventas nuevas de MercadoLibre.</summary>
    public bool NotifVentas { get; set; }
    /// <summary>Le llegan las alertas de "Mis Alertas" (banco, cheques, correos, publicaciones MeLi...).</summary>
    public bool NotifAlertas { get; set; }
    /// <summary>Le llegan las fichadas de empleados (entrada/salida + plata a rendir).</summary>
    public bool NotifFichadas { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
