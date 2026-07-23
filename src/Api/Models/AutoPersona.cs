using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-07-23 (Centro de Automatizaciones): la "libretita" de personas que reciben avisos.
/// Cada una con sus 3 direcciones: Telegram (ChatId del bot AVISOS), WhatsApp y correo.
/// Se cargan UNA vez acá y después se tildan por automatización.</summary>
[Table("Auto_Personas")]
public class AutoPersona
{
    public int Id { get; set; }
    [Required, MaxLength(80)] public string Nombre { get; set; } = "";
    public long? TelegramChatId { get; set; }
    [MaxLength(30)] public string? WhatsAppNumero { get; set; }
    [MaxLength(150)] public string? Email { get; set; }
    public bool Activo { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Configuración de una automatización programada (aviso diario): interruptor,
/// días (1=lunes..7=domingo, csv), hora (hora Argentina) y canales de salida.</summary>
[Table("Auto_Config")]
public class AutoConfig
{
    [Key, MaxLength(50)] public string AutoKey { get; set; } = "";
    public bool Enabled { get; set; } = true;
    [MaxLength(20)] public string Dias { get; set; } = "1,2,3,4,5,6,7";
    public int Hora { get; set; } = 8;
    public bool CanalCampanita { get; set; }
    public bool CanalTelegram { get; set; } = true;
    public bool CanalWhatsApp { get; set; }
    public bool CanalEmail { get; set; }
    public DateTime? LastRunAt { get; set; }
    public bool? LastRunOk { get; set; }
    [MaxLength(300)] public string? LastRunDetalle { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>True si hoy (hora Argentina) es uno de los días elegidos.</summary>
    public bool CorreHoy(DateTime argNow)
    {
        // DayOfWeek: Sunday=0 ... Saturday=6 → nuestro esquema 1=lunes..7=domingo
        var dia = argNow.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)argNow.DayOfWeek;
        return (Dias ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(dia.ToString());
    }
}

/// <summary>Qué personas reciben cada automatización.</summary>
[Table("Auto_Destinatarios")]
public class AutoDestinatario
{
    public int Id { get; set; }
    [Required, MaxLength(50)] public string AutoKey { get; set; } = "";
    public int PersonaId { get; set; }
}
