using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_Proveedores")]
public class CafeProveedor
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Nombre { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Contacto { get; set; }

    [MaxLength(50)]
    public string? Telefono { get; set; }

    [MaxLength(200)]
    public string? Email { get; set; }

    [MaxLength(500)]
    public string? Notas { get; set; }

    /// <summary>CUIT/CUIL del proveedor. Indice unico filtrado (solo cuando no es null).</summary>
    [MaxLength(20)]
    public string? Cuit { get; set; }

    /// <summary>Categoria impositiva: RI / MO / EX / CF / etc.</summary>
    [MaxLength(20)]
    public string? CategoriaImpositiva { get; set; }

    [MaxLength(300)]
    public string? Direccion { get; set; }

    [MaxLength(20)]
    public string? CodigoPostal { get; set; }

    [MaxLength(100)]
    public string? Provincia { get; set; }

    [MaxLength(100)]
    public string? Ciudad { get; set; }

    [MaxLength(200)]
    public string? Web { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
