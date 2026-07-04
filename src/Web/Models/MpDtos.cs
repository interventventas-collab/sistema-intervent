namespace Web.Models;

public class MpAccountDto
{
    public int Id { get; set; }
    public string? Alias { get; set; }
    public bool HasToken { get; set; }
    public bool IsActive { get; set; }
    public long? MpUserId { get; set; }
    public string? Nickname { get; set; }
    public string? SiteId { get; set; }
    public decimal? LastSaldoDisponible { get; set; }
    public decimal? LastSaldoTotal { get; set; }
    public DateTime? LastSaldoAt { get; set; }
    public bool LastSyncOk { get; set; }
    public string? LastError { get; set; }
    public bool AutoSyncEnabled { get; set; }
    public string? AutoSyncTimes { get; set; }
    public DateTime? LastAutoSyncAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class SaveMpAccountRequest
{
    public string? AccessToken { get; set; }
    public string? Alias { get; set; }
    public bool IsActive { get; set; } = true;
    public bool AutoSyncEnabled { get; set; } = false;
    public string? AutoSyncTimes { get; set; }
}

public class MpSincronizarResultDto
{
    public bool Ok { get; set; }
    public decimal? Disponible { get; set; }
    public decimal? Total { get; set; }
    public string? Error { get; set; }
}
