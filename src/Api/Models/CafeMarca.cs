using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_Marcas")]
public class CafeMarca
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Nombre { get; set; } = string.Empty;

    /// <summary>Proveedor principal de la marca (opcional).</summary>
    public int? ProveedorId { get; set; }

    [ForeignKey(nameof(ProveedorId))]
    public CafeProveedor? ProveedorNav { get; set; }

    [MaxLength(500)]
    public string? Notas { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Marcas propias (Frikaf) bloquean descuentos: siempre se cobra 100%.</summary>
    public bool BloqueaDescuento { get; set; } = false;

    /// <summary>% de margen sobre el costo para calcular el "PVP por %" de productos OTROS de esta marca.
    /// Default 100% (PVP = costo × 2).</summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "decimal(7,2)")]
    public decimal MargenPctSobreCosto { get; set; } = 100m;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
