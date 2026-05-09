using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Nom_Empleados")]
public class NomEmpleado
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Nombre { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? Documento { get; set; }

    [MaxLength(100)]
    public string? Puesto { get; set; }

    public DateTime FechaIngreso { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal SueldoBase { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal ValorHora { get; set; }

    [Column(TypeName = "decimal(8,2)")]
    public decimal? ComisionPorcentaje { get; set; }

    // Tarifa que el empleado cobra por cada kg de café vendido. Se multiplica
    // por KgCafe de la liquidacion para calcular la Comision del mes.
    [Column(TypeName = "decimal(18,2)")]
    public decimal ComisionPorKg { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
