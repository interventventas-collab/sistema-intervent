using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-07-17: Base de datos propia de clientes de MercadoLibre. UN registro por comprador
/// (identificado por su BuyerId de MeLi), que se va acumulando y actualizando solo con cada venta.
/// Guarda la mejor info de contacto conocida (telefono/direccion vienen SOLO de ventas Flex/ME1,
/// porque MeLi no da esos datos en las ventas por correo normal) + el resumen de compras.
/// El detalle de cada compra vive en MeliClienteCompras (asi el historial queda permanente aunque
/// se borren las ordenes de MeliOrders al desconectar una cuenta).
/// </summary>
[Table("MeliClientes")]
public class MeliCliente
{
    [Key]
    public int Id { get; set; }

    /// <summary>Id del comprador en MeLi. Clave estable del cliente (unico).</summary>
    public long BuyerId { get; set; }

    [MaxLength(255)] public string? Nickname { get; set; }
    /// <summary>Nombre real del receptor (solo lo tenemos en Flex/ME1).</summary>
    [MaxLength(200)] public string? ReceiverName { get; set; }

    /// <summary>Telefono conocido mas reciente (solo Flex/ME1).</summary>
    [MaxLength(50)] public string? Phone { get; set; }

    [MaxLength(300)] public string? AddressLine { get; set; }
    [MaxLength(150)] public string? Neighborhood { get; set; }
    [MaxLength(150)] public string? City { get; set; }
    [MaxLength(150)] public string? State { get; set; }
    [MaxLength(20)] public string? ZipCode { get; set; }

    public DateTime? FirstPurchaseAt { get; set; }
    public DateTime? LastPurchaseAt { get; set; }
    /// <summary>Fecha de la orden con la que se guardo el contacto actual (para no pisarlo con una mas vieja).</summary>
    public DateTime? LastContactAt { get; set; }

    public int OrdersCount { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal TotalSpent { get; set; }

    /// <summary>Resumen de la ultima compra (para verlo de un vistazo en la grilla).</summary>
    [MaxLength(500)] public string? LastItems { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public List<MeliClienteCompra> Compras { get; set; } = new();
}
