using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-08-03: Catalogo de empresas de transporte que usamos para despachar ventas
/// (Via Cargo, Andreani, Cruz del Sur, etc). Se carga una vez desde "Configuracion del sistema"
/// y luego se elige de una lista al marcar TRANSPORTE en la venta.
///
/// Alimenta el bloque superior del rotulo/etiqueta (empresa de transporte + sucursal + telefono).
/// </summary>
[Table("Cafe_Transportes")]
public class CafeTransporte
{
    [Key]
    public int Id { get; set; }

    /// <summary>Nombre del transporte. Ej: "Via Cargo".</summary>
    [Required, MaxLength(150)]
    public string Nombre { get; set; } = string.Empty;

    /// <summary>Direccion de la sucursal/deposito del transporte. Ej: "Quaranta 3505".</summary>
    [MaxLength(300)]
    public string? Direccion { get; set; }

    /// <summary>Telefono de la sucursal. Ej: "(0376) 4471778".</summary>
    [MaxLength(100)]
    public string? Telefono { get; set; }

    /// <summary>Localidad de destino/sucursal. Ej: "Posadas".</summary>
    [MaxLength(150)]
    public string? Localidad { get; set; }

    /// <summary>Provincia de destino. Ej: "Misiones".</summary>
    [MaxLength(150)]
    public string? Provincia { get; set; }

    /// <summary>Codigo postal de la sucursal/destino.</summary>
    [MaxLength(20)]
    public string? CodigoPostal { get; set; }

    /// <summary>Si el envio se paga en destino (aparece "Pago destino" en el rotulo).</summary>
    public bool PagoDestino { get; set; } = true;

    /// <summary>Si esta activo (aparece en la lista para elegir).</summary>
    public bool Activo { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
