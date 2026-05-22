using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Catalogo maestro de bancos. Centraliza los nombres para evitar la sopa de variantes
/// tipica del texto libre ("Galicia" / "GALICIA" / "Banco Galicia SA").
///
/// Se usa como FK desde Cafe_Cheques (cheques manuales) y opcionalmente desde Cafe_ChequesBanco
/// (e-cheqs del extracto bancario, que pueden vincularse al banco canonico).
/// </summary>
[Table("Cafe_Bancos")]
public class CafeBanco
{
    [Key]
    public int Id { get; set; }

    /// <summary>Nombre canonico — el "oficial". Ej: "Banco Galicia y Buenos Aires".</summary>
    [Required, MaxLength(150)]
    public string Nombre { get; set; } = string.Empty;

    /// <summary>Nombre corto / alias para mostrar en pantallas y selects. Ej: "Galicia".
    /// Si es null, se muestra Nombre.</summary>
    [MaxLength(50)]
    public string? Alias { get; set; }

    /// <summary>CUIT del banco (opcional). Util para conciliacion automatica con extractos.</summary>
    [MaxLength(13)]
    public string? Cuit { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Orden para mostrar en dropdowns (menor = mas arriba). Default 0.</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
