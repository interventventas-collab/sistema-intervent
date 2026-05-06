using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_Clientes")]
public class CafeCliente
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Código secuencial autogenerado (formato '0001'). Único, sirve para buscar rápido.</summary>
    [MaxLength(20)]
    public string? Codigo { get; set; }

    /// <summary>Nombre fantasía o nombre comercial del cliente. Es el campo "Nombre" original — se usa para mostrar.</summary>
    [Required, MaxLength(200)]
    public string Nombre { get; set; } = string.Empty;

    /// <summary>Razón social (nombre legal) — se usa cuando se emite factura.</summary>
    [MaxLength(200)]
    public string? RazonSocial { get; set; }

    [Required, MaxLength(20)]
    public string Tipo { get; set; } = "OTRO"; // BAR | OTRO

    [MaxLength(20)]
    public string? Cuit { get; set; }

    [MaxLength(50)]
    public string? Telefono { get; set; }

    [MaxLength(255)]
    public string? Email { get; set; }

    [MaxLength(300)]
    public string? Direccion { get; set; }

    /// <summary>Domicilio donde se entregan los pedidos (puede ser distinto del Direccion fiscal).</summary>
    [MaxLength(500)]
    public string? DomicilioEntrega { get; set; }

    [MaxLength(500)]
    public string? Notas { get; set; }

    /// <summary>Comentarios que se imprimen en TODOS los comprobantes de este cliente
    /// (ej: "Entregar antes de las 11 hs", "Solicitar firma del recepcionista").</summary>
    public string? ComentariosComprobante { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
