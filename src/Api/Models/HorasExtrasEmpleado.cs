using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("HorasExtras_Empleados")]
public class HorasExtrasEmpleado
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(120)]
    public string Nombre { get; set; } = string.Empty;

    /// <summary>Token publico (GUID) — legacy. Antes era la credencial del URL.
    /// Ahora el URL usa {slug-del-nombre}/horario/{ultimos3DniDelEmpleado}.
    /// Lo dejamos en la tabla para no perder histórico, pero ya no es funcional.</summary>
    [Required, MaxLength(64)]
    public string Token { get; set; } = string.Empty;

    /// <summary>Últimos 3 dígitos del DNI del empleado — clave corta para el link público.
    /// Si está vacío, el link no funciona (el admin debe cargarlo).</summary>
    [MaxLength(3)]
    public string? DniUltimos3 { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public List<HorasExtrasRegistro> Registros { get; set; } = new();
}

[Table("HorasExtras_Registros")]
public class HorasExtrasRegistro
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int EmpleadoId { get; set; }

    [ForeignKey(nameof(EmpleadoId))]
    public HorasExtrasEmpleado? Empleado { get; set; }

    /// <summary>Fecha calendario a la que corresponde la carga (Date, sin hora).</summary>
    [Column(TypeName = "date")]
    public DateTime Fecha { get; set; }

    /// <summary>Horas extras cargadas. Soporta fracciones (1.5 = 1 hora 30 min).</summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal Cantidad { get; set; }

    [MaxLength(500)]
    public string? Observaciones { get; set; }

    /// <summary>Hora de entrada (opcional). Si la cargó el empleado, queda registrada.</summary>
    [Column(TypeName = "time")]
    public TimeSpan? HoraEntrada { get; set; }

    /// <summary>Hora de salida (opcional). Si la cargó el empleado, queda registrada.</summary>
    [Column(TypeName = "time")]
    public TimeSpan? HoraSalida { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
