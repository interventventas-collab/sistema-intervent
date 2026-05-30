using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Configuracion por publicacion MeLi: que se sincroniza y con que ajuste.
///
/// Relacion 1:1 con MeliItem (clave MeliItemId, no autoincremental).
/// Permite controlar item por item:
///   - Si el sistema pushea stock a esta publicacion
///   - Si el sistema pushea precio a esta publicacion
///   - Que formula de precio aplicar al pushear: precio_sistema_cIVA × (1 + AjustePct/100) + AjusteFijo
///
/// Ejemplo: AjustePct=0, AjusteFijo=1000 → MeLi = $4.235 + $1.000 = $5.235
/// Ejemplo: AjustePct=16, AjusteFijo=0 → MeLi = $4.235 × 1.16 = $4.913
/// </summary>
[Table("MeliItem_SyncConfig")]
public class MeliItemSyncConfig
{
    [Key, MaxLength(50)]
    public string MeliItemId { get; set; } = string.Empty;

    /// <summary>True por default: el sistema pushea stock automatico cuando cambia.</summary>
    public bool SyncStock { get; set; } = true;

    /// <summary>False por default: la primera vez el operador decide configurarlo.
    /// Cuando es true, el push event-driven aplica AjustePct + AjusteFijo sobre el precio_sistema_cIVA.</summary>
    public bool SyncPrecio { get; set; } = false;

    [Column(TypeName = "decimal(8,4)")]
    public decimal AjustePct { get; set; } = 0m;

    [Column(TypeName = "decimal(18,2)")]
    public decimal AjusteFijo { get; set; } = 0m;

    /// <summary>2026-05-29: redondeo al final de la cuenta (despues de aplicar Pct y Fijo).
    /// Valores: "" (sin redondeo), "99" (terminacion 99), "999" (terminacion 999), "000" (multiplo de 1000).
    /// Siempre redondea HACIA ARRIBA — no le baja al usuario sin querer.</summary>
    [MaxLength(8)]
    public string? AjusteRedondeo { get; set; }

    /// <summary>Ultima vez que el sistema pusheo precio o stock para este item.</summary>
    public DateTime? LastSyncAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Cache de comisiones MeLi por (CategoryId + ListingTypeId).
/// Evita consultar /sites/MLA/listing_prices en cada llamada — se consulta 1 vez
/// y se reutiliza. Si los precios oficiales de MeLi cambian, basta con borrar la fila.
/// </summary>
[Table("MeliCommissionRates")]
public class MeliCommissionRate
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(30)]
    public string CategoryId { get; set; } = string.Empty;

    [Required, MaxLength(30)]
    public string ListingTypeId { get; set; } = string.Empty;

    [Column(TypeName = "decimal(5,2)")]
    public decimal PercentageFee { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal FixedFee { get; set; }

    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
}
