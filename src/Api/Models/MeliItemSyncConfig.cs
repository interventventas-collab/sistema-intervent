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

    // ==========================================
    // 2026-06-11: Manejo de "precio independiente" por MLA
    // Para familias donde cada MLA tiene su propio precio (estrategia cuotas)
    // ==========================================

    /// <summary>2026-06-11: Si esta en true, el push de precio NO toca esta MLA por la formula
    /// estandar (precio_sistema × ajuste). En cambio, usa PrecioFactor × Precio_actual_del_producto_base.
    /// Esto es para familias donde cada MLA tiene su propio precio en MeLi (ej: 6 cuotas / 12 cuotas / envio gratis)
    /// y no queres que el sistema iguale todo al mismo precio.</summary>
    public bool PrecioIndependiente { get; set; } = false;

    /// <summary>2026-06-11: Factor multiplicador sobre el precio base del producto.
    /// Calculado como (Precio actual MeLi) / (Precio Otro del producto base).
    /// Ej: si producto vale $22.000 y la MLA esta en $33.000, Factor = 1.5000.
    /// Cuando el operador sube el costo y cambia el precio base, este factor se usa para sugerir
    /// los nuevos precios de las MLAs marcadas como Independiente.</summary>
    [Column(TypeName = "decimal(10,4)")]
    public decimal? PrecioFactor { get; set; }

    /// <summary>2026-06-11: Precio base del producto al momento de calcular el factor (snapshot).
    /// Sirve para verificar que el factor sigue siendo valido cuando cambia el precio base.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? PrecioBaseRef { get; set; }

    /// <summary>2026-06-11: Tipo de publicacion en MeLi: "gold_special" (Clásica), "gold_pro" (Premium), etc.
    /// Se sincroniza desde MeLi para que el operador sepa que MLA es cual sin entrar a la pagina.</summary>
    [MaxLength(40)]
    public string? ListingType { get; set; }

    /// <summary>2026-06-11: Texto descriptivo de la configuracion de cuotas.
    /// Ej: "Sin cuotas", "3 al mismo precio", "12 al mismo precio", "3 a 12 con interes bajo".
    /// Se sincroniza desde MeLi para tener visibilidad.</summary>
    [MaxLength(80)]
    public string? InstallmentConfig { get; set; }

    /// <summary>2026-06-11: Si esta MLA tiene envio gratis configurado en MeLi.</summary>
    public bool? FreeShipping { get; set; }

    /// <summary>2026-07-02: Objetivo de ganancia (% sobre costo) que el operador queria
    /// al pushear el precio. Se usa para verificar despues si el precio publicado sigue
    /// dando ese margen, o si comisiones/envio lo corrieron. Umbral tolerado: +-2 pt.</summary>
    [Column(TypeName = "decimal(6,2)")]
    public decimal? GananciaObjetivoPct { get; set; }

    /// <summary>2026-07-02: Cuando se cargo/actualizo el objetivo.</summary>
    public DateTime? GananciaObjetivoAt { get; set; }
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
