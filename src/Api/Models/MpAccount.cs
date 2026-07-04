using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Cuenta de Mercado Pago conectada por API oficial (no scraping). Guarda el Access Token
/// de produccion (APP_USR-...) que el usuario saca de mercadopago.com.ar/developers. Con ese
/// token la API lee el saldo de la cuenta. El token se guarda en texto (mismo criterio que
/// Galicia/Shell/Arca — protege el acceso a la DB).
///
/// Etapa 1: solo saldo. Las etapas siguientes (movimientos, ventas cobradas, pagos,
/// conciliacion) suman sobre esta misma cuenta.
///
/// Pedido de Osmar 2026-07-04.
/// </summary>
[Table("MpAccounts")]
public class MpAccount
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Access Token de produccion de Mercado Pago (APP_USR-...). Texto — la API lo usa.</summary>
    [Required]
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Alias amigable opcional (ej "Mercado Pago Palanica").</summary>
    [MaxLength(100)]
    public string? Alias { get; set; }

    public bool IsActive { get; set; } = true;

    // --- Datos de la cuenta (traidos de /users/me al conectar) ---
    /// <summary>ID numerico del usuario de Mercado Pago.</summary>
    public long? MpUserId { get; set; }
    [MaxLength(120)]
    public string? Nickname { get; set; }
    [MaxLength(10)]
    public string? SiteId { get; set; }

    // --- Ultimo saldo leido ---
    [Column(TypeName = "decimal(18,2)")]
    public decimal? LastSaldoDisponible { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? LastSaldoTotal { get; set; }
    public DateTime? LastSaldoAt { get; set; }
    public bool LastSyncOk { get; set; } = false;
    [MaxLength(500)]
    public string? LastError { get; set; }

    // --- Automatico (mismos horarios configurables que Galicia/Shell) ---
    public bool AutoSyncEnabled { get; set; } = false;
    [MaxLength(200)]
    public string? AutoSyncTimes { get; set; }
    public DateTime? LastAutoSyncAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
