using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-10-30: FICHA TÉCNICA de un café (pedido: "una base fija para no armar cada ficha de cero").
/// Una sola ficha por café: se edita desde Catálogo → Fichas técnicas o desde la ficha del producto,
/// y es la misma. Puede existir sin producto (ej: una muestra que todavía no se vende).
/// Los textos son libres a propósito (altura "1.600 – 1.900 msnm", proceso "Natural fermentado 40 h").
/// </summary>
[Table("Cafe_FichasTecnicas")]
public class CafeFichaTecnica
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Producto del sistema al que pertenece (único). Null = ficha suelta.</summary>
    public int? CafeProductoId { get; set; }

    [Required, MaxLength(150)]
    public string Nombre { get; set; } = string.Empty;

    /// <summary>"ORIGEN" (origen único) | "BLEND". En un blend los datos de origen van por cada parte (ComponentesJson).</summary>
    [MaxLength(10)]
    public string Tipo { get; set; } = "ORIGEN";

    /// <summary>"Arábica" | "Robusta" | "Arábica + Robusta".</summary>
    [MaxLength(30)] public string? Especie { get; set; }
    /// <summary>Sello corto destacado al lado del nombre (ej: "FUNKY", "CLÁSICO").</summary>
    [MaxLength(30)] public string? Perfil { get; set; }

    [MaxLength(80)] public string? Pais { get; set; }
    [MaxLength(150)] public string? Region { get; set; }
    [MaxLength(150)] public string? Finca { get; set; }
    [MaxLength(80)] public string? Altura { get; set; }
    [MaxLength(150)] public string? Variedad { get; set; }
    [MaxLength(150)] public string? Proceso { get; set; }
    [MaxLength(60)] public string? Tueste { get; set; }
    [MaxLength(150)] public string? IdealPara { get; set; }
    [MaxLength(20)] public string? Puntaje { get; set; }

    [MaxLength(2000)] public string? Descripcion { get; set; }
    [MaxLength(300)] public string? FraganciaAroma { get; set; }
    [MaxLength(300)] public string? Sabor { get; set; }
    [MaxLength(300)] public string? Retrogusto { get; set; }

    /// <summary>Familias de la rueda de sabores marcadas, separadas por coma (keys de FichaTecnicaCatalogo.Rueda).</summary>
    [MaxLength(300)] public string? Rueda { get; set; }

    /// <summary>Balance de 1 a 5 (admite medios: 3,5).</summary>
    [Column(TypeName = "decimal(3,1)")] public decimal? Cuerpo { get; set; }
    [Column(TypeName = "decimal(3,1)")] public decimal? Acidez { get; set; }
    [Column(TypeName = "decimal(3,1)")] public decimal? Dulzor { get; set; }

    /// <summary>Partes del blend: JSON [{Pais, Pct, Region, Variedad, Proceso, Finca, Altura}].</summary>
    public string? ComponentesJson { get; set; }

    /// <summary>Datos marcados "no aplica" (keys separadas por coma): no cuentan como faltante ni salen en el PDF.</summary>
    [MaxLength(400)] public string? NoAplica { get; set; }

    /// <summary>Archivos en /data/files/producto-fotos (los sirve /api/public/producto-foto/img/).</summary>
    [MaxLength(200)] public string? FotoCafeArchivo { get; set; }
    [MaxLength(200)] public string? FotoEtiquetaArchivo { get; set; }

    public bool IsActive { get; set; } = true;

    [MaxLength(100)] public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
