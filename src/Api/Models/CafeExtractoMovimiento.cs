using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Movimientos del extracto bancario del Banco Galicia (CC). Importados desde el Excel
/// que el banco deja descargar. Cada fila del Excel es un movimiento aca.
///
/// Saldo: aparece en el Excel (saldo despues del movimiento). El "saldo actual" del banco
/// se calcula como el Saldo del ultimo movimiento por fecha.
///
/// Asociacion con ventas: el usuario puede marcar manualmente que un ingreso corresponde
/// a una venta puntual. Esto SOLO marca la asociacion — NO crea cobranza automatica.
/// La cobranza real se carga aparte en /cafe/tesoreria/cobranzas.
///
/// Hash: usado para detectar duplicados al re-importar el mismo Excel.
///
/// Pedido del usuario 2026-05-19.
/// </summary>
[Table("Cafe_ExtractoMovimientos")]
public class CafeExtractoMovimiento
{
    public int Id { get; set; }

    [Column(TypeName = "date")]
    public DateTime Fecha { get; set; }

    [MaxLength(200)]
    public string? Descripcion { get; set; }

    [MaxLength(50)]
    public string? Origen { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Debitos { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Creditos { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Saldo { get; set; }

    [MaxLength(100)]
    public string? GrupoConceptos { get; set; }

    [MaxLength(100)]
    public string? Concepto { get; set; }

    [MaxLength(50)]
    public string? NumeroTerminal { get; set; }

    [MaxLength(500)]
    public string? ObservacionesCliente { get; set; }

    [MaxLength(100)]
    public string? NumeroComprobante { get; set; }

    /// <summary>Razon social del cliente (extraida del extracto en transferencias recibidas).</summary>
    [MaxLength(200)]
    public string? LeyendaAdicional1 { get; set; }

    /// <summary>CUIT del cliente.</summary>
    [MaxLength(50)]
    public string? LeyendaAdicional2 { get; set; }

    /// <summary>CBU origen.</summary>
    [MaxLength(50)]
    public string? LeyendaAdicional3 { get; set; }

    [MaxLength(200)]
    public string? LeyendaAdicional4 { get; set; }

    /// <summary>Imputado | Pendiente (segun el banco).</summary>
    [MaxLength(30)]
    public string? TipoMovimiento { get; set; }

    /// <summary>Hash de fecha+descripcion+importe+saldo para detectar duplicados al re-importar.</summary>
    [Required, MaxLength(80)]
    public string HashUnico { get; set; } = "";

    /// <summary>Si el usuario asocio este movimiento con una venta, su Id.</summary>
    public int? VentaIdAsociada { get; set; }
    [ForeignKey(nameof(VentaIdAsociada))]
    public CafeVenta? VentaAsociada { get; set; }

    [MaxLength(120)]
    public string? AsociadoPor { get; set; }

    public DateTime? AsociadoAt { get; set; }

    /// <summary>Id de la cobranza que ya consumio este movimiento. Si != null, el
    /// movimiento NO se sugiere mas como "disponible" en /cafe/tesoreria/cobranzas.</summary>
    public int? CobranzaUsadaId { get; set; }

    [MaxLength(200)]
    public string? ArchivoOrigen { get; set; }

    public DateTime ImportadoAt { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
