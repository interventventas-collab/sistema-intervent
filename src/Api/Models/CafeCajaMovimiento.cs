using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Todo lo que mueve plata en una caja y NO es una cobranza.
/// Sin esto la caja sólo sumaba: las cobranzas entraban y nada salía nunca, asi que el saldo
/// era un acumulado histórico (el 04/09/2026 "Efectivo" mostraba $142.736.024).
///
/// El Importe lleva el signo: negativo lo que sale, positivo lo que entra.
/// </summary>
[Table("Cafe_CajaMovimientos")]
public class CafeCajaMovimiento
{
    [Key]
    public int Id { get; set; }

    public int CajaId { get; set; }

    /// <summary>Fecha en que se movió la plata (argentina), no la de carga.</summary>
    public DateTime Fecha { get; set; }

    /// <summary>SALIDA | ENTRADA | TRANSFERENCIA | ARQUEO</summary>
    [Required, MaxLength(20)]
    public string Tipo { get; set; } = "SALIDA";

    /// <summary>Con signo: negativo sale, positivo entra.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Importe { get; set; }

    /// <summary>En castellano y para el dueño: "nafta", "adelanto a Walter", "deposito al banco".</summary>
    [Required, MaxLength(300)]
    public string Motivo { get; set; } = string.Empty;

    /// <summary>Las dos patas de una transferencia comparten este id para poder deshacerla junta.</summary>
    public int? TransferenciaGrupoId { get; set; }

    /// <summary>Quién lo cargó, para poder preguntarle después.</summary>
    [MaxLength(100)]
    public string? CargadoPor { get; set; }

    /// <summary>
    /// Cuándo se anuló (05/09/2026). Los movimientos NO se borran: quedan tachados en la lista y
    /// el saldo los ignora, igual que una cobranza anulada. El dueño lo pidió asi para que quede
    /// el rastro de lo que se dio de baja.
    /// </summary>
    public DateTime? AnuladoAt { get; set; }

    [MaxLength(100)]
    public string? AnuladoPor { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    [ForeignKey(nameof(CajaId))]
    public CafeCaja? Caja { get; set; }
}
