using System.ComponentModel.DataAnnotations;

namespace Api.Models;

/// <summary>
/// 2026-07-08: Comprobante (factura o nota de credito) IMPORTADO del Excel oficial de MercadoLibre
/// "Reporte de facturas y notas de creditos" (Facturacion -> Descargar reporte).
///
/// A diferencia de MeliFacturas (que se baja por la API, una fila por ORDEN, sin notas de credito),
/// esta tabla guarda una fila por COMPROBANTE, incluye las NOTAS DE CREDITO (que RESTAN) y trae el
/// neto/IVA ya discriminado por MeLi, mas el envio separado. Es la fuente para el Libro IVA Ventas
/// "oficial" que le cuadra 1 a 1 a la contadora contra el reporte que ella conoce.
///
/// Clave para no duplicar: <see cref="IdComprobante"/> (ID del comprobante de MeLi, unico global).
/// Re-importar el mismo mes -o meses que se pisan- no duplica: se actualiza la fila existente.
/// </summary>
public class ContadoraComprobante
{
    public int Id { get; set; }

    /// <summary>VENTA (IVA débito) o COMPRA (IVA crédito). Default VENTA.</summary>
    [MaxLength(10)]
    public string Naturaleza { get; set; } = "VENTA";

    /// <summary>Origen del dato: "MELI_REPORTE", "SISTEMA" (ventas propias por AFIP) o "AFIP_RECIBIDOS" (compras).</summary>
    [MaxLength(30)]
    public string Origen { get; set; } = "MELI_REPORTE";

    /// <summary>Concepto AFIP: 1=Productos, 2=Servicios, 3=Productos y Servicios. MeLi = 1 (productos).</summary>
    public int? Concepto { get; set; }

    /// <summary>CUIT del vendedor/emisor (viene en el encabezado del reporte) = la EMPRESA.</summary>
    [MaxLength(20)]
    public string? EmisorCuit { get; set; }

    /// <summary>ID del comprobante en MeLi (columna "ID del comprobante"). Unico. Es la clave anti-duplicados.</summary>
    [MaxLength(40)]
    public string IdComprobante { get; set; } = "";

    public long? NumeroVenta { get; set; }
    public long? NumeroEnvio { get; set; }

    /// <summary>"Venta" o "Cancelacion" (las notas de credito son "Cancelacion").</summary>
    [MaxLength(30)]
    public string? TipoOperacion { get; set; }

    /// <summary>"Factura A/B" o "Nota de Credito A/B" tal cual el reporte.</summary>
    [MaxLength(40)]
    public string? TipoComprobante { get; set; }

    /// <summary>True si es Nota de Credito: en los totales RESTA.</summary>
    public bool EsNotaCredito { get; set; }

    /// <summary>Letra A o B (del tipo de comprobante).</summary>
    [MaxLength(4)]
    public string? Letra { get; set; }

    public int? PuntoVenta { get; set; }
    /// <summary>Numero de factura/comprobante (columna "Numero de factura").</summary>
    public long? NumeroComprobante { get; set; }
    [MaxLength(30)]
    public string? Cae { get; set; }
    public DateTime? FechaEmision { get; set; }
    [MaxLength(30)]
    public string? Estado { get; set; }

    [MaxLength(20)]
    public string? ReceptorTipoDoc { get; set; }
    [MaxLength(30)]
    public string? ReceptorDoc { get; set; }
    [MaxLength(80)]
    public string? ReceptorCondIva { get; set; }
    [MaxLength(200)]
    public string? ReceptorNombre { get; set; }

    /// <summary>Provincia del domicilio fiscal del comprador.</summary>
    [MaxLength(120)]
    public string? Provincia { get; set; }
    /// <summary>Provincia del domicilio de envio.</summary>
    [MaxLength(120)]
    public string? ProvinciaEnvio { get; set; }

    // Desglose de importes (tal cual el reporte de MeLi).
    public decimal NetoGravado { get; set; }
    public decimal BaseIva105 { get; set; }
    public decimal Iva105 { get; set; }
    public decimal BaseIva21 { get; set; }
    public decimal Iva21 { get; set; }
    public decimal EnvioNeto { get; set; }
    public decimal EnvioIva { get; set; }
    public decimal EnvioTotal { get; set; }
    public decimal Conceptos { get; set; }
    public decimal OtrosImpuestos { get; set; }
    public decimal NoGravado { get; set; }
    public decimal Exento { get; set; }
    public decimal Total { get; set; }

    /// <summary>Nombre del archivo Excel del que se importo (para trazabilidad).</summary>
    [MaxLength(255)]
    public string? ArchivoOrigen { get; set; }
    public DateTime ImportadoEn { get; set; } = DateTime.UtcNow;
}
