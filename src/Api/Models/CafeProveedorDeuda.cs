using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 17/09/2026 — Deuda con un proveedor cargada a mano: una COTIZACION que nos pasó (no oficial,
/// sin factura de AFIP) o el SALDO_INICIAL con el que arranca su cuenta corriente (puede ser
/// oficial o no oficial). Las facturas oficiales NO van acá: salen solas de AFIP.
/// No tiene productos a propósito: el stock se carga aparte.
/// </summary>
[Table("Cafe_ProveedorDeudas")]
public class CafeProveedorDeuda
{
    [Key]
    public int Id { get; set; }

    public int ProveedorId { get; set; }
    [ForeignKey(nameof(ProveedorId))]
    public CafeProveedor? Proveedor { get; set; }

    /// <summary>COTIZACION | SALDO_INICIAL</summary>
    [Required, MaxLength(20)]
    public string Tipo { get; set; } = "COTIZACION";

    /// <summary>Solo un saldo inicial puede ser oficial (lo que se debía en facturas antes de arrancar).</summary>
    public bool Oficial { get; set; }

    /// <summary>Día argentino, sin hora.</summary>
    public DateTime Fecha { get; set; }

    [MaxLength(50)]
    public string? Numero { get; set; }

    /// <summary>Positivo = le debemos. Un saldo inicial negativo es plata a nuestro favor.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Importe { get; set; }

    [MaxLength(500)]
    public string? Observaciones { get; set; }

    /// <summary>Foto o PDF de la cotización, relativo a la carpeta de archivos.</summary>
    [MaxLength(500)]
    public string? ArchivoPath { get; set; }

    [MaxLength(200)]
    public string? ArchivoNombre { get; set; }

    /// <summary>VIGENTE | ANULADA</summary>
    [Required, MaxLength(20)]
    public string Estado { get; set; } = "VIGENTE";

    [MaxLength(100)]
    public string? Operador { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
