using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-06-08: registro de productos "descartados" como sugerencia de "Más comprados"
/// para un cliente puntual. El operador puede apretar "×" en el chip de un producto
/// sugerido y queda guardado acá. Mientras la última compra de ese producto haya sido
/// ANTES del descarte, el producto NO aparece en sugerencias para ese cliente.
/// Si el cliente vuelve a comprar el producto después del descarte, se ignora el
/// registro y el producto vuelve a aparecer automáticamente.
/// </summary>
[Table("Cafe_ClienteProductoDescartado")]
public class CafeClienteProductoDescartado
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int ClienteId { get; set; }

    [ForeignKey(nameof(ClienteId))]
    public CafeCliente? ClienteNav { get; set; }

    public int ProductoId { get; set; }

    [ForeignKey(nameof(ProductoId))]
    public CafeProducto? ProductoNav { get; set; }

    public DateTime DescartadoAt { get; set; } = DateTime.UtcNow;

    [MaxLength(50)]
    public string? DescartadoPor { get; set; }
}
