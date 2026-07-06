using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Chat_Mensajes")]
public class ChatMensaje
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Usuario que envió el mensaje.</summary>
    public int DeUserId { get; set; }

    /// <summary>Nombre para mostrar, guardado al momento de enviar.</summary>
    [MaxLength(120)]
    public string? DeNombre { get; set; }

    /// <summary>NULL = Grupo general. Con valor = mensaje privado a ese usuario.</summary>
    public int? ParaUserId { get; set; }

    /// <summary>Texto del mensaje. Puede ir vacío si el mensaje es solo un adjunto.</summary>
    public string Cuerpo { get; set; } = string.Empty;

    // 2026-07-06: adjunto opcional (foto / archivo / audio). El archivo físico vive en el
    // volumen /data/files/chat/. Un job lo borra a los X días (config); el mensaje queda.
    /// <summary>Nombre del archivo guardado en el volumen (interno). Null = sin adjunto.</summary>
    [MaxLength(255)]
    public string? AdjuntoArchivo { get; set; }

    /// <summary>Nombre original del archivo, para mostrar y descargar.</summary>
    [MaxLength(255)]
    public string? AdjuntoNombre { get; set; }

    /// <summary>Tipo: "image" | "audio" | "file".</summary>
    [MaxLength(20)]
    public string? AdjuntoTipo { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
