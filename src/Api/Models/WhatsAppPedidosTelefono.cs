using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Teléfonos autorizados a mandar pedidos por WhatsApp con #PED.
/// El poller (WhatsAppPedidosPollerService) itera esta lista, abre el chat de
/// cada uno cada 45s y lee mensajes nuevos. Cada teléfono tiene su propio
/// cursor (LastMessageId) para no re-procesar.
/// </summary>
[Table("WhatsAppPedidosTelefonos")]
public class WhatsAppPedidosTelefono
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Teléfono normalizado (solo dígitos, con código país, sin +). Ej: "5491122525458".</summary>
    [Required, MaxLength(40)]
    public string Telefono { get; set; } = "";

    /// <summary>Etiqueta opcional para identificarlo en pantalla. Ej: "Hermano", "Yo móvil".</summary>
    [MaxLength(80)]
    public string? Etiqueta { get; set; }

    /// <summary>Si está apagado, el poller lo saltea (sin borrarlo).</summary>
    public bool Activo { get; set; } = true;

    /// <summary>Cursor: id del último mensaje leído de WA para este teléfono.</summary>
    [MaxLength(200)]
    public string? LastMessageId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastReadAt { get; set; }
}
