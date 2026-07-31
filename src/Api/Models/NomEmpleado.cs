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

    // 2026-07-31: datos personales / administrativos de la ficha del empleado.
    // Fecha de alta (distinta de FechaIngreso — la puede usar el usuario como fecha de alta formal).
    public DateTime? FechaAlta { get; set; }

    // Datos bancarios (para pagar el sueldo por transferencia).
    [MaxLength(150)]
    public string? Banco { get; set; }
    [MaxLength(60)]
    public string? Cbu { get; set; }
    [MaxLength(120)]
    public string? Alias { get; set; }

    // Contacto.
    [MaxLength(300)]
    public string? Domicilio { get; set; }
    [MaxLength(60)]
    public string? TelefonoContacto { get; set; }
    [MaxLength(60)]
    public string? TelefonoFamiliar { get; set; }
    [MaxLength(200)]
    public string? Email { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
