using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-11-27: "Recorrer estantes" (celu del depósito): un estante revisado — quién, cuándo,
/// cuántos productos tenía y cuántos stocks se corrigieron. Un estante cuenta como revisado si tiene
/// una revisión después del inicio del recorrido actual (AppSettings "deposito.recorrido.desde").</summary>
[Table("Deposito_EstanteRevisiones")]
public class DepositoEstanteRevision
{
    [Key]
    public int Id { get; set; }
    public int ElementoId { get; set; }
    public int Cara { get; set; } = 1;
    public int Posicion { get; set; } = 1;
    public int Nivel { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public int? OperadorId { get; set; }
    [MaxLength(100)]
    public string? Por { get; set; }
    public int Productos { get; set; }
    public int Ajustes { get; set; }
}
