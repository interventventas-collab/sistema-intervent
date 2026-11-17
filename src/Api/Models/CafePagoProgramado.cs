using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 17/11/2026 — Pago a proveedor PROGRAMADO: "el viernes le pago $X a tal proveedor".
/// No mueve plata: solo anota cuándo y cuánto. Se ve arriba de "¿A quién le debo?", en el Almanaque
/// y como aviso en la tarjeta Compras del inicio. Con "Pagar" se abre el Nuevo pago de siempre ya
/// completo; al guardarlo, este queda PAGADO con el PagoId.
/// </summary>
[Table("Cafe_PagosProgramados")]
public class CafePagoProgramado
{
    [Key]
    public int Id { get; set; }
    public int ProveedorId { get; set; }
    [ForeignKey(nameof(ProveedorId))]
    public CafeProveedor? Proveedor { get; set; }
    /// <summary>Día argentino, sin hora.</summary>
    public DateTime Fecha { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal Importe { get; set; }
    /// <summary>TRANSFERENCIA | ECHEQ | EFECTIVO</summary>
    [Required, MaxLength(20)]
    public string Medio { get; set; } = "TRANSFERENCIA";
    /// <summary>Claves de la cuenta corriente que cubre ("AFIP:..." / "DEU:..."), separadas por "|". Vacío = a cuenta.</summary>
    [MaxLength(2000)]
    public string? Claves { get; set; }
    [MaxLength(500)]
    public string? Nota { get; set; }
    /// <summary>Si se dividió en partes: número de esta parte y total de partes (1 de 3).</summary>
    public int Parte { get; set; } = 1;
    public int Partes { get; set; } = 1;
    /// <summary>PENDIENTE | PAGADO | ANULADO</summary>
    [Required, MaxLength(20)]
    public string Estado { get; set; } = "PENDIENTE";
    public int? PagoId { get; set; }
    [MaxLength(100)]
    public string? Operador { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
