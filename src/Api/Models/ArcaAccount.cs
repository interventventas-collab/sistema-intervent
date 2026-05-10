using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Cuenta de ARCA (ex AFIP) para login automatizado vía scraping.
/// Algunos contribuyentes loguean con un CUIT distinto al de la cuenta y luego
/// "representan" al CUIT objetivo — por eso CuitLogin es opcional y separado.
/// Si CuitLogin es null/vacío, se asume que se loguea con el mismo Cuit.
/// </summary>
[Table("ArcaAccounts")]
public class ArcaAccount
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>CUIT objetivo (11 dígitos sin guiones).</summary>
    [Required, MaxLength(11)]
    public string Cuit { get; set; } = string.Empty;

    /// <summary>CUIT con el que se loguea si es distinto al objetivo (opcional).</summary>
    [MaxLength(11)]
    public string? CuitLogin { get; set; }

    /// <summary>Alias amigable opcional (ej: "Estudio contable", "Empresa principal").</summary>
    [MaxLength(100)]
    public string? Alias { get; set; }

    /// <summary>Contraseña del CUIT que loguea. Se guarda como texto — el scraper la usa.</summary>
    [Required]
    public string Password { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
