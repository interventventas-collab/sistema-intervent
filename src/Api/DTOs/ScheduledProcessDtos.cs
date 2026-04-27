namespace Api.DTOs;

public record ScheduledProcessDto(
    int Id,
    string Code,
    string Name,
    string? Description,
    string TriggerType,
    int? IntervalMinutes,
    string? DailyAtTime,
    string? CronExpression,
    bool IsEnabled,
    DateTime? LastRunAt,
    string? LastRunStatus,
    int? LastRunDurationMs,
    DateTime? NextRunAt
);

public record UpdateScheduleRequest(
    string TriggerType,
    int? IntervalMinutes,
    string? DailyAtTime,
    bool IsEnabled
);

public record ProcessLogDto(
    int Id,
    string ProcessCode,
    DateTime StartedAt,
    DateTime? FinishedAt,
    string Status,
    int? DurationMs,
    string? ResultSummary,
    string? ErrorMessage
);

public record ProcessLogListResponse(
    List<ProcessLogDto> Logs,
    int Total,
    int Page,
    int PageSize
);

public record RunProcessResponse(
    bool Started,
    string? Message
);
