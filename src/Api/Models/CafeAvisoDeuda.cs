using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-09-29 — "Mandale a Gabriel lo que debe este cliente" programado desde la ficha.
/// Una vez, todas las semanas un día, o un día fijo de cada mes, a la hora elegida (hora
/// ARGENTINA). A quién: Auto_Destinatarios con clave <see cref="Clave"/> (libretita de personas).
/// El mensaje sale por WhatsApp_MensajesProgramados con EsperarVentana: si el destinatario no
/// escribió en 24 hs, queda esperando y sale apenas escriba.
/// </summary>
[Table("Cafe_AvisosDeuda")]
public class CafeAvisoDeuda
{
    [Key]
    public int Id { get; set; }

    public int ClienteId { get; set; }

    /// <summary>UNA_VEZ | SEMANAL | MENSUAL</summary>
    [Required, MaxLength(10)]
    public string Frecuencia { get; set; } = FrecUnaVez;

    /// <summary>UNA_VEZ: el día (hora argentina, sin la hora).</summary>
    public DateTime? Fecha { get; set; }

    /// <summary>SEMANAL: 1 = lunes … 7 = domingo.</summary>
    public int? DiaSemana { get; set; }

    /// <summary>MENSUAL: 1..28 (así existe en todos los meses).</summary>
    public int? DiaMes { get; set; }

    /// <summary>Hora argentina en minutos desde las 00:00 (10:00 = 600).</summary>
    public int HoraMin { get; set; } = 600;

    /// <summary>UTC. Cuándo le toca el próximo envío. Null = no hay más (una vez ya enviada).</summary>
    public DateTime? ProximoEnvio { get; set; }

    public bool Activo { get; set; } = true;

    public DateTime? UltimoEnvioAt { get; set; }

    [MaxLength(400)]
    public string? UltimoResultado { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string? CreatedBy { get; set; }

    public const string FrecUnaVez = "UNA_VEZ";
    public const string FrecSemanal = "SEMANAL";
    public const string FrecMensual = "MENSUAL";

    public static string Clave(int avisoId) => $"deuda-cli:{avisoId}";
}
