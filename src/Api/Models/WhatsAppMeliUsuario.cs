using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-08-21: qué usuario de MercadoLibre es el que escribe desde ESTE teléfono de WhatsApp.
/// Lo deja atado el operador desde el 🟡 Modo MercadoLibre del chat, y a partir de ahí la ficha
/// del comprador se abre sola cada vez que ese número escribe.
///
/// Va aparte del vínculo con el cliente del sistema (Cafe_Clientes.MeliBuyerId) a propósito:
/// el que pregunta por una publicación muchas veces NO es cliente nuestro todavía, y aun así
/// queremos reconocerlo. Un mismo teléfono puede tener más de un usuario de MeLi colgado.
/// </summary>
[Table("WhatsApp_MeliUsuarios")]
public class WhatsAppMeliUsuario
{
    public int Id { get; set; }
    /// <summary>"whatsapp:+549..." — igual que WhatsApp_TwilioContactos.</summary>
    [MaxLength(30)] public string Numero { get; set; } = "";
    /// <summary>Id del comprador en MercadoLibre.</summary>
    public long BuyerId { get; set; }
    [MaxLength(255)] public string? Nickname { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Quién lo ató (firma del operador o "user:{id}"), para poder rastrearlo.</summary>
    [MaxLength(60)] public string? CreatedBy { get; set; }
}
