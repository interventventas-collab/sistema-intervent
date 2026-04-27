namespace Web.Models;

public class BackupFileDto
{
    public int Id { get; set; }
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string BackupType { get; set; } = "";
    public string Status { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class BackupSettingsDto
{
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 1440;
    public int RetentionDays { get; set; } = 7;
    public DateTime? LastRunAt { get; set; }
    public string? LastRunStatus { get; set; }
    public DateTime? NextRunAt { get; set; }
}

public class UpdateBackupSettingsRequest
{
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 1440;
    public int RetentionDays { get; set; } = 7;
}

public class RestoreBackupRequest
{
    public string FileName { get; set; } = "";
    public string Confirmation { get; set; } = "";
}
