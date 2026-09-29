using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-09-29 — Plan de bonificación de un cliente (ej: Núcleo).
///
/// Dos premios, los dos por los kg PAGADOS del café base (F1):
///   1. En cada venta: cada <see cref="KgPorRegalo"/> kg → <see cref="CantidadRegalo"/> unidades
///      de <see cref="ProductoRegaloId"/> (caja de edulcorante D401) bonificadas.
///   2. A fin de mes: <see cref="PctMensual"/>% de los kg del mes, en kg del mismo café,
///      redondeado para arriba al kilo (se puede cambiar a mano, ver <see cref="CafeBonificacionMes"/>).
///
/// Lo bonificado (renglones con 100% de descuento) NUNCA suma kg: serían kilos falsos.
/// </summary>
[Table("Cafe_PlanesBonificacion")]
public class CafePlanBonificacion
{
    [Key]
    public int Id { get; set; }

    public int ClienteId { get; set; }

    public bool Activo { get; set; } = true;

    /// <summary>Café que se cuenta (F1). También es el café que se regala a fin de mes.</summary>
    public int ProductoKgId { get; set; }

    [Column(TypeName = "decimal(18,3)")]
    public decimal KgPorRegalo { get; set; } = 5m;

    /// <summary>Lo que se regala en cada venta (D401). Null = sin regalo por venta.</summary>
    public int? ProductoRegaloId { get; set; }

    public int CantidadRegalo { get; set; } = 1;

    /// <summary>% mensual en kg. 0 = sin bonificación de fin de mes.</summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal PctMensual { get; set; } = 10m;

    /// <summary>Primer día del mes desde el que corre el plan (las ventas anteriores no cuentan).</summary>
    public DateTime Desde { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    [MaxLength(100)]
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// 2026-09-29 — Kg de fin de mes que se le dan a un cliente, cuando alguien los cambió a mano.
/// Si no hay fila para un mes, vale lo sugerido (10% redondeado para arriba).
/// </summary>
[Table("Cafe_BonificacionesMes")]
public class CafeBonificacionMes
{
    [Key]
    public int Id { get; set; }

    public int ClienteId { get; set; }

    public int Anio { get; set; }

    public int Mes { get; set; }

    [Column(TypeName = "decimal(18,3)")]
    public decimal KgOtorgado { get; set; }

    [MaxLength(300)]
    public string? Nota { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string? UpdatedBy { get; set; }
}
