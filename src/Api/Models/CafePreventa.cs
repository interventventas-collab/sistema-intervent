using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Vendedor que puede cargar preventas (notas de pedido) desde un link público.
/// Inicialmente "Gaby" pero extensible.
/// </summary>
[Table("Cafe_PreventaVendedores")]
public class CafePreventaVendedor
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(120)]
    public string Nombre { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string Token { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Preventa = nota de pedido informal cargada desde el celular por un vendedor.
/// NO descuenta stock, NO factura. Es un papelito digital que Osmar después
/// convierte en una venta real desde el panel admin.
/// </summary>
[Table("Cafe_Preventas")]
public class CafePreventa
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(30)]
    public string Numero { get; set; } = string.Empty;

    [Column(TypeName = "date")]
    public DateTime Fecha { get; set; }

    public int? VendedorId { get; set; }
    [ForeignKey(nameof(VendedorId))]
    public CafePreventaVendedor? VendedorNav { get; set; }

    [MaxLength(120)]
    public string? VendedorNombreSnap { get; set; }

    /// <summary>Cliente del catálogo (si lo buscó y eligió uno existente).</summary>
    public int? ClienteId { get; set; }
    [ForeignKey(nameof(ClienteId))]
    public CafeCliente? ClienteNav { get; set; }

    /// <summary>Si NO eligió cliente del catálogo, el nombre que escribió libre.</summary>
    [MaxLength(200)]
    public string? ClienteNombreLibre { get; set; }

    [MaxLength(60)]
    public string? ClienteTelefono { get; set; }

    [MaxLength(1000)]
    public string? Notas { get; set; }

    /// <summary>Path al archivo de foto si la subió. Relativo a /data/preventas-fotos.</summary>
    [MaxLength(300)]
    public string? FotoPath { get; set; }

    /// <summary>pendiente | procesada | cancelada</summary>
    [Required, MaxLength(20)]
    public string Estado { get; set; } = "pendiente";

    /// <summary>Si fue convertida a venta, guardamos el id de la venta resultante.</summary>
    public int? VentaIdFinal { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public List<CafePreventaItem> Items { get; set; } = new();
}

[Table("Cafe_PreventaItems")]
public class CafePreventaItem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int PreventaId { get; set; }
    [ForeignKey(nameof(PreventaId))]
    public CafePreventa? PreventaNav { get; set; }

    /// <summary>Si eligió producto del catálogo. Null si fue descripción libre.</summary>
    public int? ProductoId { get; set; }
    [ForeignKey(nameof(ProductoId))]
    public CafeProducto? ProductoNav { get; set; }

    /// <summary>Nombre del producto del catálogo en el momento (snapshot).</summary>
    [MaxLength(200)]
    public string? ProductoNombreSnap { get; set; }

    /// <summary>Texto libre que escribió el vendedor (si no usó producto del catálogo).</summary>
    [MaxLength(300)]
    public string? DescripcionLibre { get; set; }

    [Column(TypeName = "decimal(18,3)")]
    public decimal Cantidad { get; set; } = 1m;

    [Column(TypeName = "decimal(18,2)")]
    public decimal? PrecioSugerido { get; set; }

    [MaxLength(300)]
    public string? Observaciones { get; set; }

    public int Orden { get; set; }
}
