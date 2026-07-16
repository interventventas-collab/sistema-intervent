using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Cambio detectado en una publicación MeLi (precio o status).
/// Se genera por dos vías:
/// 1. Webhook items: MeLi notifica → sync del item → comparación → log si cambió.
/// 2. Sync periódico: cada vez que SyncItemsAsync corre, compara contra DB y registra cambios.
/// El usuario los ve en /cafe/cambios-meli con badge en topbar.
/// </summary>
[Table("MeliCambiosDetectados")]
public class MeliCambioDetectado
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(50)]
    public string MeliItemId { get; set; } = "";

    public int? MeliAccountId { get; set; }

    [MaxLength(80)]
    public string? Sku { get; set; }

    [MaxLength(300)]
    public string? Title { get; set; }

    /// <summary>PRECIO_BAJA | PRECIO_SUBE | STATUS_PAUSED | STATUS_ACTIVE</summary>
    [Required, MaxLength(30)]
    public string Tipo { get; set; } = "";

    [MaxLength(60)]
    public string? ValorAnterior { get; set; }

    [MaxLength(60)]
    public string? ValorNuevo { get; set; }

    /// <summary>Diferencia numérica (precio nuevo - precio anterior).</summary>
    public decimal? Delta { get; set; }

    /// <summary>Variación porcentual.</summary>
    public decimal? DeltaPct { get; set; }

    /// <summary>webhook | sync</summary>
    [Required, MaxLength(20)]
    public string Source { get; set; } = "sync";

    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Cuando el usuario marcó este cambio como visto.</summary>
    public DateTime? SeenAt { get; set; }

    /// <summary>2026-07-16: cuándo se avisó este cambio por Mis Alertas (Telegram/campanita).
    /// NULL = todavía no se avisó. Lo consume TelegramService.NotificarPublicacionesMeliAsync
    /// para los tipos PAUSADA_CON_STOCK y STATUS_ACTIVE (publicaciones a revisar).</summary>
    public DateTime? NotifiedAt { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }
}
