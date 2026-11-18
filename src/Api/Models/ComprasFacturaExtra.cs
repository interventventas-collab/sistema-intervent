using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>18/11/2026 — Facturas recibidas: categoría y regla de pago por proveedor (CUIT).
/// ReglaPago != null = "se paga solo" (débito automático, tarjeta, MP): todas sus facturas salen pagadas.</summary>
[Table("Compras_ProveedorConfig")]
public class ComprasProveedorConfig
{
    [Key, MaxLength(30)] public string Cuit { get; set; } = "";
    [MaxLength(60)] public string? Categoria { get; set; }
    [MaxLength(30)] public string? ReglaPago { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(120)] public string? UpdatedBy { get; set; }
}

/// <summary>18/11/2026 — Facturas recibidas: comentario y pago marcado de UNA factura de ARCA.</summary>
[Table("Compras_FacturaExtra")]
public class ComprasFacturaExtra
{
    [Key, MaxLength(40)] public string IdComprobante { get; set; } = "";
    [MaxLength(500)] public string? Comentario { get; set; }
    [MaxLength(30)] public string? PagoMedio { get; set; }
    public DateTime? PagoFecha { get; set; }
    public int? PagoMpMovId { get; set; }
    public int? PagoExtractoMovId { get; set; }
    [MaxLength(1000)] public string? Rechazados { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(120)] public string? UpdatedBy { get; set; }
}
