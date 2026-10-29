using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-10-29: recordatorios / notas entre el equipo que aparecen en la ventanita de bienvenida.
/// "Persona" = el operador del PIN (OSMAR, GERMAN, GABRIEL...), no el usuario del login (varios
/// comparten login). Para = null es "para todos"; si uno lo tilda, queda hecho para todos.
/// </summary>
[Table("Bienvenida_Recordatorios")]
public class BienvenidaRecordatorio
{
    public int Id { get; set; }

    [Required, MaxLength(500)]
    public string Texto { get; set; } = "";

    /// <summary>Día en que tiene que aparecer (fecha Argentina). Null = desde ya, sin fecha.</summary>
    [Column(TypeName = "date")]
    public DateTime? Fecha { get; set; }

    /// <summary>Para quién es (operador). Null = para todos.</summary>
    [MaxLength(30)]
    public string? Para { get; set; }

    /// <summary>Quién lo anotó (operador).</summary>
    [MaxLength(30)]
    public string De { get; set; } = "";

    public bool Hecho { get; set; }
    public DateTime? HechoAt { get; set; }
    [MaxLength(30)]
    public string? HechoPor { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Último día (Argentina) en que a cada persona se le abrió sola la bienvenida.</summary>
[Table("Bienvenida_Vistas")]
public class BienvenidaVista
{
    [Key, MaxLength(30)]
    public string Persona { get; set; } = "";

    [Column(TypeName = "date")]
    public DateTime UltimaFecha { get; set; }
}
