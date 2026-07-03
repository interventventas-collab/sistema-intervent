using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Credenciales del Office Banking (empresas) de Banco Galicia para login
/// automatizado por scraping. Se maneja como una única cuenta (una empresa),
/// pero la tabla soporta varias por las dudas. La password se guarda en texto
/// porque el robot Playwright la necesita en runtime (mismo criterio que ArcaAccount).
/// Protegé el acceso a la DB.
/// </summary>
[Table("GaliciaAccounts")]
public class GaliciaAccount
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Usuario del Office Banking (ej "intervent25").</summary>
    [Required, MaxLength(100)]
    public string Usuario { get; set; } = string.Empty;

    /// <summary>Clave del Office Banking. Texto — el scraper la usa.</summary>
    [Required]
    public string Password { get; set; } = string.Empty;

    /// <summary>Alias amigable opcional (ej "PALANICA HERMANOS SRL").</summary>
    [MaxLength(100)]
    public string? Alias { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
