using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-09-08 — Precio pactado con UN cliente para UN producto en UN formato.
///
/// Osmar: *"Núcleo, si compra vasos VT120 x bulto, tiene un precio especial pactado"*.
/// El formato es parte del pacto: el acuerdo es "x bulto", no "el producto" — por eso la
/// clave es (Cliente, Producto, Formato) y no (Cliente, Producto).
///
/// Precio SIN IVA, igual que CafeProducto.PrecioBar / PrecioOtro: el IVA se suma después,
/// en el total de la venta. Cuando hay fila, este precio PISA todo el motor de precios
/// (tipo de cliente, precio de bulto, fraccionamiento de café): lo pactado es lo pactado,
/// aunque la lista general quede más barata.
/// </summary>
[Table("Cafe_PreciosEspecialesCliente")]
public class CafePrecioEspecialCliente
{
    [Key]
    public int Id { get; set; }

    public int ClienteId { get; set; }

    [ForeignKey(nameof(ClienteId))]
    public CafeCliente? ClienteNav { get; set; }

    public int ProductoId { get; set; }

    [ForeignKey(nameof(ProductoId))]
    public CafeProducto? ProductoNav { get; set; }

    /// <summary>UNIT | BULTO | PACK_N | 1KG | MEDIO | CUARTO (constantes de CafePricingService).</summary>
    [Required, MaxLength(20)]
    public string Formato { get; set; } = CafePricingServiceFormatos.Unit;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Precio { get; set; }

    /// <summary>Por qué se pactó (opcional). Ej: "acuerdo mayo 2026, compra mínima 5 bultos".</summary>
    [MaxLength(300)]
    public string? Notas { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    [MaxLength(100)]
    public string? CreatedBy { get; set; }
}

/// <summary>Constante suelta para no arrastrar Api.Services dentro del modelo.</summary>
internal static class CafePricingServiceFormatos
{
    public const string Unit = "UNIT";
}
