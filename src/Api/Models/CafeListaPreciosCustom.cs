using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Lista de precios personalizada (Fase 1 - 2026-06-09).
/// El usuario arma manualmente listas con secciones tituladas + items elegidos
/// (productos / combos / packs). Los precios se calculan en runtime (no se congelan),
/// asi que cualquier cambio en el catalogo se refleja automaticamente al imprimir.
/// </summary>
[Table("Cafe_ListasPreciosCustom")]
public class CafeListaPreciosCustom
{
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Nombre { get; set; } = "";

    /// <summary>Cliente opcional al que apunta esta lista (sino es lista "general").</summary>
    public int? ClienteId { get; set; }
    [ForeignKey(nameof(ClienteId))]
    public CafeCliente? ClienteNav { get; set; }

    /// <summary>Override del tipo de cliente: BAR / OTRO. Si null, se toma del ClienteNav.</summary>
    [MaxLength(20)]
    public string? TipoCliente { get; set; }

    [MaxLength(1000)]
    public string? Observaciones { get; set; }

    [MaxLength(50)]
    public string? NumeroLista { get; set; }

    /// <summary>2026-06-16: URL/path relativo del SVG/PNG que se usa como fondo del PDF.
    /// Si esta seteado, el PDF lo renderea con baja opacidad atrás del contenido.
    /// Ejemplo: "Listas Custom/take-away-bg.svg"</summary>
    [MaxLength(500)]
    public string? BackgroundUrl { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<CafeListaPreciosCustomSeccion> Secciones { get; set; } = new List<CafeListaPreciosCustomSeccion>();
}

[Table("Cafe_ListasPreciosCustomSeccion")]
public class CafeListaPreciosCustomSeccion
{
    public int Id { get; set; }

    public int ListaId { get; set; }
    [ForeignKey(nameof(ListaId))]
    public CafeListaPreciosCustom? ListaNav { get; set; }

    [Required, MaxLength(200)]
    public string Titulo { get; set; } = "";

    public int Orden { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<CafeListaPreciosCustomItem> Items { get; set; } = new List<CafeListaPreciosCustomItem>();
}

[Table("Cafe_ListasPreciosCustomItem")]
public class CafeListaPreciosCustomItem
{
    public int Id { get; set; }

    public int SeccionId { get; set; }
    [ForeignKey(nameof(SeccionId))]
    public CafeListaPreciosCustomSeccion? SeccionNav { get; set; }

    /// <summary>"PRODUCTO" / "COMBO" / "PACK"</summary>
    [Required, MaxLength(20)]
    public string TipoItem { get; set; } = "";

    /// <summary>Id de Cafe_Producto / Cafe_Combos / Cafe_ProductoPacks segun TipoItem.</summary>
    public int RefId { get; set; }

    public int Orden { get; set; }

    [MaxLength(500)]
    public string? Notas { get; set; }

    /// <summary>2026-06-16: marca NOVEDAD — chip rojo en el PDF tipo TAKE AWAY.</summary>
    public bool EsNovedad { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
