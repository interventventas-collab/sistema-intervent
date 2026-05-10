using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record IntegrationDto(
    int Id,
    string Provider,
    string? AppId,
    bool HasSecret,
    string? RedirectUrl,
    string? Settings,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record SaveIntegrationRequest(
    [Required][MaxLength(50)] string Provider,
    [MaxLength(255)] string? AppId,
    [MaxLength(255)] string? AppSecret,
    [MaxLength(500)] string? RedirectUrl,
    string? Settings,
    bool IsActive
);

// ===== ARCA (scraping) =====
// La contraseña NUNCA se devuelve al frontend — solo HasPassword indica si hay una guardada.
public record ArcaAccountDto(
    int Id,
    string Cuit,
    string? CuitLogin,
    string? Alias,
    bool HasPassword,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public class CreateArcaAccountRequest
{
    [Required, MaxLength(20)]
    public string Cuit { get; set; } = string.Empty;
    [MaxLength(20)]
    public string? CuitLogin { get; set; }
    [MaxLength(100)]
    public string? Alias { get; set; }
    [Required]
    public string Password { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public class UpdateArcaAccountRequest
{
    [MaxLength(20)]
    public string? Cuit { get; set; }
    [MaxLength(20)]
    public string? CuitLogin { get; set; }
    [MaxLength(100)]
    public string? Alias { get; set; }
    /// <summary>Si viene vacío/null, NO se cambia la contraseña existente.</summary>
    public string? Password { get; set; }
    public bool? IsActive { get; set; }
}
