using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Nom_Liquidaciones")]
public class NomLiquidacion
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int EmpleadoId { get; set; }

    [ForeignKey(nameof(EmpleadoId))]
    public NomEmpleado? EmpleadoNav { get; set; }

    public int Anio { get; set; }
    public int Mes { get; set; }

    // Insumos del mes
    [Column(TypeName = "decimal(8,2)")]
    public decimal HorasTrabajadas { get; set; }

    [Column(TypeName = "decimal(8,2)")]
    public decimal HorasExtra { get; set; }

    [Column(TypeName = "decimal(8,2)")]
    public decimal RecargoHsExtraPct { get; set; } = 0m;

    [Column(TypeName = "decimal(5,2)")]
    public decimal DiasAusencia { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal DiasVacaciones { get; set; }

    // Kg de café vendidos por el empleado en el mes. Se multiplica por
    // empleado.ComisionPorKg para calcular Comision automáticamente.
    [Column(TypeName = "decimal(18,2)")]
    public decimal KgCafe { get; set; }

    // 2026-06-08: días trabajados en el mes. Solo aplica si el empleado tiene
    // ModalidadSueldo == "diario". En ese caso, SueldoBase = DiasTrabajados * empleado.JornalDiario.
    [Column(TypeName = "decimal(5,2)")]
    public decimal DiasTrabajados { get; set; }

    // Conceptos calculados
    [Column(TypeName = "decimal(18,2)")]
    public decimal SueldoBase { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal MontoHsExtra { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Comision { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Bonos { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Aguinaldo { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal DescuentoFaltas { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Adelantos { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal OtrosDescuentos { get; set; }

    /// <summary>2026-11-11: suma de los extras cargados aparte (Horas extras, Comisiones, Feriados,
    /// Aguinaldo y Plus — tabla Nom_Extras). Se recalcula cada vez que se carga/cambia/borra uno.
    /// Entra en TotalGanado para que el saldo y "¿Cuánto debo pagar?" lo sumen.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal MontoExtras { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalGanado { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalDescuentos { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal NetoAPagar { get; set; }

    [MaxLength(20)]
    public string Estado { get; set; } = "pendiente";

    [MaxLength(1000)]
    public string? Notas { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<NomPago> Pagos { get; set; } = new List<NomPago>();
    public ICollection<NomExtra> Extras { get; set; } = new List<NomExtra>();
}

[Table("Nom_Pagos")]
public class NomPago
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int LiquidacionId { get; set; }

    [ForeignKey(nameof(LiquidacionId))]
    public NomLiquidacion? LiquidacionNav { get; set; }

    public DateTime FechaPago { get; set; }

    [Required, MaxLength(50)]
    public string Metodo { get; set; } = "efectivo";

    [Column(TypeName = "decimal(18,2)")]
    public decimal Monto { get; set; }

    // Concepto del pago — para diferenciar en concepto de qué se paga.
    // Valores: sueldo | comision_cafe | horas_extra | bono | adelanto | aguinaldo | otro
    [Required, MaxLength(30)]
    public string Concepto { get; set; } = "sueldo";

    // Detalle libre del concepto (ej: "kg de cafe vendidos en marzo: 120kg").
    [MaxLength(500)]
    public string? Detalle { get; set; }

    [MaxLength(500)]
    public string? Notas { get; set; }

    /// <summary>
    /// De qué caja sale la plata (05/09/2026). Hasta ahora el sueldo se anotaba al costado y no
    /// descontaba de ningún lado, asi que la caja sólo subía. Si viene en NULL el pago no toca
    /// ninguna caja, que es como quedaron los 154 pagos viejos.
    /// </summary>
    public int? CajaId { get; set; }

    /// <summary>El renglon que este pago genero en la caja, para poder deshacerlo si se borra.</summary>
    public int? CajaMovimientoId { get; set; }

    /// <summary>2026-11-11: si el pago es de un extra (Nom_Extras), cuál. NULL = pago de la liquidación.</summary>
    public int? ExtraId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 2026-11-11: Horas extras, Comisiones, Feriados trabajados, Aguinaldo y Plus, cada uno por su lado
/// (pedido del dueño: "no me gusta como esta todo junto"). Cada renglón cuelga de la liquidación
/// del mes del empleado (aguinaldo: junio o diciembre) para que el total del mes los sume, pero se
/// cargan y se pagan desde su propio módulo. El monto lo deciden ellos: el sistema solo propone.
/// </summary>
[Table("Nom_Extras")]
public class NomExtra
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int LiquidacionId { get; set; }
    [ForeignKey(nameof(LiquidacionId))]
    public NomLiquidacion? LiquidacionNav { get; set; }

    public int EmpleadoId { get; set; }
    [ForeignKey(nameof(EmpleadoId))]
    public NomEmpleado? EmpleadoNav { get; set; }

    /// <summary>horas_extra | comision_cafe | feriado | aguinaldo | plus (igual que NomPago.Concepto).</summary>
    [Required, MaxLength(20)]
    public string Tipo { get; set; } = "plus";

    /// <summary>Día al que corresponde (feriado trabajado, día del plus). NULL en los mensuales.</summary>
    public DateTime? Fecha { get; set; }

    /// <summary>Horas (horas extra), kg (comisión) o días del semestre (aguinaldo). Informativo.</summary>
    [Column(TypeName = "decimal(10,2)")]
    public decimal Cantidad { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Monto { get; set; }

    [MaxLength(300)]
    public string? Detalle { get; set; }

    public int? FeriadoId { get; set; }

    /// <summary>2026-11-11 (2ª): cuándo se le va a pagar. Ese día aparece en "Equipo hoy" del inicio.</summary>
    [Column(TypeName = "date")]
    public DateTime? FechaPago { get; set; }

    [MaxLength(120)]
    public string? CargadoPor { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
