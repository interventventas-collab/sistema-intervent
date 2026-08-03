using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-08-03: Remitentes alternativos para el rotulo/etiqueta de transporte.
///
/// Por defecto el rotulo usa los "Datos del negocio" de Cafe_Settings (PALANICA HERMANOS SRL),
/// que son la unica fuente de verdad y no se duplican aca. Esta tabla existe SOLO para cuando
/// se despacha en nombre de otra sociedad/remitente distinto al negocio principal.
/// Arranca vacia.
/// </summary>
[Table("Cafe_Remitentes")]
public class CafeRemitente
{
    [Key]
    public int Id { get; set; }

    /// <summary>Razon social (legal). Ej: "PALANICA HERMANOS S.R.L.".</summary>
    [Required, MaxLength(200)]
    public string Nombre { get; set; } = string.Empty;

    /// <summary>CUIT del remitente.</summary>
    [MaxLength(20)]
    public string? Cuit { get; set; }

    /// <summary>Nombre de fantasia / comercial. Ej: "INTERVENT".</summary>
    [MaxLength(150)]
    public string? NombreFantasia { get; set; }

    [MaxLength(300)]
    public string? Direccion { get; set; }

    [MaxLength(100)]
    public string? Telefono { get; set; }

    [MaxLength(150)]
    public string? Localidad { get; set; }

    [MaxLength(150)]
    public string? Provincia { get; set; }

    [MaxLength(20)]
    public string? CodigoPostal { get; set; }

    public bool Activo { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
