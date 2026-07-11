using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Cuenta de EZVIZ (cámaras) conectada por la API oficial (EZVIZ Open Platform).
/// El usuario saca un appKey + appSecret de la web de desarrolladores de EZVIZ; con eso el
/// sistema pide un accessToken (dura ~7 días) que renueva solo, y con el token lista las
/// cámaras y arma el link de video en vivo.
///
/// El appKey/appSecret se guardan en texto (mismo criterio que Mercado Pago/Galicia/Shell:
/// lo que protege esto es el acceso a la DB). El accessToken se cachea acá para no pedirlo
/// en cada request.
///
/// Pedido de Osmar 2026-07-10: "ver las cámaras del depósito en el dashboard".
/// </summary>
[Table("EzvizAccounts")]
public class EzvizAccount
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>appKey de la app creada en la web de desarrolladores de EZVIZ.</summary>
    [Required]
    [MaxLength(200)]
    public string AppKey { get; set; } = string.Empty;

    /// <summary>appSecret de la app de EZVIZ. Texto — la API lo usa para pedir el token.</summary>
    [Required]
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>Alias amigable opcional (ej "Cámaras depósito 9 de Abril").</summary>
    [MaxLength(120)]
    public string? Alias { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Host base de la API de EZVIZ para pedir el token, según la región de la cuenta.
    /// Global = https://open.ezvizlife.com. Se puede cambiar si la cuenta es de otra región.</summary>
    [MaxLength(200)]
    public string ApiHost { get; set; } = "https://open.ezvizlife.com";

    // --- Token cacheado (se pide/renueva solo con appKey+appSecret) ---
    /// <summary>accessToken vigente devuelto por EZVIZ. Se renueva antes de que expire.</summary>
    public string? AccessToken { get; set; }
    public DateTime? TokenExpiresAt { get; set; }

    /// <summary>Host "de área" que EZVIZ devuelve junto con el token: es el host correcto para
    /// las llamadas de datos (lista de cámaras, etc). Puede diferir del ApiHost.</summary>
    [MaxLength(200)]
    public string? AreaDomain { get; set; }

    // --- Último intento de conexión ---
    public bool LastSyncOk { get; set; } = false;
    [MaxLength(500)]
    public string? LastError { get; set; }
    public DateTime? LastSyncAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
