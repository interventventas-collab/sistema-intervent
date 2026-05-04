namespace Web.Models;

public class VaultStatusDto
{
    public bool IsInitialized { get; set; }
    public bool IsUnlocked { get; set; }
    public int AutoLockMinutes { get; set; }
}

public class VaultUnlockResponse
{
    public string Token { get; set; } = "";
    public int AutoLockMinutes { get; set; }
}

public class VaultEntryDto
{
    public int Id { get; set; }
    public string Servicio { get; set; } = "";
    public string Usuario { get; set; } = "";
    public string Password { get; set; } = "";
    public string? Notas { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class VaultUpsertEntryRequest
{
    public string Servicio { get; set; } = "";
    public string Usuario { get; set; } = "";
    public string Password { get; set; } = "";
    public string? Notas { get; set; }
}

public class VaultGenerateRequest
{
    public int Length { get; set; } = 20;
    public bool IncludeSymbols { get; set; } = true;
    public bool IncludeNumbers { get; set; } = true;
    public bool IncludeUppercase { get; set; } = true;
}

public class VaultGenerateResponse
{
    public string Password { get; set; } = "";
}

public class VaultChangeMasterRequest
{
    public string OldPassword { get; set; } = "";
    public string NewPassword { get; set; } = "";
}

public class VaultSetupRequest
{
    public string Password { get; set; } = "";
    public int? AutoLockMinutes { get; set; }
}

public class VaultUpdateSettingsRequest
{
    public int? AutoLockMinutes { get; set; }
}
