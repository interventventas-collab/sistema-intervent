using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-06-19: Marca/Sitio web. Cada sitio agrupa uno o varios dominios bajo
/// una identidad visual (logo, frase, colores, contactos). Las landings estaticas
/// (frikaf, futuras) consultan el endpoint publico por host para resolver su data.</summary>
[Table("Cafe_Sitios")]
public class CafeSitio
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(120)]
    public string Nombre { get; set; } = string.Empty;

    [Required, MaxLength(60)]
    public string Slug { get; set; } = string.Empty;

    /// <summary>Lista separada por coma. Ej: "frikaf.com.ar,cafefrikaf.com.ar".</summary>
    [MaxLength(500)]
    public string? Dominios { get; set; }

    [MaxLength(400)]
    public string? LogoUrl { get; set; }

    [MaxLength(120)]
    public string? Eyebrow { get; set; }

    [MaxLength(200)]
    public string? Frase { get; set; }

    [MaxLength(40)]
    public string? WhatsApp { get; set; }

    [MaxLength(40)]
    public string? WhatsApp2 { get; set; }

    [MaxLength(200)]
    public string? Instagram { get; set; }

    [MaxLength(200)]
    public string? Facebook { get; set; }

    [MaxLength(20)]
    public string? ColorPrimario { get; set; }

    [MaxLength(20)]
    public string? ColorAcento { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}
