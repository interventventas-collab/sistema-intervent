using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Controla qué items del sidebar ve cada rol no-admin. Una fila por (rol, key) significa que
/// ese rol VE ese item. El admin tiene UI para tildar/destildar items por rol sin tocar código.
/// Pedido del usuario 2026-05-28.
/// </summary>
[Table("MenuVisibility")]
public class MenuVisibility
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Nombre del rol: "deposito", "oficina", etc.</summary>
    [Required]
    [MaxLength(50)]
    public string RoleName { get; set; } = "";

    /// <summary>Ruta relativa del item (ej: "cafe/ventas", "cafe/preparacion", "mapeo").</summary>
    [Required]
    [MaxLength(200)]
    public string MenuKey { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
