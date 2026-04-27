namespace Web.Models;

public class ScheduledProcessDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string TriggerType { get; set; } = "Interval";
    public int? IntervalMinutes { get; set; }
    public string? DailyAtTime { get; set; }
    public string? CronExpression { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string? LastRunStatus { get; set; }
    public int? LastRunDurationMs { get; set; }
    public DateTime? NextRunAt { get; set; }
}

public class UpdateScheduleRequest
{
    public string TriggerType { get; set; } = "Interval";
    public int? IntervalMinutes { get; set; }
    public string? DailyAtTime { get; set; }
    public bool IsEnabled { get; set; }
}

public class ProcessLogDto
{
    public int Id { get; set; }
    public string ProcessCode { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? DurationMs { get; set; }
    public string? ResultSummary { get; set; }
    public string? ErrorMessage { get; set; }
}

public class ProcessLogListResponse
{
    public List<ProcessLogDto> Logs { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class RunProcessResponse
{
    public bool Started { get; set; }
    public string? Message { get; set; }
}
