using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-09-09 — Un aviso que le tapa la pantalla a Depósito hasta que alguien lo toca.
///
/// Nace de dos lados: porque Gabriel escribió una palabra clave (@ojo o 🔴) en un chat de
/// Depósito, o porque la oficina apretó el botón de avisar.
///
/// Lo importante no es sólo que suene: es que quede <b>quién lo vio y a qué hora</b>. Sin eso el
/// aviso no se puede confiar — hoy no hay forma de saber si lo leyeron o si el mensaje quedó
/// enterrado entre otros veinte.
/// </summary>
[Table("WhatsApp_AvisosDeposito")]
public class WhatsAppAvisoDeposito
{
    public const string OrigenMensaje = "mensaje";
    public const string OrigenBoton = "boton";

    [Key]
    public int Id { get; set; }

    /// <summary>Chat del que salió (para el botón "Ir al chat"). Null en un aviso suelto.</summary>
    [MaxLength(60)] public string? Numero { get; set; }
    [MaxLength(60)] public string? LineaPhoneId { get; set; }

    [Required, MaxLength(120)] public string Titulo { get; set; } = "";

    /// <summary>Lo que se muestra grande. En los de mensaje, el texto de Gabriel SIN la palabra clave.</summary>
    [Required, MaxLength(1000)] public string Texto { get; set; } = "";

    [Required, MaxLength(20)] public string Origen { get; set; } = OrigenMensaje;

    [MaxLength(100)] public string? CreadoPor { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Quién apretó "Lo vi" y cuándo. Mientras esté en null, el cartel sigue tapando.</summary>
    [MaxLength(100)] public string? VistoPor { get; set; }
    public DateTime? VistoAt { get; set; }
}
