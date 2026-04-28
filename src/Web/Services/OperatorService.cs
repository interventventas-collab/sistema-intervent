using Microsoft.JSInterop;

namespace Web.Services;

/// <summary>
/// Mantiene quien es el operador (persona fisica) que esta trabajando ahora.
/// Lo guarda en localStorage. El nombre se manda en cada request al backend
/// como header X-Operator-Name para auditoria.
/// </summary>
public class OperatorService
{
    private const string StorageKey = "current_operator";
    private readonly IJSRuntime _js;
    private string? _current;

    public static readonly string[] Operators =
    {
        "OSMAR", "GERMAN", "GABRIEL", "MIGUEL", "MAXI", "ALEXIS", "WALTER", "RODRIGO"
    };

    public string? Current => _current;
    public bool HasOperator => !string.IsNullOrWhiteSpace(_current);

    public event Action? OnChange;

    public OperatorService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task LoadAsync()
    {
        try
        {
            var stored = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrWhiteSpace(stored) && Operators.Contains(stored))
                _current = stored;
        }
        catch { /* primer arranque, sin nada */ }
    }

    public async Task SetAsync(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && !Operators.Contains(name))
            return;
        _current = string.IsNullOrWhiteSpace(name) ? null : name;
        try
        {
            if (_current is null)
                await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
            else
                await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, _current);
        }
        catch { }
        OnChange?.Invoke();
    }

    public async Task ClearAsync() => await SetAsync(null);
}
