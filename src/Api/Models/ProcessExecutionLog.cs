namespace Api.Models;

public class ProcessExecutionLog
{
    public int Id { get; set; }
    public string ProcessCode { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string Status { get; set; } = "Running";
    public int? DurationMs { get; set; }
    public string? ResultSummary { get; set; }
    public string? ErrorMessage { get; set; }

    public ScheduledProcess? Process { get; set; }
}
