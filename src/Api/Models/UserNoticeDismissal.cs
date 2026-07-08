using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Marca que un usuario ocultó ("No volver a mostrar") un aviso/novedad puntual.
/// Se guarda por CUENTA, así vale en cualquier dispositivo desde el que entre. 2026-07-08.
/// </summary>
[Table("User_NoticeDismissals")]
public class UserNoticeDismissal
{
    public int Id { get; set; }

    public int UserId { get; set; }

    /// <summary>Identificador del aviso, ej: "novedad-atajos-2026-07".</summary>
    [Required, MaxLength(80)]
    public string NoticeKey { get; set; } = "";

    public DateTime DismissedAt { get; set; } = DateTime.UtcNow;
}
