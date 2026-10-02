using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-08-05: estado de la foto de un producto de café A NIVEL SISTEMA (no toca la foto de MeLi).
/// El armador, desde /cafe/preparacion, puede APROBAR una foto (✅) o REPORTARLA como errónea (❌).
/// Como es por producto, apenas uno la marca lo ven todos. Un registro por producto.
/// (Paso 3 va a sumar acá la "foto propia" subida por QR — por eso la tabla es genérica.)
/// </summary>
[Table("Cafe_ProductoFoto")]
public class CafeProductoFoto
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Id del producto de café (Cafe_Productos.Id). Único: un estado por producto.</summary>
    public int CafeProductoId { get; set; }

    /// <summary>"APROBADA" | "REPORTADA" | null (sin marcar todavía).</summary>
    [MaxLength(20)]
    public string? Estado { get; set; }

    /// <summary>Quién dejó la última marca (usuario logueado, típicamente DEPOSITO).</summary>
    [MaxLength(100)]
    public string? Usuario { get; set; }

    /// <summary>Comentario opcional al reportar (ej: "es otro color", "no es este producto").</summary>
    [MaxLength(500)]
    public string? Comentario { get; set; }

    /// <summary>2026-08-05 (Paso 3): nombre del archivo de la FOTO PROPIA subida por el depósito
    /// (por QR desde el celu). Vive en /data/files/producto-fotos. Null = todavía usa la de MeLi.
    /// La foto propia NO toca la de MercadoLibre; es del sistema.</summary>
    [MaxLength(200)]
    public string? FotoPropiaArchivo { get; set; }

    /// <summary>Cuándo se subió la foto propia.</summary>
    public DateTime? FotoPropiaAt { get; set; }

    /// <summary>2026-10-01: foto ORIGINAL con la tela verde/azul de la que salió la foto propia (Fotos
    /// chroma). Mismo directorio. Null si la foto propia se subió tal cual (sin sacarle el fondo).</summary>
    [MaxLength(200)]
    public string? FotoOriginalArchivo { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 2026-08-05 (Paso 3): token de un solo uso para subir la foto de un producto desde el celu
/// escaneando el QR (sin login). El depósito genera el token en la compu, el celu abre
/// /subir-foto-producto/{token}, saca/sube la foto y el token queda usado.
/// </summary>
[Table("Cafe_ProductoFotoToken")]
public class CafeProductoFotoToken
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(64)]
    public string Token { get; set; } = string.Empty;

    public int CafeProductoId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(30);
    public DateTime? UsedAt { get; set; }
}

/// <summary>
/// 2026-10-01: GALERÍA de fotos con tela verde/azul (Fotos chroma). Se sacan muchas seguidas desde el
/// celu SIN elegir producto; cada una queda procesada (fondo blanco 1200x1200) y después se asigna a un
/// producto (foto propia), se agrega a una publicación de MeLi, se descarga o se borra.
/// Archivos en /data/files/producto-fotos con prefijo "gal-" (así los sirve el mismo /img público).
/// </summary>
[Table("Cafe_FotoGaleria")]
public class CafeFotoGaleria
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Foto ya procesada (JPG 1200x1200 sobre blanco). Null si no se pudo procesar (ver Error).</summary>
    [MaxLength(200)]
    public string? Archivo { get; set; }

    /// <summary>Foto original, con la tela.</summary>
    [Required, MaxLength(200)]
    public string OriginalArchivo { get; set; } = string.Empty;

    /// <summary>Opciones con las que se procesó: "auto" | "verde" | "azul", tolerancia y suavizado 0-100.</summary>
    [MaxLength(10)]
    public string Color { get; set; } = "auto";
    public int Tolerancia { get; set; } = 50;
    public int Suavizado { get; set; } = 40;

    /// <summary>Color de tela que se terminó usando ("verde" | "azul").</summary>
    [MaxLength(10)]
    public string? ColorUsado { get; set; }

    [MaxLength(600)]
    public string? Aviso { get; set; }

    [MaxLength(300)]
    public string? Error { get; set; }

    [MaxLength(100)]
    public string? Usuario { get; set; }

    /// <summary>Producto al que se le puso como foto propia (la última vez).</summary>
    public int? ProductoId { get; set; }

    /// <summary>Publicaciones de MeLi a las que se agregó, separadas por coma.</summary>
    [MaxLength(400)]
    public string? Publicaciones { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
