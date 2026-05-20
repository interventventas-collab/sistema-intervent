using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Repartidor con PIN (ultimos 3 del DNI) para autenticarse en la pantalla publica
/// /repartidor/{token}. Patron tomado de HorasExtrasEmpleado. Pedido 2026-05-19.
/// </summary>
[Table("Cafe_Repartidores")]
public class CafeRepartidor
{
    public int Id { get; set; }

    [Required, MaxLength(120)]
    public string Nombre { get; set; } = "";

    /// <summary>Ultimos 3 digitos del DNI. PIN corto para login en mobile.</summary>
    [MaxLength(3)]
    public string? DniUltimos3 { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
