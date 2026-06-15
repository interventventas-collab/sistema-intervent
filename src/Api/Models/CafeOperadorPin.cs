using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_OperadoresPin")]

/// <summary>2026-06-15: PIN corto (4 dígitos) por operador para autenticarse al
/// iniciar la sesión de trabajo. El hash usa BCrypt. Cada operador tiene su PIN
/// y puede cambiarlo confirmando el anterior. Admin puede resetearlo desde el panel.</summary>
public class CafeOperadorPin
{
    [Key]
    [MaxLength(40)]
    public string Nombre { get; set; } = "";

    [MaxLength(200)]
    public string PinHash { get; set; } = "";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Quien hizo el último cambio: el propio operador o "admin" si fue reset.</summary>
    [MaxLength(40)]
    public string? UpdatedBy { get; set; }
}
