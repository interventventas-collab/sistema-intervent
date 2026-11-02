using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

// 2026-11-02: vacaciones de un empleado (pantalla Empleados -> Vacaciones).
[Table("Nom_Vacaciones")]
public class NomVacacion
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int EmpleadoId { get; set; }

    public DateTime Desde { get; set; }
    public DateTime Hasta { get; set; }

    // De qué año son estas vacaciones (las de enero 2027 corresponden a 2026).
    public int Periodo { get; set; }

    // "aprobada" o "pedida" (falta que el dueño la apruebe).
    [Required, MaxLength(20)]
    public string Estado { get; set; } = "aprobada";

    [MaxLength(300)]
    public string? Nota { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(120)]
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
