using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-08-27 — Historial de comprobantes ARCA de una reserva de alquiler: cada factura y cada
/// nota de credito que se emitio, con sus datos fiscales completos.
///
/// Por que existe: la reserva guarda "el comprobante vigente" en sus campos Arca*/Nc*. Cuando una
/// factura se anula con NC y despues se vuelve a facturar (por ejemplo, porque salio con la empresa
/// equivocada), esos campos se pisan con la factura nueva. Sin esta tabla se perderia el rastro de
/// lo que ya se le declaro a ARCA. Aca queda todo, en orden, y nunca se borra.
/// </summary>
[Table("Alq_ReservaComprobantes")]
public class AlqReservaComprobante
{
    [Key]
    public int Id { get; set; }

    public int ReservaId { get; set; }

    /// <summary>"factura" | "nota_credito".</summary>
    [MaxLength(20)]
    public string Clase { get; set; } = "factura";

    /// <summary>FA | FB | FC | NCA | NCB | NCC.</summary>
    [MaxLength(10)]
    public string TipoComprobante { get; set; } = "";

    /// <summary>Numero de tipo de ARCA: 1=FA, 6=FB, 11=FC, 3=NCA, 8=NCB, 13=NCC.</summary>
    public int CbteTipoNum { get; set; }

    public int PtoVta { get; set; }
    public int CbteNro { get; set; }

    [MaxLength(20)]
    public string? Cae { get; set; }
    public DateTime? CaeVto { get; set; }
    /// <summary>Fecha de emision registrada por ARCA.</summary>
    public DateTime? Fecha { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? ImpNeto { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? ImpIVA { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? ImpTotal { get; set; }

    /// <summary>Certificado/CUIT con el que se emitio.</summary>
    public int? ArcaWebserviceAccountId { get; set; }

    [MaxLength(20)]
    public string? CuitEmisor { get; set; }

    /// <summary>Solo en notas de credito: por que se anulo.</summary>
    [MaxLength(300)]
    public string? Motivo { get; set; }

    /// <summary>Operador que lo emitio (header X-Operator-Name), si vino.</summary>
    [MaxLength(40)]
    public string? Operador { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
