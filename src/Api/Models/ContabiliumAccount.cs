using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Credenciales de la API de Contabilium. Singleton (Id=1) — un solo registro porque
/// el negocio tiene una sola cuenta. Para soportar multi-cuenta en el futuro alcanzaría
/// con relajar la PK.
///
/// OAuth flow:
///   POST https://rest.contabilium.com/token
///     grant_type=client_credentials
///     client_id=<email>
///     client_secret=<apiKey>
///   → { access_token, expires_in (segundos, ~86400 = 24h) }
///
/// El token se cachea en este registro hasta que vence; ahí se renueva.
/// </summary>
[Table("ContabiliumAccounts")]
public class ContabiliumAccount
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string Email { get; set; } = "";

    /// <summary>API Key (client_secret) que el usuario regenera desde Contabilium.</summary>
    [Required]
    [MaxLength(200)]
    public string ApiKey { get; set; } = "";

    /// <summary>Access token cacheado. Se renueva cuando vence.</summary>
    public string? AccessToken { get; set; }
    public DateTime? AccessTokenExpiresAt { get; set; }

    public DateTime? LastSyncAt { get; set; }
    public string? LastSyncError { get; set; }
    public int? LastSyncCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
