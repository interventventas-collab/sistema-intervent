using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-11-10 — Un pedido a proveedor ya cerrado. Los productos son los renglones de
/// Cafe_StockFaltantes con este PedidoProveedorId: mientras se arma están en Estado='ARMANDO' y
/// al cerrar pasan a 'PEDIDO'. Se cierra uno por marca: cada proveedor recibe solo lo suyo.</summary>
[Table("Cafe_PedidosProveedor")]
public class CafePedidoProveedor
{
    public int Id { get; set; }

    /// <summary>Null = productos sin marca cargada.</summary>
    [MaxLength(200)]
    public string? Marca { get; set; }

    public DateTime CerradoAt { get; set; } = DateTime.UtcNow;

    [MaxLength(120)]
    public string? CerradoPor { get; set; }

    /// <summary>Último mail al que se mandó: la próxima vez de esa marca se propone el mismo.</summary>
    [MaxLength(254)]
    public string? UltimoEmail { get; set; }

    public DateTime? UltimoEmailAt { get; set; }
}
