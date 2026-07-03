namespace Web.Models;

public class ShellAccountDto
{
    public int Id { get; set; }
    public string Usuario { get; set; } = "";
    public string? Alias { get; set; }
    public bool HasPassword { get; set; }
    public bool IsActive { get; set; }
    public string? LastSaldo { get; set; }
    public DateTime? LastSaldoAt { get; set; }
    public bool LastSyncOk { get; set; }
    public string? LastError { get; set; }
    public bool AutoSyncEnabled { get; set; }
    public string? AutoSyncTimes { get; set; }
    public DateTime? LastAutoSyncAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class SaveShellAccountRequest
{
    public string Usuario { get; set; } = "";
    public string? Password { get; set; }
    public string? Alias { get; set; }
    public bool IsActive { get; set; } = true;
    public bool AutoSyncEnabled { get; set; } = false;
    public string? AutoSyncTimes { get; set; }
}

public class ShellSincronizarResultDto
{
    public bool Ok { get; set; }
    public string? Saldo { get; set; }
    public string? Error { get; set; }
}

public class ShellTestStatusDto
{
    public bool Running { get; set; }
    public string Step { get; set; } = "";
    public ShellTestResultDto? Result { get; set; }
}

public class ShellTestResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public bool? LoggedIn { get; set; }
    public string? Saldo { get; set; }
}
