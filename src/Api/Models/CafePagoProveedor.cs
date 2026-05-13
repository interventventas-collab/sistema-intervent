using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_PagosProveedor")]
public class CafePagoProveedor
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(20)]
    public string Numero { get; set; } = string.Empty;

    public DateTime Fecha { get; set; } = DateTime.UtcNow;

    public int ProveedorId { get; set; }
    [ForeignKey(nameof(ProveedorId))]
    public CafeProveedor? Proveedor { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Total { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Retenciones { get; set; }

    [MaxLength(100)] public string? Operador { get; set; }
    [MaxLength(500)] public string? Observaciones { get; set; }

    [Required, MaxLength(20)]
    public string Estado { get; set; } = "VIGENTE";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public List<CafePagoProveedorComprobante> Comprobantes { get; set; } = new();
    public List<CafePagoProveedorMedio> Medios { get; set; } = new();
}

[Table("Cafe_PagosProveedorComprobantes")]
public class CafePagoProveedorComprobante
{
    [Key]
    public int Id { get; set; }

    public int PagoId { get; set; }
    [ForeignKey(nameof(PagoId))]
    public CafePagoProveedor? Pago { get; set; }

    public int? CompraId { get; set; }
    [ForeignKey(nameof(CompraId))]
    public CafeCompra? Compra { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Importe { get; set; }
}

[Table("Cafe_PagosProveedorMedios")]
public class CafePagoProveedorMedio
{
    [Key]
    public int Id { get; set; }

    public int PagoId { get; set; }
    [ForeignKey(nameof(PagoId))]
    public CafePagoProveedor? Pago { get; set; }

    public int CajaId { get; set; }
    [ForeignKey(nameof(CajaId))]
    public CafeCaja? Caja { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Importe { get; set; }

    [MaxLength(200)]
    public string? Referencia { get; set; }

    /// <summary>Si el pago se hizo endosando un cheque de cartera, este apunta al cheque.</summary>
    public int? ChequeId { get; set; }
    [ForeignKey(nameof(ChequeId))]
    public CafeCheque? Cheque { get; set; }
}
