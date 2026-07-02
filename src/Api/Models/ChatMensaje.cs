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

    [Required]
    public string Cuerpo { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
