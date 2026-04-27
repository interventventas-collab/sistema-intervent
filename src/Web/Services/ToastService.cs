namespace Web.Services;

public class ToastService
{
    public event Action<string, ToastType>? OnShow;

    public void Show(string message, ToastType type = ToastType.Info)
    {
        OnShow?.Invoke(message, type);
    }

    public void Success(string message) => Show(message, ToastType.Success);
    public void Error(string message) => Show(message, ToastType.Error);
    public void Info(string message) => Show(message, ToastType.Info);
    public void Warning(string message) => Show(message, ToastType.Warning);
}

public enum ToastType
{
    Success,
    Error,
    Info,
    Warning
}
