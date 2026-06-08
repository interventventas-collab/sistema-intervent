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

    // ─── 2026-06-03: Ciclo de liquidacion + flags granulares de visibilidad en el celular ───
    /// <summary>Si esta en true, el celular del empleado muestra las horas extras (+/- al lado del dia
    /// y total extras del ciclo).</summary>
    public bool MostrarExtrasAlEmpleado { get; set; } = false;

    /// <summary>2026-06-03 v2: si true, muestra el cuadro grande del mes/ciclo en el celular.
    /// Si false, ese cuadro no se muestra.</summary>
    public bool MostrarCuadroCiclo { get; set; } = false;

    /// <summary>2026-06-03 v2: si true, en "Ultimos 7 dias" muestra "11,5 h" en azul a la derecha de cada dia.
    /// Si false, solo se ve la fecha y el horario "08:00 → 19:30".</summary>
    public bool MostrarHorasTrabajadasDia { get; set; } = false;

    /// <summary>2026-06-03 v3: piloto - flag por empleado para probar el modo nuevo de fichada.
    /// Si el toggle GLOBAL esta OFF pero este flag esta ON, solo ESTE empleado pasa por validacion
    /// de WiFi. Permite testear con un empleado de prueba sin afectar a los demas.</summary>
    public bool ProbarModoNuevoFichada { get; set; } = false;

    /// <summary>2026-06-03 v4: controla si el empleado aparece en la pantalla /fichador (kiosco).
    /// Default true (compatibilidad). Si false, no se ve en el grid del kiosco aunque este activo.
    /// Util para sacar a empleados de prueba, ex-empleados que siguen activos en el sistema, etc.</summary>
    public bool MostrarEnFichador { get; set; } = true;

    /// <summary>2026-06-08: si TRUE, la página personal /he/{slug}/horario es SOLO INFORMATIVA
    /// para este empleado — puede ver sus horas y el cuadro del ciclo pero NO puede marcar
    /// entrada/salida desde ahí. Pensado para empleados que tienen que fichar SÍ o SÍ desde
    /// el kiosco (/fichador) con huella o PIN, para evitar el "fichaje fantasma" desde el celular.</summary>
    public bool SoloInformativo { get; set; } = false;

    /// <summary>Dia del mes (1-31) en que arranca el ciclo de liquidacion. NULL = mes calendario (1 al fin del mes).
    /// Ej: 16 -> el ciclo va del 16 de un mes al 15 del siguiente (o al CicloDiaFin si se configuro).</summary>
    public int? CicloDiaInicio { get; set; }

    /// <summary>Dia del mes (1-31) en que termina el ciclo. NULL = fin de mes calendario.
    /// Si el dia no existe en el mes (ej. 31 en febrero), se usa el ultimo dia disponible del mes.</summary>
    public int? CicloDiaFin { get; set; }

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

    /// <summary>Calcula el ciclo de liquidacion ACTUAL para este empleado dado el dia de hoy.
    /// Devuelve (desde, hasta) inclusive, y un label corto para mostrar ("CICLO 16/05 → 15/06" o "ESTE MES (junio)").
    ///
    /// Reglas:
    /// - Si CicloDiaInicio o CicloDiaFin estan en null -> ciclo = mes calendario (1 al fin del mes).
    /// - Si CicloDiaInicio tiene valor (ej. 16) -> el ciclo arranca el dia 16. El "hasta" se calcula con CicloDiaFin
    ///   (o el dia anterior a Inicio si Fin es null). Ej: Inicio=16, Fin=15 -> 16 → 15 del mes siguiente.
    /// - Si el dia configurado no existe en el mes (ej. 31 en febrero), se usa el ULTIMO DIA disponible del mes.
    /// - "Hoy" determina en que ciclo estamos parados: si hoy >= dia inicio, ciclo arranca este mes; si no, arranca mes pasado.
    /// </summary>
    public (DateTime Desde, DateTime Hasta, string Label) CicloActual(DateTime hoy)
    {
        var hoyDate = hoy.Date;
        // ─── Caso simple: mes calendario ───
        if (!CicloDiaInicio.HasValue || !CicloDiaFin.HasValue)
        {
            var desde = new DateTime(hoyDate.Year, hoyDate.Month, 1);
            var hasta = desde.AddMonths(1).AddDays(-1);
            var mesNombre = desde.ToString("MMMM", new System.Globalization.CultureInfo("es-AR"));
            return (desde, hasta, $"ESTE MES ({mesNombre})");
        }
        // ─── Ciclo personalizado ───
        int diaIni = Math.Clamp(CicloDiaInicio.Value, 1, 31);
        int diaFin = Math.Clamp(CicloDiaFin.Value, 1, 31);
        // Determinar de que mes arranca el ciclo actual.
        // Si hoy.Day >= diaIni -> arranca este mes. Si no -> arranca mes pasado.
        // Excepcion: si diaIni > dias del mes de hoy (ej. diaIni=31 y mes tiene 28), tambien arranca este mes.
        DateTime desdeReal;
        var diasMesActual = DateTime.DaysInMonth(hoyDate.Year, hoyDate.Month);
        var diaIniEnMesActual = Math.Min(diaIni, diasMesActual);
        if (hoyDate.Day >= diaIniEnMesActual)
        {
            desdeReal = new DateTime(hoyDate.Year, hoyDate.Month, diaIniEnMesActual);
        }
        else
        {
            var mesAnt = hoyDate.AddMonths(-1);
            var diasMesAnt = DateTime.DaysInMonth(mesAnt.Year, mesAnt.Month);
            desdeReal = new DateTime(mesAnt.Year, mesAnt.Month, Math.Min(diaIni, diasMesAnt));
        }
        // Calculo "hasta": mes siguiente al de desdeReal, dia = diaFin (cap al ultimo dia del mes).
        var mesHasta = desdeReal.AddMonths(1);
        var diasMesHasta = DateTime.DaysInMonth(mesHasta.Year, mesHasta.Month);
        var hastaReal = new DateTime(mesHasta.Year, mesHasta.Month, Math.Min(diaFin, diasMesHasta));
        // Si diaFin >= diaIni (ej. 1 al 28 -> ciclo dentro del mismo mes), el "hasta" es del mismo mes que desdeReal.
        if (diaFin >= diaIni)
        {
            hastaReal = new DateTime(desdeReal.Year, desdeReal.Month, Math.Min(diaFin, DateTime.DaysInMonth(desdeReal.Year, desdeReal.Month)));
        }
        var label = $"CICLO {desdeReal:dd/MM} → {hastaReal:dd/MM}";
        return (desdeReal, hastaReal, label);
    }
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
