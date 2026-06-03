using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-06-03: registra que un movimiento del extracto bancario fue marcado como "no es de
/// este cliente" desde la pantalla de Cobranzas. La proxima vez que se abre el modal de
/// cobranza de ese cliente, el movimiento NO se sugiere. Es POR CLIENTE: el mismo movimiento
/// puede aparecer normalmente para otros clientes.
///
/// Es REVERSIBLE: el usuario puede ver los descartados y restaurarlos con un click.
/// </summary>
[Table("Cafe_ExtractoMov_DescartadoPorCliente")]
public class CafeExtractoMovDescartadoPorCliente
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int MovimientoId { get; set; }
    [ForeignKey(nameof(MovimientoId))]
    public CafeExtractoMovimiento? Movimiento { get; set; }

    public int ClienteId { get; set; }
    [ForeignKey(nameof(ClienteId))]
    public CafeCliente? Cliente { get; set; }

    public DateTime DescartadoAt { get; set; } = DateTime.UtcNow;

    [MaxLength(120)]
    public string? DescartadoPor { get; set; }
}
