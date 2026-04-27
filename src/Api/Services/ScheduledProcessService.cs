using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class ScheduledProcessService
{
    private readonly AppDbContext _db;

    public ScheduledProcessService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<ScheduledProcessDto>> GetAllAsync()
    {
        return await _db.ScheduledProcesses
            .OrderBy(p => p.Name)
            .Select(p => new ScheduledProcessDto(
                p.Id, p.Code, p.Name, p.Description,
                p.TriggerType, p.IntervalMinutes, p.DailyAtTime, p.CronExpression,
                p.IsEnabled, p.LastRunAt, p.LastRunStatus, p.LastRunDurationMs, p.NextRunAt))
            .ToListAsync();
    }

    public async Task<ScheduledProcessDto?> GetByCodeAsync(string code)
    {
        var p = await _db.ScheduledProcesses.FirstOrDefaultAsync(x => x.Code == code);
        if (p is null) return null;

        return new ScheduledProcessDto(
            p.Id, p.Code, p.Name, p.Description,
            p.TriggerType, p.IntervalMinutes, p.DailyAtTime, p.CronExpression,
            p.IsEnabled, p.LastRunAt, p.LastRunStatus, p.LastRunDurationMs, p.NextRunAt);
    }

    public async Task<ScheduledProcessDto?> UpdateScheduleAsync(string code, UpdateScheduleRequest request)
    {
        var process = await _db.ScheduledProcesses.FirstOrDefaultAsync(p => p.Code == code);
        if (process is null) return null;

        process.TriggerType = request.TriggerType;
        process.IntervalMinutes = request.IntervalMinutes;
        process.DailyAtTime = request.DailyAtTime;
        process.IsEnabled = request.IsEnabled;
        process.UpdatedAt = DateTime.UtcNow;

        // Calculate next run if enabling
        if (request.IsEnabled)
        {
            process.NextRunAt = CalculateNextRun(process);
        }
        else
        {
            process.NextRunAt = null;
        }

        await _db.SaveChangesAsync();

        return new ScheduledProcessDto(
            process.Id, process.Code, process.Name, process.Description,
            process.TriggerType, process.IntervalMinutes, process.DailyAtTime, process.CronExpression,
            process.IsEnabled, process.LastRunAt, process.LastRunStatus, process.LastRunDurationMs, process.NextRunAt);
    }

    public async Task<RunProcessResponse> RunNowAsync(string code)
    {
        var process = await _db.ScheduledProcesses.FirstOrDefaultAsync(p => p.Code == code);
        if (process is null)
            return new RunProcessResponse(false, "Proceso no encontrado");

        if (process.LastRunStatus == "Running")
            return new RunProcessResponse(false, "El proceso ya esta ejecutandose");

        // Set NextRunAt to now so the scheduler picks it up immediately
        process.NextRunAt = DateTime.UtcNow;
        process.IsEnabled = true;
        process.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return new RunProcessResponse(true, "Proceso programado para ejecucion inmediata");
    }

    public async Task<ProcessLogListResponse> GetLogsAsync(string? processCode, int page, int pageSize)
    {
        var query = _db.ProcessExecutionLogs.AsQueryable();

        if (!string.IsNullOrEmpty(processCode))
            query = query.Where(l => l.ProcessCode == processCode);

        var total = await query.CountAsync();
        var logs = await query
            .OrderByDescending(l => l.StartedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new ProcessLogDto(
                l.Id, l.ProcessCode, l.StartedAt, l.FinishedAt,
                l.Status, l.DurationMs, l.ResultSummary, l.ErrorMessage))
            .ToListAsync();

        return new ProcessLogListResponse(logs, total, page, pageSize);
    }

    public static DateTime? CalculateNextRun(ScheduledProcess process)
    {
        var now = DateTime.UtcNow;
        return process.TriggerType switch
        {
            "Interval" when process.IntervalMinutes > 0
                => now.AddMinutes(process.IntervalMinutes.Value),
            "DailyAt" when !string.IsNullOrEmpty(process.DailyAtTime)
                => CalculateNextDailyRun(process.DailyAtTime, now),
            _ => null
        };
    }

    private static DateTime CalculateNextDailyRun(string timeStr, DateTime now)
    {
        var parts = timeStr.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var hour) || !int.TryParse(parts[1], out var minute))
            return now.AddDays(1);

        var today = now.Date.AddHours(hour).AddMinutes(minute);
        return today > now ? today : today.AddDays(1);
    }
}
