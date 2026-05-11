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

// ===== ARCA (webservice) — certificados .pfx =====
// Atención: Password sí se devuelve al DTO porque el modal de edición debe poder
// mostrarla con el ojito 👁. Si en algún momento querés que sea read-only, sacala.
public record ArcaWebserviceAccountDto(
    int Id,
    string Cuit,
    string? Alias,
    string FileName,
    string FilePath,
    string? Password,
    string Environment,
    DateTime? ExpiresAt,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

/// <summary>Body de update — solo los campos editables (no FileName ni Cuit ni FilePath).</summary>
public class UpdateArcaWebserviceAccountRequest
{
    [MaxLength(100)]
    public string? Alias { get; set; }
    /// <summary>Si viene null, NO se toca la contraseña actual. Si viene "" se setea como vacía.</summary>
    public string? Password { get; set; }
    [MaxLength(20)]
    public string? Environment { get; set; }
    public bool? IsActive { get; set; }
}

// ===== Wizard de generación de certificado =====
public class GenerateCsrRequest
{
    [Required, MaxLength(20)]
    public string Cuit { get; set; } = string.Empty;
    [Required, MaxLength(100)]
    public string Alias { get; set; } = string.Empty;
}

public record GenerateCsrResponseDto(int Id, string FileName, string CsrPem, string Subject);
