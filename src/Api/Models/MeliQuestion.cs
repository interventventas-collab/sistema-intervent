using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("MeliQuestions")]
public class MeliQuestion
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>ID de la pregunta en MeLi (unico).</summary>
    public long MeliQuestionId { get; set; }

    public int MeliAccountId { get; set; }

    [ForeignKey(nameof(MeliAccountId))]
    public MeliAccount? MeliAccount { get; set; }

    /// <summary>ID del item (publicacion) MLA1234. Snapshot.</summary>
    [MaxLength(50)]
    public string ItemId { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? ItemTitle { get; set; }

    [MaxLength(500)]
    public string? ItemThumbnail { get; set; }

    /// <summary>ID del comprador en MeLi.</summary>
    public long FromUserId { get; set; }

    [MaxLength(100)]
    public string? FromNickname { get; set; }

    [Required]
    public string Text { get; set; } = string.Empty;

    /// <summary>Texto de la respuesta. Null mientras no se respondio.</summary>
    public string? AnswerText { get; set; }

    /// <summary>UNANSWERED | ANSWERED | DELETED | BANNED | UNDER_REVIEW | CLOSED_UNANSWERED.</summary>
    [MaxLength(30)]
    public string Status { get; set; } = "UNANSWERED";

    public DateTime DateCreated { get; set; }

    public DateTime? DateAnswered { get; set; }

    /// <summary>Cuando la marcamos como vista en la app (para que la campanita no parpadee mas).</summary>
    public DateTime? SeenAt { get; set; }

    public DateTime LastSyncedAt { get; set; } = DateTime.UtcNow;
}
