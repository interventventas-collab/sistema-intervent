namespace Web.Services;

public class UploadProgressService
{
    public bool IsUploading { get; private set; }
    public int DoneCount { get; private set; }
    public int TotalCount { get; private set; }
    public int OkCount { get; private set; }
    public int FailCount { get; private set; }

    public event Action? OnChanged;
    public event Action? ReopenRequested;

    public Func<Task>? CancelHandler { get; set; }

    public int Percentage => TotalCount == 0 ? 0 : DoneCount * 100 / TotalCount;

    public void Start(int total)
    {
        IsUploading = true;
        TotalCount = total;
        DoneCount = 0;
        OkCount = 0;
        FailCount = 0;
        OnChanged?.Invoke();
    }

    public void Update(int done, int ok, int fail)
    {
        DoneCount = done;
        OkCount = ok;
        FailCount = fail;
        OnChanged?.Invoke();
    }

    public void Finish()
    {
        IsUploading = false;
        CancelHandler = null;
        OnChanged?.Invoke();
    }

    public async Task RequestCancelAsync()
    {
        var h = CancelHandler;
        if (h is not null) await h();
    }

    public void RequestReopen() => ReopenRequested?.Invoke();
}
