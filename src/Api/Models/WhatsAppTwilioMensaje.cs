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
    [MaxLength(50)] public string? TwilioMessageSid { get; set; }
    public bool Procesado { get; set; }
    [MaxLength(10)] public string? PedidoTrigger { get; set; }
    public int? VentaIdGenerada { get; set; }
    public string? RespuestaEnviada { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
