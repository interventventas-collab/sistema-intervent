using System.ComponentModel.DataAnnotations;

namespace Api.Models;

/// <summary>
/// 2026-07-08: Factura de VENTA emitida por MercadoLibre por cuenta del vendedor.
/// Se baja de GET /users/{userid}/invoices/orders/{orderid} y alimenta el Libro IVA Ventas
/// del modulo "Contadora" (ventas por punto de venta, por empresa/CUIT emisor).
///
/// Una fila por orden procesada. Si la orden NO tiene factura en MeLi, se guarda igual con
/// Status='SIN_FACTURA' (sentinela) para no volver a consultarla infinitamente.
/// </summary>
public class MeliFactura
{
    public int Id { get; set; }
    public long MeliOrderId { get; set; }
    public int MeliAccountId { get; set; }

    /// <summary>id de la factura en MeLi (invoice id). 0 si SIN_FACTURA.</summary>
    public long InvoiceId { get; set; }

    /// <summary>CUIT del emisor (issuer.identifications.cuit) = la EMPRESA. Clave para filtrar por empresa.</summary>
    [MaxLength(20)]
    public string? EmisorCuit { get; set; }
    [MaxLength(200)]
    public string? EmisorNombre { get; set; }

    /// <summary>Punto de venta (invoice_series).</summary>
    public int? PuntoVenta { get; set; }
    /// <summary>Numero de comprobante (invoice_number).</summary>
    public long? NumeroComprobante { get; set; }
    public DateTime? FechaEmision { get; set; }

    /// <summary>Letra del comprobante inferida: A (receptor con CUIT/responsable) o B (consumidor final).</summary>
    [MaxLength(4)]
    public string? Letra { get; set; }
    /// <summary>Condicion IVA del receptor tal cual la manda MeLi (recipient.tax_type).</summary>
    [MaxLength(60)]
    public string? ReceptorTaxType { get; set; }
    [MaxLength(200)]
    public string? ReceptorNombre { get; set; }
    [MaxLength(20)]
    public string? ReceptorDoc { get; set; }

    public decimal Neto { get; set; }
    public decimal Iva { get; set; }
    public decimal Total { get; set; }

    /// <summary>Provincia de destino (shipment.destination.state.name), bonus que trae la factura.</summary>
    [MaxLength(120)]
    public string? Provincia { get; set; }

    /// <summary>Estado MeLi de la factura (authorized, ...) o 'SIN_FACTURA' si la orden no tiene.</summary>
    [MaxLength(30)]
    public string? Status { get; set; }

    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;
}
