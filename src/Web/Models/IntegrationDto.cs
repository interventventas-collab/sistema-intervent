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
