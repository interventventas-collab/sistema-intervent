using System.Diagnostics;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Api.BackgroundJobs;

public class ProcessSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEnumerable<IScheduledJob> _jobs;
    private readonly ILogger<ProcessSchedulerService> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    public ProcessSchedulerService(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IScheduledJob> jobs,
        ILogger<ProcessSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _jobs = jobs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Process Scheduler started");

        // Wait for the app to fully start
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndRunDueProcesses(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in scheduler loop");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private async Task CheckAndRunDueProcesses(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var dueProcesses = await db.ScheduledProcesses
            .Where(p => p.IsEnabled && p.NextRunAt != null && p.NextRunAt <= now)
            .Where(p => p.LastRunStatus != "Running")
            .ToListAsync(ct);

        foreach (var process in dueProcesses)
        {
            var job = _jobs.FirstOrDefault(j => j.Code == process.Code);
            if (job is null)
            {
                _logger.LogWarning("No job implementation found for code: {Code}", process.Code);
                continue;
            }

            _ = Task.Run(() => RunJob(process.Code, job, ct), ct);
        }
    }

    private async Task RunJob(string processCode, IScheduledJob job, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var process = await db.ScheduledProcesses.FirstAsync(p => p.Code == processCode, ct);

        // Create execution log
        var log = new ProcessExecutionLog
        {
            ProcessCode = processCode,
            StartedAt = DateTime.UtcNow,
            Status = "Running"
        };
        db.ProcessExecutionLogs.Add(log);

        // Mark process as running
        process.LastRunAt = DateTime.UtcNow;
        process.LastRunStatus = "Running";
        await db.SaveChangesAsync(ct);

        var sw = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Starting job: {Code}", processCode);
            var summary = await job.ExecuteAsync(ct);
            sw.Stop();

            log.FinishedAt = DateTime.UtcNow;
            log.Status = "Success";
            log.DurationMs = (int)sw.ElapsedMilliseconds;
            log.ResultSummary = summary;

            process.LastRunStatus = "Success";
            process.LastRunDurationMs = (int)sw.ElapsedMilliseconds;
            process.NextRunAt = ScheduledProcessService.CalculateNextRun(process);
            process.UpdatedAt = DateTime.UtcNow;

            _logger.LogInformation("Job {Code} completed in {Duration}ms", processCode, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();

            log.FinishedAt = DateTime.UtcNow;
            log.Status = "Error";
            log.DurationMs = (int)sw.ElapsedMilliseconds;
            log.ErrorMessage = ex.Message;

            process.LastRunStatus = "Error";
            process.LastRunDurationMs = (int)sw.ElapsedMilliseconds;
            process.NextRunAt = ScheduledProcessService.CalculateNextRun(process);
            process.UpdatedAt = DateTime.UtcNow;

            _logger.LogError(ex, "Job {Code} failed after {Duration}ms", processCode, sw.ElapsedMilliseconds);
        }

        await db.SaveChangesAsync(ct);
    }
}
