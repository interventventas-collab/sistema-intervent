using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-08-19: sonidos de aviso SUBIDOS por el usuario, para sumarlos a los seis que trae
/// el sistema (esos están hechos por código en index.html, no son archivos). Se guardan acá adentro
/// —no en disco— porque pesan poco y así los ve todo el equipo desde cualquier computadora.
/// En la configuración de la línea se guardan con la clave "subido:{Id}".</summary>
[Table("WhatsApp_Sonidos")]
public class WhatsAppSonido
{
    public int Id { get; set; }
    /// <summary>Cómo lo ve el usuario en la lista, ej "Campana iglesia".</summary>
    [MaxLength(80)] public string Nombre { get; set; } = "";
    /// <summary>Tipo del archivo (audio/mpeg, audio/wav…), para devolverlo bien al navegador.</summary>
    [MaxLength(60)] public string Mime { get; set; } = "audio/mpeg";
    /// <summary>El audio en sí. Tope 300 KB, lo controla el controller.</summary>
    public byte[] Datos { get; set; } = Array.Empty<byte>();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Quién lo subió, solo para saber a quién preguntarle si aparece uno raro.</summary>
    [MaxLength(120)] public string? CreadoPor { get; set; }
}
