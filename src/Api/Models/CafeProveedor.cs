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

    /// <summary>09/09/2026 — Si está tildado, este proveedor aparece como destinatario posible de
    /// un COBRO REDIRIGIDO (el cliente le paga a él y eso cancela lo que le debemos).
    /// Arranca apagado a propósito: hay 560 proveedores activos y elegir de una lista así es
    /// pedir un dedazo. Sólo aparecen los que se habilitan a mano.</summary>
    public bool AceptaRedirigido { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
