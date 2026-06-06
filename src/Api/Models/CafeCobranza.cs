using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_Cobranzas")]
public class CafeCobranza
{
    [Key]
    public int Id { get; set; }

    /// <summary>Numero correlativo del recibo (ej: 0100-00000001).</summary>
    [Required, MaxLength(20)]
    public string Numero { get; set; } = string.Empty;

    public DateTime Fecha { get; set; } = DateTime.UtcNow;

    /// <summary>2026-06-06: Nullable para permitir cobrar ventas "ocasionales" (sin cliente
    /// del catálogo). Cuando es null, la cobranza esta asociada solo a la venta via
    /// CafeCobranzaComprobante.VentaId, y el snapshot del nombre se lee desde la venta.</summary>
    public int? ClienteId { get; set; }
    [ForeignKey(nameof(ClienteId))]
    public CafeCliente? Cliente { get; set; }

    /// <summary>Total cobrado (suma de los medios de pago).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Total { get; set; }

    /// <summary>Retenciones sufridas (concepto generico "Retenciones"). Cancela saldo igual aunque no entra a caja.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Retenciones { get; set; }

    [MaxLength(100)]
    public string? Operador { get; set; }

    [MaxLength(500)]
    public string? Observaciones { get; set; }

    /// <summary>VIGENTE | ANULADA</summary>
    [Required, MaxLength(20)]
    public string Estado { get; set; } = "VIGENTE";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public List<CafeCobranzaComprobante> Comprobantes { get; set; } = new();
    public List<CafeCobranzaMedio> Medios { get; set; } = new();
}

[Table("Cafe_CobranzasComprobantes")]
public class CafeCobranzaComprobante
{
    [Key]
    public int Id { get; set; }

    public int CobranzaId { get; set; }
    [ForeignKey(nameof(CobranzaId))]
    public CafeCobranza? Cobranza { get; set; }

    /// <summary>VentaId nullable: si es NULL = cobranza "a cuenta" (no asignada a una venta puntual).</summary>
    public int? VentaId { get; set; }
    [ForeignKey(nameof(VentaId))]
    public CafeVenta? Venta { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Importe { get; set; }
}

[Table("Cafe_CobranzasMedios")]
public class CafeCobranzaMedio
{
    [Key]
    public int Id { get; set; }

    public int CobranzaId { get; set; }
    [ForeignKey(nameof(CobranzaId))]
    public CafeCobranza? Cobranza { get; set; }

    public int CajaId { get; set; }
    [ForeignKey(nameof(CajaId))]
    public CafeCaja? Caja { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Importe { get; set; }

    /// <summary>Nro de transferencia, nro de operacion MP, destinatario V, etc.</summary>
    [MaxLength(200)]
    public string? Referencia { get; set; }

    /// <summary>Si el medio fue un cheque, apunta al registro creado en Cafe_Cheques.</summary>
    public int? ChequeId { get; set; }
    [ForeignKey(nameof(ChequeId))]
    public CafeCheque? Cheque { get; set; }
}
