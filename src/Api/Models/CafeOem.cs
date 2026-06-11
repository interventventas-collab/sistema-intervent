using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_Oems")]
public class CafeOem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(50)]
    public string Codigo { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Descripcion { get; set; }

    [MaxLength(100)]
    public string? Marca { get; set; }

    /// <summary>FK a Cafe_Marcas. Reemplaza progresivamente el campo de texto Marca.</summary>
    public int? MarcaId { get; set; }

    [ForeignKey(nameof(MarcaId))]
    public CafeMarca? MarcaNav { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Costo { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? PvpConIva { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? IvaPct { get; set; }

    [MaxLength(100)]
    public string? Barcode { get; set; }

    [MaxLength(100)]
    public string? Proveedor { get; set; }

    /// <summary>Unidades por bulto. Informativo; se autocompleta a la variante al vincular.</summary>
    public int? UxB { get; set; }

    /// <summary>2026-06-10: URL al producto en el sitio web del proveedor (ej: colombraro.com.ar).</summary>
    [MaxLength(500)]
    public string? UrlWeb { get; set; }

    /// <summary>2026-06-11: URL de la imagen miniatura del producto (extraida de la web del proveedor).</summary>
    [MaxLength(500)]
    public string? ImagenUrl { get; set; }

    /// <summary>2026-06-11: descripcion larga del producto en la web del proveedor.</summary>
    [MaxLength(2000)]
    public string? DescripcionWeb { get; set; }

    /// <summary>2026-06-11: ficha tecnica como JSON {clave: valor} (Material, Medidas, Volumen, etc).</summary>
    [MaxLength(2000)]
    public string? EspecificacionesJson { get; set; }

    /// <summary>2026-06-11: fecha del ultimo scraping exitoso.</summary>
    public DateTime? ScrapedAt { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public DateTime? LastImportAt { get; set; }
}
