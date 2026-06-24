namespace Api.DTOs;

public record VaultStatusDto(bool IsInitialized, bool IsUnlocked, int AutoLockMinutes);

public class VaultSetupRequest
{
    public string Password { get; set; } = string.Empty;
    public int? AutoLockMinutes { get; set; }
}

public class VaultUnlockRequest
{
    public string Password { get; set; } = string.Empty;
}

public record VaultUnlockResponse(string Token, int AutoLockMinutes);

public record VaultEntryDto(
    int Id,
    string Servicio,
    string? Categoria,
    string Usuario,
    string? Otro,
    string Password,
    string? Pin,
    string? Mail,
    string? Enlace,
    string? Notas,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public class VaultUpsertEntryRequest
{
    public string Servicio { get; set; } = string.Empty;
    public string? Categoria { get; set; }
    public string Usuario { get; set; } = string.Empty;
    public string? Otro { get; set; }
    public string Password { get; set; } = string.Empty;
    public string? Pin { get; set; }
    public string? Mail { get; set; }
    public string? Enlace { get; set; }
    public string? Notas { get; set; }
}

public record VaultImportResultDto(int Creadas, int Actualizadas, int Saltadas, List<string> Errores);

public class VaultGenerateRequest
{
    public int Length { get; set; } = 20;
    public bool IncludeSymbols { get; set; } = true;
    public bool IncludeNumbers { get; set; } = true;
    public bool IncludeUppercase { get; set; } = true;
}

public record VaultGenerateResponse(string Password);

public class VaultChangeMasterRequest
{
    public string OldPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public class VaultUpdateSettingsRequest
{
    public int? AutoLockMinutes { get; set; }
}
