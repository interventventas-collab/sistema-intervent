using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-11-28: ajustes sueltos del depósito (clave → valor). NO usar AppSettings para esto: su GET es
/// público. Hoy guarda "PinEstantePublico": el PIN que pide el QR del estante a quien no tiene usuario.</summary>
[Table("Deposito_Ajustes")]
public class DepositoAjuste
{
    [Key, MaxLength(50)]
    public string Clave { get; set; } = "";
    [MaxLength(200)]
    public string Valor { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
    [MaxLength(100)]
    public string? Por { get; set; }
}
