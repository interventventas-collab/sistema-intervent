using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("WhatsApp_TwilioMensajes")]
public class WhatsAppTwilioMensaje
{
    public int Id { get; set; }
    [MaxLength(10)] public string Direccion { get; set; } = "INCOMING";
    [MaxLength(30)] public string Numero { get; set; } = "";
    [MaxLength(120)] public string? NombrePerfil { get; set; }
    public string? Cuerpo { get; set; }
    [MaxLength(500)] public string? MediaUrl { get; set; }
    public int? NumMedia { get; set; }
    /// <summary>ID del mensaje del proveedor: SID de Twilio o wamid.* de Meta Cloud API (este último es largo).</summary>
    [MaxLength(200)] public string? TwilioMessageSid { get; set; }
    /// <summary>Canal de origen del mensaje: "TWILIO" (default) o "CLOUD" (API oficial de Meta).</summary>
    [MaxLength(10)] public string Canal { get; set; } = "TWILIO";
    public bool Procesado { get; set; }
    [MaxLength(10)] public string? PedidoTrigger { get; set; }
    public int? VentaIdGenerada { get; set; }
    public string? RespuestaEnviada { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
