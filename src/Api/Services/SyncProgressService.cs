using System.Collections.Concurrent;

namespace Api.Services;

/// <summary>
/// Singleton service to track sync progress across API calls.
/// Frontend polls GET /api/meli/items/sync/progress to get updates.
/// </summary>
public class SyncProgressService
{
    private readonly ConcurrentDictionary<string, SyncProgressInfo> _progress = new();

    public string StartSync(string description)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        _progress[id] = new SyncProgressInfo
        {
            Id = id,
            Description = description,
            Status = "running",
            StartedAt = DateTime.UtcNow
        };
        return id;
    }

    public void Update(string id, Action<SyncProgressInfo> updater)
    {
        if (_progress.TryGetValue(id, out var info))
            updater(info);
    }

    public SyncProgressInfo? Get(string id)
    {
        _progress.TryGetValue(id, out var info);
        return info;
    }

    public SyncProgressInfo? GetLatest()
    {
        return _progress.Values
            .OrderByDescending(p => p.StartedAt)
            .FirstOrDefault();
    }

    public void Complete(string id, string summary)
    {
        if (_progress.TryGetValue(id, out var info))
        {
            info.Status = "completed";
            info.CurrentStep = summary;
            info.FinishedAt = DateTime.UtcNow;
        }
    }

    public void Fail(string id, string error)
    {
        if (_progress.TryGetValue(id, out var info))
        {
            info.Status = "error";
            info.CurrentStep = error;
            info.FinishedAt = DateTime.UtcNow;
        }
    }

    public void Remove(string id)
    {
        _progress.TryRemove(id, out _);
    }

    /// <summary>
    /// Cleanup old entries (older than 10 minutes)
    /// </summary>
    public void Cleanup()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        foreach (var kvp in _progress)
        {
            if (kvp.Value.FinishedAt.HasValue && kvp.Value.FinishedAt.Value < cutoff)
                _progress.TryRemove(kvp.Key, out _);
        }
    }
}

public class SyncProgressInfo
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "running"; // running, completed, error
    public string CurrentStep { get; set; } = "";
    public string CurrentAccount { get; set; } = "";
    public int AccountIndex { get; set; }
    public int TotalAccounts { get; set; }
    public int TotalItemsFound { get; set; }
    public int ItemsSynced { get; set; }
    public int TotalErrors { get; set; }
    public int Percentage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}
