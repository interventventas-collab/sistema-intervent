using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-08-01: nombre + imagen personalizados por LÍNEA (WhatsApp o Instagram), para
/// identificarlas mejor en la bandeja. LineaId = phone_number_id (WhatsApp) o IG account id.
/// El número/usuario real sigue viniendo de AppSettings whatsapp.linea.{id}; esto es solo cómo se ve.</summary>
[Table("WhatsApp_LineasConfig")]
public class WhatsAppLineaConfig
{
    [Key, MaxLength(60)] public string LineaId { get; set; } = "";
    [MaxLength(120)] public string? Nombre { get; set; }
    /// <summary>Imagen como data URL (data:image/...;base64,...). Es un avatar chico, va embebido.</summary>
    public string? ImagenDataUrl { get; set; }
    /// <summary>2026-08-01: sonido de aviso de esta línea (clave: chime/msn/coin/powerup/laser/levelup). Null = default.</summary>
    [MaxLength(30)] public string? Sonido { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
