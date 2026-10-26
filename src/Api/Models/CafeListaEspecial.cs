using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-10-26 — Lista de precios especiales (Ventas → Precios especiales).
///
/// Agrupa precios pactados que valen para VARIOS clientes a la vez ("Lista clientes
/// especiales" de vasos y tapas: Núcleo y algún otro). Sirve también como "categoría de
/// precios": se pueden armar todas las listas que hagan falta.
///
/// La venta NO lee estas tablas: cada cambio se copia a Cafe_PreciosEspecialesCliente
/// (una fila por cliente, con ListaId), que es lo que ya usa el motor de precios.
/// </summary>
[Table("Cafe_ListasEspeciales")]
public class CafeListaEspecial
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Nombre { get; set; } = "";

    [MaxLength(300)]
    public string? Notas { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    [MaxLength(100)]
    public string? CreatedBy { get; set; }
}

/// <summary>Un producto + formato de la lista, con su precio SIN IVA.</summary>
[Table("Cafe_ListasEspecialesItems")]
public class CafeListaEspecialItem
{
    [Key]
    public int Id { get; set; }

    public int ListaId { get; set; }

    public int ProductoId { get; set; }

    [ForeignKey(nameof(ProductoId))]
    public CafeProducto? ProductoNav { get; set; }

    [Required, MaxLength(20)]
    public string Formato { get; set; } = "UNIT";

    [Column(TypeName = "decimal(18,2)")]
    public decimal Precio { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>Cliente al que se le aplica la lista.</summary>
[Table("Cafe_ListasEspecialesClientes")]
public class CafeListaEspecialCliente
{
    [Key]
    public int Id { get; set; }

    public int ListaId { get; set; }

    public int ClienteId { get; set; }

    [ForeignKey(nameof(ClienteId))]
    public CafeCliente? ClienteNav { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
