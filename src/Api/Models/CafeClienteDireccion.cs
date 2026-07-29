using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-07-29: Direcciones de ENTREGA de un cliente. Un cliente puede tener varias
/// (ej: "Depósito Canning", "Local centro", "Sucursal Boulevard") y al hacer una venta
/// se elige cuál es la de esa entrega. La columna vieja Cafe_Clientes.DomicilioEntrega
/// sigue existiendo como fallback (dirección principal) para clientes que no cargaron lista.
/// </summary>
[Table("Cafe_ClienteDirecciones")]
public class CafeClienteDireccion
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int ClienteId { get; set; }

    /// <summary>Nombre para reconocerla en el desplegable (ej: "Depósito Canning", "Local centro").</summary>
    [MaxLength(120)]
    public string? Etiqueta { get; set; }

    [Required, MaxLength(300)]
    public string Direccion { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? EntreCalles { get; set; }

    [MaxLength(150)]
    public string? Localidad { get; set; }

    [MaxLength(150)]
    public string? Ciudad { get; set; }

    [MaxLength(20)]
    public string? Cp { get; set; }

    [MaxLength(50)]
    public string? Telefono { get; set; }

    /// <summary>Enlace corto de Google Maps al pin exacto de esta dirección.</summary>
    [MaxLength(500)]
    public string? MapeoLink { get; set; }

    [Column(TypeName = "decimal(10,7)")]
    public decimal? MapeoLat { get; set; }

    [Column(TypeName = "decimal(10,7)")]
    public decimal? MapeoLng { get; set; }

    /// <summary>La que viene pre-elegida por defecto al cargar una venta de este cliente.</summary>
    public bool EsPrincipal { get; set; } = false;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
