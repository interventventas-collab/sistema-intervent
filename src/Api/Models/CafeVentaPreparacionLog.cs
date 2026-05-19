using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Log de auditoria de cambios en el flujo de Preparacion de Pedidos.
/// Cada vez que alguien aprieta "Avanzar →" en /cafe/preparacion, se inserta un
/// renglon aca con el operador actual y el estado anterior/nuevo. Sirve para
/// reconstruir el historial: cuanto tardo en armarse, quien se confundio, etc.
/// </summary>
[Table("Cafe_VentaPreparacionLog")]
public class CafeVentaPreparacionLog
{
    public int Id { get; set; }

    public int VentaId { get; set; }

    [MaxLength(20)]
    public string? EstadoAnterior { get; set; }

    [Required, MaxLength(20)]
    public string EstadoNuevo { get; set; } = "";

    [MaxLength(120)]
    public string? OperadorNombre { get; set; }

    [MaxLength(300)]
    public string? Notas { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
