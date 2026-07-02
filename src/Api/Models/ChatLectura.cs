using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>Hasta dónde leyó cada usuario en cada conversación.
/// Conversacion = "grupo" ó "u:{idDelOtroUsuario}".</summary>
[Table("Chat_Lecturas")]
public class ChatLectura
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int UserId { get; set; }

    [Required]
    [MaxLength(40)]
    public string Conversacion { get; set; } = string.Empty;

    public DateTime LastReadAt { get; set; } = DateTime.UtcNow;
}
