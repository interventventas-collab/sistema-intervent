namespace Web.Services;

/// <summary>
/// Singleton service that tracks sync progress globally across all pages.
/// Polls the API for updates and notifies subscribers (MainLayout, Publicaciones, etc.)
/// </summary>
public class SyncProgressTracker : IDisposable
{
    private readonly ApiClient _api;
    private Timer? _pollTimer;
    private bool _isPolling;

    public SyncProgressState State { get; private set; } = new();
    public event Action? OnChange;

    public SyncProgressTracker(ApiClient api)
    {
        _api = api;
    }

    /// <summary>
    /// Start tracking a sync operation. Begins polling the API for progress.
    /// </summary>
    public void StartTracking(string progressId, string description, bool showModal = true)
    {
        State = new SyncProgressState
        {
            IsActive = true,
            ProgressId = progressId,
            Description = description,
            ShowModal = showModal,
            Status = "running"
        };
        NotifyChanged();
        StartPolling();
    }

    /// <summary>
    /// Switch to background mode (close modal, keep topbar indicator).
    /// </summary>
    public void SendToBackground()
    {
        State.ShowModal = false;
        NotifyChanged();
    }

    /// <summary>
    /// Dismiss the completed/error notification.
    /// </summary>
    public void Dismiss()
    {
        StopPolling();
        State = new SyncProgressState();
        NotifyChanged();
    }

    private void StartPolling()
    {
        StopPolling();
        _pollTimer = new Timer(async _ => await PollProgress(), null, 0, 1500);
    }

    private void StopPolling()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    private async Task PollProgress()
    {
        if (_isPolling || string.IsNullOrEmpty(State.ProgressId)) return;
        _isPolling = true;

        try
        {
            var info = await _api.GetSyncProgressAsync(State.ProgressId);
            if (info is null) return;

            State.Status = info.Status ?? "running";
            State.CurrentStep = info.CurrentStep ?? "";
            State.CurrentAccount = info.CurrentAccount ?? "";
            State.AccountIndex = info.AccountIndex;
            State.TotalAccounts = info.TotalAccounts;
            State.TotalItemsFound = info.TotalItemsFound;
            State.ItemsSynced = info.ItemsSynced;
            State.TotalErrors = info.TotalErrors;
            State.Percentage = info.Percentage;

            if (info.Status == "completed" || info.Status == "error")
            {
                StopPolling();
            }

            NotifyChanged();
        }
        catch
        {
            // Ignore poll errors
        }
        finally
        {
            _isPolling = false;
        }
    }

    private void NotifyChanged()
    {
        try { OnChange?.Invoke(); }
        catch { }
    }

    public void Dispose()
    {
        StopPolling();
    }
}

public class SyncProgressState
{
    public bool IsActive { get; set; }
    public bool ShowModal { get; set; }
    public string ProgressId { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "idle"; // idle, running, completed, error
    public string CurrentStep { get; set; } = "";
    public string CurrentAccount { get; set; } = "";
    public int AccountIndex { get; set; }
    public int TotalAccounts { get; set; }
    public int TotalItemsFound { get; set; }
    public int ItemsSynced { get; set; }
    public int TotalErrors { get; set; }
    public int Percentage { get; set; }
}

public class SyncProgressResponse
{
    public string? Id { get; set; }
    public string? Status { get; set; }
    public string? Description { get; set; }
    public string? CurrentStep { get; set; }
    public string? CurrentAccount { get; set; }
    public int AccountIndex { get; set; }
    public int TotalAccounts { get; set; }
    public int TotalItemsFound { get; set; }
    public int ItemsSynced { get; set; }
    public int TotalErrors { get; set; }
    public int Percentage { get; set; }
}
