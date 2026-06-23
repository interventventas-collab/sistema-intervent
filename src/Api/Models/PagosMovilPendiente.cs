using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("PagosMovil_Pendientes")]
public class PagosMovilPendiente
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(20)]
    public string Tipo { get; set; } = "empleado"; // empleado | proveedor

    public int? EmpleadoId { get; set; }
    [ForeignKey(nameof(EmpleadoId))]
    public NomEmpleado? Empleado { get; set; }

    public int? LiquidacionId { get; set; }
    [ForeignKey(nameof(LiquidacionId))]
    public NomLiquidacion? Liquidacion { get; set; }

    public int? ProveedorId { get; set; }
    [ForeignKey(nameof(ProveedorId))]
    public CafeProveedor? Proveedor { get; set; }

    [Required, MaxLength(60)]
    public string Concepto { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Monto { get; set; }

    [Required, MaxLength(30)]
    public string MedioPago { get; set; } = "efectivo";

    [MaxLength(500)]
    public string? Notas { get; set; }

    [Required, MaxLength(20)]
    public string Estado { get; set; } = "PENDIENTE"; // PENDIENTE | CONFIRMADO | RECHAZADO

    public int CreadoPorUsuarioId { get; set; }
    [ForeignKey(nameof(CreadoPorUsuarioId))]
    public User? CreadoPor { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? ConfirmadoPorUsuarioId { get; set; }
    [ForeignKey(nameof(ConfirmadoPorUsuarioId))]
    public User? ConfirmadoPor { get; set; }

    public DateTime? ConfirmadoAt { get; set; }

    [MaxLength(300)]
    public string? MotivoRechazo { get; set; }

    public int? NomPagoId { get; set; }
    [ForeignKey(nameof(NomPagoId))]
    public NomPago? NomPago { get; set; }

    public int? CafePagoProveedorId { get; set; }
    [ForeignKey(nameof(CafePagoProveedorId))]
    public CafePagoProveedor? CafePagoProveedor { get; set; }

    public List<PagosMovilPendienteComprobante> Comprobantes { get; set; } = new();
}

[Table("PagosMovil_PendientesComprobantes")]
public class PagosMovilPendienteComprobante
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int PendienteId { get; set; }
    [ForeignKey(nameof(PendienteId))]
    public PagosMovilPendiente? Pendiente { get; set; }

    public int CompraId { get; set; }
    [ForeignKey(nameof(CompraId))]
    public CafeCompra? Compra { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Importe { get; set; }
}
