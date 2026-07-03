using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Credenciales de Shell Flota (Office/Edenred) para leer el saldo disponible por
/// scraping. El login pide usuario+clave y un token OTP por mail; el robot lee ese
/// token de la casilla de Gmail ya conectada (integración email-smtp). La clave se
/// guarda en texto (mismo criterio que Galicia/Arca).
/// </summary>
[Table("ShellAccounts")]
public class ShellAccount
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Usuario { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Alias { get; set; }

    public bool IsActive { get; set; } = true;

    // --- Último saldo leído ---
    [MaxLength(50)]
    public string? LastSaldo { get; set; }
    public DateTime? LastSaldoAt { get; set; }
    public bool LastSyncOk { get; set; } = false;
    [MaxLength(500)]
    public string? LastError { get; set; }

    // --- Automático (mismos horarios configurables que Galicia) ---
    public bool AutoSyncEnabled { get; set; } = false;
    [MaxLength(200)]
    public string? AutoSyncTimes { get; set; }
    public DateTime? LastAutoSyncAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
