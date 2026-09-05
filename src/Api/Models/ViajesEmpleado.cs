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

    /// <summary>
    /// 2026-09-04 (Nacho): el empleado NO carga sus viajes a mano — se los cuenta el sistema.
    /// Cada parada del mapa que quedo ENTREGADA le suma un viaje a la tarifa <see cref="TarifaViaje"/>.
    /// false = modo viejo (el empleado tipea cuantos viajes hizo en CABA y en PCIA). Walter sigue asi.
    /// </summary>
    public bool ModoAutomatico { get; set; }

    /// <summary>Chofer del mapa (MapeoDrivers.Id) del que se cuentan las entregas. Solo en modo automatico.</summary>
    public int? MapeoDriverId { get; set; }

    /// <summary>
    /// Tarifa PLANA por entrega en modo automatico: da lo mismo Flex, venta de cafe, CABA o Provincia.
    /// Se congela en cada entrega al momento de contarla, asi cambiarla no recalcula la deuda vieja.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal TarifaViaje { get; set; } = 8500m;

    /// <summary>
    /// Con qué empleado de nómina es la misma persona (05/09/2026). Nacho es Walter Ignacio
    /// Carrizo: cobra sueldo fijo Y por entrega. Sin esto, redirigir una cobranza "a Nacho" no
    /// sabría que es el mismo tipo que cobra el sueldo.
    /// </summary>
    public int? NomEmpleadoId { get; set; }

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

/// <summary>
/// 2026-09-04: UN viaje ya contado del empleado que cobra por entrega (modo automatico).
///
/// Por que una tabla propia y no calcularlo al vuelo sobre las paradas del mapa:
///   - las paradas del mapa se pueden borrar o re-asignar; la plata que se le debe a alguien no
///     puede depender de eso;
///   - la tarifa se CONGELA acá al contar la entrega, asi subirle el precio manana no recalcula
///     lo que ya se le pago;
///   - lo LIQUIDADO queda cerrado: una vez pagado, ese viaje no se toca mas (ver LiquidadoPagoId).
///
/// StopId es la clave contra duplicados: una parada suma UNA sola vez, aunque MercadoLibre confirme
/// la entrega dos veces o el sincronizador corra mil veces. Los ajustes a mano van con StopId NULL.
/// </summary>
[Table("Viajes_Entregas")]
public class ViajesEntrega
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int EmpleadoId { get; set; }

    [ForeignKey(nameof(EmpleadoId))]
    public ViajesEmpleado? Empleado { get; set; }

    /// <summary>Parada del mapa (MapeoStops.Id) que genero este viaje. NULL = ajuste cargado a mano.</summary>
    public int? StopId { get; set; }

    /// <summary>Dia del REPARTO (fecha argentina). Si MeLi confirma la entrega a la noche o al otro
    /// dia, el viaje igual cae en el dia que salio a repartir.</summary>
    [Column(TypeName = "date")]
    public DateTime Fecha { get; set; }

    /// <summary>Lo que se le paga por esta entrega. Congelado al momento de contarla.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Tarifa { get; set; }

    /// <summary>flex | me1 | venta_cafe | alquiler | visita | manual</summary>
    [MaxLength(20)]
    public string Origen { get; set; } = "manual";

    [MaxLength(300)]
    public string? Direccion { get; set; }

    [MaxLength(150)]
    public string? Cliente { get; set; }

    /// <summary>Hora en que se dio por entregada (la que informo MeLi o la marca del repartidor).</summary>
    public DateTime? EntregadoAt { get; set; }

    /// <summary>Ajuste a mano: por que se le suma o se le resta (puede ser importe negativo).</summary>
    [MaxLength(300)]
    public string? Detalle { get; set; }

    /// <summary>Pago (Viajes_Pagos.Id) con el que se liquido este viaje. NULL = todavia se le debe.
    /// Un viaje liquidado NO se borra ni se recalcula aunque cambie la parada de origen.</summary>
    public int? LiquidadoPagoId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
