using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Viajes_Empleados")]
public class ViajesEmpleado
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(120)]
    public string Nombre { get; set; } = string.Empty;

    /// <summary>Token publico (GUID) que va en el URL del empleado. Permite acceso sin login.</summary>
    [Required, MaxLength(64)]
    public string Token { get; set; } = string.Empty;

    /// <summary>Tarifa que cobra el empleado por viaje en CABA. Default $6.000 (puede variar).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal TarifaCABA { get; set; } = 6000m;

    /// <summary>Tarifa que cobra el empleado por viaje en Provincia / Conurbano. Default $8.000.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal TarifaPCIA { get; set; } = 8000m;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public List<ViajesRegistro> Registros { get; set; } = new();
    public List<ViajesPago> Pagos { get; set; } = new();
}

[Table("Viajes_Registros")]
public class ViajesRegistro
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int EmpleadoId { get; set; }

    [ForeignKey(nameof(EmpleadoId))]
    public ViajesEmpleado? Empleado { get; set; }

    [Column(TypeName = "date")]
    public DateTime Fecha { get; set; }

    public int CantidadCABA { get; set; }
    public int CantidadPCIA { get; set; }

    /// <summary>Tarifa CABA vigente el dia que se cargo este viaje. Se congela al crear el registro
    /// para que cambiar la tarifa del empleado NO recalcule la deuda historica.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal TarifaCABA { get; set; }

    /// <summary>Tarifa PCIA vigente el dia que se cargo este viaje. Se congela al crear el registro.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal TarifaPCIA { get; set; }

    [MaxLength(500)]
    public string? Anotaciones { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

[Table("Viajes_Pagos")]
public class ViajesPago
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int EmpleadoId { get; set; }

    [ForeignKey(nameof(EmpleadoId))]
    public ViajesEmpleado? Empleado { get; set; }

    [Column(TypeName = "date")]
    public DateTime Fecha { get; set; }

    [Required, MaxLength(300)]
    public string Descripcion { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Importe { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
