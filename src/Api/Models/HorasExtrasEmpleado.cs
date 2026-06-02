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

    // ─── 2026-06-02: Jornada laboral configurable por día de la semana ───
    // Cuántas horas trabaja el empleado en cada día. 0 = no trabaja ese día.
    // El admin la ve en la ficha del empleado y la edita.
    // Se usa para calcular extras (trabajadas - jornada) y acumulados semanal/mensual.
    [Column(TypeName = "decimal(4,2)")] public decimal HorasLunes { get; set; } = 8m;
    [Column(TypeName = "decimal(4,2)")] public decimal HorasMartes { get; set; } = 8m;
    [Column(TypeName = "decimal(4,2)")] public decimal HorasMiercoles { get; set; } = 8m;
    [Column(TypeName = "decimal(4,2)")] public decimal HorasJueves { get; set; } = 8m;
    [Column(TypeName = "decimal(4,2)")] public decimal HorasViernes { get; set; } = 8m;
    [Column(TypeName = "decimal(4,2)")] public decimal HorasSabado { get; set; } = 5m;
    [Column(TypeName = "decimal(4,2)")] public decimal HorasDomingo { get; set; } = 0m;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public List<HorasExtrasRegistro> Registros { get; set; } = new();

    /// <summary>Devuelve las horas configuradas para un dia de la semana. Helper para reportes.</summary>
    public decimal HorasParaDia(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => HorasLunes,
        DayOfWeek.Tuesday => HorasMartes,
        DayOfWeek.Wednesday => HorasMiercoles,
        DayOfWeek.Thursday => HorasJueves,
        DayOfWeek.Friday => HorasViernes,
        DayOfWeek.Saturday => HorasSabado,
        DayOfWeek.Sunday => HorasDomingo,
        _ => 0m
    };
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
