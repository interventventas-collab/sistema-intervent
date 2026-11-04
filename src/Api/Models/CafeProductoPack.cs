using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>Pack prearmado: 1 producto base × N unidades, con su propio precio (o calculado).
/// Aparece como opción de "Formato" en el modal de venta junto a Suelto y Bulto.</summary>
[Table("Cafe_ProductoPacks")]
public class CafeProductoPack
{
    public int Id { get; set; }
    public int ProductoId { get; set; }
    public CafeProducto? Producto { get; set; }
    /// <summary>Cuántas unidades sueltas tiene el pack. Ej: 100, 1000.</summary>
    public int Cantidad { get; set; }
    /// <summary>Texto que se muestra al operador. Ej: "Pack x 100".</summary>
    public string Nombre { get; set; } = string.Empty;
    /// <summary>Precio del pack. Si es null, se calcula al vuelo como precio_unitario × Cantidad.</summary>
    public decimal? PrecioOverride { get; set; }
    /// <summary>2026-11-04: pack que va armado en ESTUCHE (cápsulas x10 con estuche). Puede convivir
    /// con otro pack de la misma Cantidad sin estuche: el formato de venta es "PACK_10E" en vez de "PACK_10".</summary>
    public bool ConEstuche { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
