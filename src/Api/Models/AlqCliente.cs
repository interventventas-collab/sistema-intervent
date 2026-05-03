using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Alq_Clientes")]
public class AlqCliente
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Nombre { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Empresa { get; set; }

    [MaxLength(50)]
    public string? DniCuit { get; set; }

    [MaxLength(50)]
    public string? Telefono { get; set; }

    [MaxLength(50)]
    public string? Telefono2 { get; set; }

    [MaxLength(200)]
    public string? Email { get; set; }

    [MaxLength(300)]
    public string? DireccionDefault { get; set; }

    [MaxLength(20)]
    public string? Piso { get; set; }

    [MaxLength(20)]
    public string? Depto { get; set; }

    [MaxLength(100)]
    public string? Barrio { get; set; }

    [MaxLength(200)]
    public string? EntreCalles { get; set; }

    [MaxLength(1000)]
    public string? Notas { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
