namespace Web.Models;

public class IntegrationDto
{
    public int Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string? AppId { get; set; }
    public bool HasSecret { get; set; }
    public string? RedirectUrl { get; set; }
    public string? Settings { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class SaveIntegrationRequest
{
    public string Provider { get; set; } = string.Empty;
    public string? AppId { get; set; }
    public string? AppSecret { get; set; }
    public string? RedirectUrl { get; set; }
    public string? Settings { get; set; }
    public bool IsActive { get; set; }
}

public class MeliAccountDto
{
    public int Id { get; set; }
    public long MeliUserId { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string? Email { get; set; }
    public bool TokenValid { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class MeliAuthUrlResponse
{
    public string AuthUrl { get; set; } = string.Empty;
}

public class MeliCallbackRequest
{
    public string Code { get; set; } = string.Empty;
}

public class OpenAiModelDto
{
    public string Id { get; set; } = string.Empty;
}

public class ClaudeModelDto
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

// ===== ARCA (scraping) =====
public class ArcaAccountDto
{
    public int Id { get; set; }
    public string Cuit { get; set; } = string.Empty;
    public string? CuitLogin { get; set; }
    public string? Alias { get; set; }
    public bool HasPassword { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class CreateArcaAccountRequest
{
    public string Cuit { get; set; } = string.Empty;
    public string? CuitLogin { get; set; }
    public string? Alias { get; set; }
    public string Password { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public class UpdateArcaAccountRequest
{
    public string? Cuit { get; set; }
    public string? CuitLogin { get; set; }
    public string? Alias { get; set; }
    /// <summary>Si null o vacío, no se cambia la contraseña.</summary>
    public string? Password { get; set; }
    public bool? IsActive { get; set; }
}

// ===== ARCA test (login + scraping) =====
public class ArcaTestStatusDto
{
    public bool Running { get; set; }
    public string Step { get; set; } = "";
    public ArcaTestResultDto? Result { get; set; }
}

public class ArcaTestResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Titular { get; set; }
    public List<ArcaDomicilioDto>? Domicilios { get; set; }
    public List<ArcaActividadDto>? Actividades { get; set; }
}

public class ArcaDomicilioDto
{
    public string Tipo { get; set; } = "";
    public string Direccion { get; set; } = "";
    public string Jurisdiccion { get; set; } = "";
}

public class ArcaActividadDto
{
    public string Descripcion { get; set; } = "";
    public string FechaInicio { get; set; } = "";
}
