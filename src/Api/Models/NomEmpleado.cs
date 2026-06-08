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

    // Bono fijo mensual del empleado (algunos lo tienen fijo, ej: $100.000).
    // Se pre-carga en el campo Bonos al crear una liquidacion nueva — el usuario puede sobrescribir.
    [Column(TypeName = "decimal(18,2)")]
    public decimal BonoFijo { get; set; }

    // 2026-06-08: modalidad de pago. "mensual" (default) o "diario" (cobra por dia trabajado).
    // Si es "diario", el SueldoBase de la liquidacion se calcula como DiasTrabajados * JornalDiario.
    [Required, MaxLength(20)]
    public string ModalidadSueldo { get; set; } = "mensual";

    // 2026-06-08: jornal diario en pesos. Solo aplica si ModalidadSueldo == "diario".
    [Column(TypeName = "decimal(18,2)")]
    public decimal JornalDiario { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
