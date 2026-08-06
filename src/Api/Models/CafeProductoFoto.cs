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

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
