using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("WhatsApp_TwilioRespuestasRapidas")]
public class WhatsAppTwilioRespuestaRapida
{
    public int Id { get; set; }
    [MaxLength(80)] public string Nombre { get; set; } = "";
    public string Texto { get; set; } = "";
    public int Orden { get; set; }
    public bool Activo { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("WhatsApp_TwilioContactos")]
public class WhatsAppTwilioContacto
{
    public int Id { get; set; }
    [MaxLength(30)] public string Numero { get; set; } = "";
    [MaxLength(120)] public string Nombre { get; set; } = "";
    [MaxLength(20)] public string Rol { get; set; } = "otro";
    public string? Notas { get; set; }
    public bool Activo { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
