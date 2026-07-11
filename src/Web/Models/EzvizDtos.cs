namespace Web.Models;

public class EzvizAccountDto
{
    public int Id { get; set; }
    public string? Alias { get; set; }
    public bool HasCredentials { get; set; }
    public bool IsActive { get; set; }
    public string ApiHost { get; set; } = "https://open.ezvizlife.com";
    public string? AreaDomain { get; set; }
    public DateTime? TokenExpiresAt { get; set; }
    public bool LastSyncOk { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class SaveEzvizAccountRequest
{
    public string? AppKey { get; set; }
    public string? AppSecret { get; set; }
    public string? Alias { get; set; }
    public bool IsActive { get; set; } = true;
    public string? ApiHost { get; set; }
}

public class EzvizProbarResultDto
{
    public bool Ok { get; set; }
    public string? AreaDomain { get; set; }
    public string? Error { get; set; }
}

public class EzvizCamaraDto
{
    public string DeviceSerial { get; set; } = string.Empty;
    public int ChannelNo { get; set; }
    public string? Nombre { get; set; }
    public bool Online { get; set; }
    public bool Encriptada { get; set; }
    public string? PicUrl { get; set; }
}

public class EzvizLiveDto
{
    public bool Ok { get; set; }
    public string? Url { get; set; }
    public string? AccessToken { get; set; }
    public string? Error { get; set; }
}
