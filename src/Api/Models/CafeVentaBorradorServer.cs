using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-07-14: borrador de venta GUARDADO EN EL SERVIDOR (compartido por todo el equipo).
/// Reemplaza al borrador viejo que vivía en el localStorage del navegador (era 1 solo y por PC).
/// Ahora se pueden tener hasta 10 borradores, visibles y retomables desde cualquier computadora.
/// El PayloadJson es el snapshot serializado del formulario de Nueva Venta (mismo shape que usaba
/// el localStorage), así el frontend lo restaura tal cual.</summary>
[Table("Cafe_VentaBorradores")]
public class CafeVentaBorradorServer
{
    [Key]
    public int Id { get; set; }

    /// <summary>Snapshot completo del formulario, serializado a JSON.</summary>
    public string PayloadJson { get; set; } = "";

    /// <summary>Nombre del cliente (para mostrar en la lista sin deserializar el payload).</summary>
    [MaxLength(200)]
    public string? ClienteNombre { get; set; }

    public int ItemsCount { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Total { get; set; }

    /// <summary>Operador que lo dejó guardado (OSMAR/GERMAN/etc), para mostrar de quién es.</summary>
    [MaxLength(60)]
    public string? CreadoPorOperador { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
