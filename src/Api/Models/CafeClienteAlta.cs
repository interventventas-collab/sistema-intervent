using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Alta de cliente CARGADA POR EL PROPIO CLIENTE desde un enlace público (sin login).
/// El cliente completa sus datos, queda como PENDIENTE, y el operador la revisa y la
/// convierte en un cliente real (Cafe_Clientes) con el botón "Dar de alta".
/// Pedido de Osmar 2026-07-20: mandar un link para que el cliente cargue todo solo.
/// </summary>
[Table("Cafe_ClienteAltas")]
public class CafeClienteAlta
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Nombre de fantasía / comercial del local (lo que se ve en el día a día).</summary>
    [Required, MaxLength(200)]
    public string NombreFantasia { get; set; } = string.Empty;

    /// <summary>Razón social legal (viene de ARCA por el CUIT).</summary>
    [MaxLength(200)]
    public string? RazonSocial { get; set; }

    [MaxLength(20)]
    public string? Cuit { get; set; }

    /// <summary>Condición IVA (CF, RI, MO, EX) — se autocompleta desde ARCA.</summary>
    [MaxLength(20)]
    public string? CondicionIva { get; set; }

    /// <summary>A quién buscar / nombre de contacto en el local.</summary>
    [MaxLength(150)]
    public string? ContactoNombre { get; set; }

    [Required, MaxLength(50)]
    public string Telefono { get; set; } = string.Empty;

    [MaxLength(255)]
    public string? Email { get; set; }

    /// <summary>Domicilio de entrega (calle y número).</summary>
    [MaxLength(300)]
    public string? Direccion { get; set; }

    [MaxLength(200)]
    public string? EntreCalles { get; set; }

    [MaxLength(150)]
    public string? Localidad { get; set; }

    /// <summary>Link de Google Maps que el cliente puede pegar con su ubicación.</summary>
    [MaxLength(500)]
    public string? MapeoLink { get; set; }

    /// <summary>Comentarios libres que escribe el cliente (horarios, aclaraciones, etc).</summary>
    [MaxLength(1000)]
    public string? Comentarios { get; set; }

    /// <summary>"pendiente" | "aprobado" | "rechazado".</summary>
    [Required, MaxLength(20)]
    public string Estado { get; set; } = "pendiente";

    /// <summary>Id del Cafe_Clientes creado cuando se aprueba (para no duplicar).</summary>
    public int? ClienteIdCreado { get; set; }

    [MaxLength(300)]
    public string? MotivoRechazo { get; set; }

    /// <summary>Operador que aprobó/rechazó.</summary>
    [MaxLength(100)]
    public string? ProcesadoPor { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcesadoAt { get; set; }
}
