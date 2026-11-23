using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-11-23: alerta de una publicación MeLi cuando entró stock y:
/// - NO_ACTIVADA: quedó pausada porque deja menos del 10% (o da pérdida, o no se pudo calcular).
/// - ACTIVADA_POCO: se activó sola pero deja entre 10% y 40%.
/// Una sola fila ABIERTA por publicación (se actualiza, no se repite). Salta al centro de la
/// pantalla del admin (NO_ACTIVADA) y se ve en la tarjeta MercadoLibre de Inicio.
/// Estado: ABIERTA → (botón) RESUELTA / DEJADA; AUTORIZADA = el admin tocó "Activar" y el
/// push de stock la despierta aunque el margen no dé.</summary>
[Table("Meli_AlertasPausadas")]
public class MeliAlertaPausada
{
    [Key] public int Id { get; set; }
    [MaxLength(30)] public string MeliItemId { get; set; } = "";
    public int? MeliAccountId { get; set; }
    [MaxLength(20)] public string Tipo { get; set; } = "NO_ACTIVADA";
    [MaxLength(20)] public string Estado { get; set; } = "ABIERTA";
    [MaxLength(300)] public string? Titulo { get; set; }
    [MaxLength(100)] public string? Sku { get; set; }
    [MaxLength(400)] public string? Permalink { get; set; }
    [MaxLength(400)] public string? Motivo { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? PrecioActual { get; set; }
    [Column(TypeName = "decimal(9,2)")] public decimal? MargenPct { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? Costo { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? Precio40 { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? Precio50 { get; set; }
    public int Stock { get; set; }
    public DateTime? PospuestaHasta { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResueltaAt { get; set; }
    [MaxLength(100)] public string? ResueltaPor { get; set; }
    [MaxLength(200)] public string? Accion { get; set; }
}
