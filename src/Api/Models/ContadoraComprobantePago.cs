using System.ComponentModel.DataAnnotations;

namespace Api.Models;

/// <summary>
/// 2026-07-21: un pago registrado sobre una factura de COMPRA con CAE (Contadora).
/// Sirve para llevar la cuenta corriente de lo que se le debe a los proveedores que dan crédito:
/// cada factura puede tener uno o varios pagos (transferencia/cheque/efectivo) y el saldo de la
/// factura es su Total menos la suma de los pagos no anulados.
///
/// Se vincula a <see cref="ContadoraComprobante.IdComprobante"/> (la clave única de AFIP), igual
/// que el PDF adjunto. NO toca la sección de "Pagos a proveedores" contra compras internas: eso
/// queda para las compras informales / cotizaciones.
/// </summary>
public class ContadoraComprobantePago
{
    public int Id { get; set; }

    /// <summary>Clave única del comprobante de AFIP al que corresponde el pago.</summary>
    [MaxLength(40)]
    public string IdComprobante { get; set; } = "";

    public DateTime Fecha { get; set; }

    /// <summary>Transferencia / Cheque / Efectivo / Otro.</summary>
    [MaxLength(20)]
    public string Medio { get; set; } = "Transferencia";

    /// <summary>Nro de transferencia, nro de cheque + banco, etc.</summary>
    [MaxLength(120)]
    public string? Referencia { get; set; }

    public decimal Importe { get; set; }

    [MaxLength(120)]
    public string? Operador { get; set; }

    [MaxLength(300)]
    public string? Observaciones { get; set; }

    public bool Anulado { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
