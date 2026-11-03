using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-11-03: "Tareas del depósito". El dueño arma una tarea en la compu (/deposito/tareas), se la
/// asigna a un empleado (o a cualquiera) y le aparece en el celu, arriba de todo, en "Tareas pendientes".
/// Tipos: CONTAR (cuentan a ciegas, el stock NO cambia hasta que el dueño acepta el conteo),
/// UBICACION (dónde está), BARCODE (código de barras) y LIBRE (texto + "✓ Listo", sin productos).
/// </summary>
[Table("Dep_Tareas")]
public class DepTarea
{
    [Key]
    public int Id { get; set; }

    /// <summary>CONTAR | UBICACION | BARCODE | LIBRE</summary>
    [Required, MaxLength(20)]
    public string Tipo { get; set; } = "CONTAR";

    [Required, MaxLength(200)]
    public string Titulo { get; set; } = "";

    [MaxLength(1000)]
    public string? Comentario { get; set; }

    /// <summary>Nom_Empleados.Id. Null = la puede hacer cualquiera.</summary>
    public int? EmpleadoId { get; set; }

    /// <summary>HOY | SEMANA | SIN_APURO</summary>
    [Required, MaxLength(20)]
    public string Para { get; set; } = "HOY";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(120)]
    public string? CreatedBy { get; set; }

    public DateTime? TerminadaAt { get; set; }

    [MaxLength(200)]
    public string? TerminadaPor { get; set; }

    /// <summary>La sacó el dueño (ya no aparece en ningún celu). No se borra: queda lo que hicieron.</summary>
    public DateTime? CanceladaAt { get; set; }
}

[Table("Dep_TareaItems")]
public class DepTareaItem
{
    [Key]
    public int Id { get; set; }

    public int TareaId { get; set; }

    public int ProductoId { get; set; }

    public int Orden { get; set; }

    public DateTime? HechoAt { get; set; }

    [MaxLength(200)]
    public string? HechoPor { get; set; }

    /// <summary>CONTAR: lo que contaron.</summary>
    public int? Contado { get; set; }

    /// <summary>CONTAR: lo que decía el sistema en el momento de contar (para la diferencia).</summary>
    public int? StockAlContar { get; set; }

    /// <summary>"No lo encuentro".</summary>
    public bool NoEncontrado { get; set; }

    /// <summary>UBICACION: "PB · TOST"; BARCODE: el código cargado.</summary>
    [MaxLength(100)]
    public string? Valor { get; set; }

    /// <summary>CONTAR con diferencia: null = sin revisar | ACEPTADO (se corrigió el stock) | DESCARTADO.</summary>
    [MaxLength(20)]
    public string? Revision { get; set; }

    public DateTime? RevisionAt { get; set; }

    [MaxLength(120)]
    public string? RevisionPor { get; set; }
}
