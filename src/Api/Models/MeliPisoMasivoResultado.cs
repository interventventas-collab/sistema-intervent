using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-08-10: resultado de una corrida del "Piso de margen 50% masivo".
/// Una fila por publicación evaluada, agrupada por RunId. La vista previa (DryRun)
/// escribe las filas SIN tocar precios; el paso de aplicar reusa estas filas y marca
/// AplicadoOk cuando pushea. Ver [[project_meli_sincro_precios_rediseno]].
/// </summary>
[Table("Cafe_PisoMasivo_Resultado")]
public class MeliPisoMasivoResultado
{
    [Key]
    public long Id { get; set; }

    /// <summary>Identificador de la corrida (comparte con el progressId para poder cruzarlos).</summary>
    [MaxLength(32)]
    public string RunId { get; set; } = string.Empty;

    [Column(TypeName = "decimal(8,4)")]
    public decimal GananciaPct { get; set; }

    [MaxLength(50)]
    public string MeliItemId { get; set; } = string.Empty;

    public int ItemDbId { get; set; }

    [MaxLength(300)]
    public string? Titulo { get; set; }

    [MaxLength(120)]
    public string? Sku { get; set; }

    [MaxLength(30)]
    public string? Status { get; set; }

    [Column(TypeName = "decimal(18,2)")] public decimal? Costo { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? PrecioBase { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? PrecioActual { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? PrecioNuevo { get; set; }
    [Column(TypeName = "decimal(9,2)")] public decimal? MargenActual { get; set; }
    [Column(TypeName = "decimal(9,2)")] public decimal? MargenNuevo { get; set; }

    /// <summary>True solo si el margen actual se pudo calcular con confianza (comisión fresca + envío cacheado).</summary>
    public bool Confiable { get; set; }

    /// <summary>SUBE | YA_OK | NO_CONFIABLE | SIN_COSTO | SIN_BASE | ERROR</summary>
    [MaxLength(20)]
    public string Accion { get; set; } = string.Empty;

    [MaxLength(400)]
    public string? Mensaje { get; set; }

    /// <summary>NULL = todavía no aplicado; true = pusheado ok; false = falló al aplicar.</summary>
    public bool? AplicadoOk { get; set; }

    public DateTime? AplicadoAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
