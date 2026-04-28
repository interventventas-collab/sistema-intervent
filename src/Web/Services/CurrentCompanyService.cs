using Microsoft.JSInterop;

namespace Web.Services;

/// <summary>
/// Mantiene cual es la EMPRESA con la que el usuario esta trabajando ahora.
/// Persiste en localStorage. Es independiente del operador (persona fisica)
/// que opera el sistema. Se usa como marca por defecto al emitir comprobantes
/// y como titulo en el topbar.
/// </summary>
public class CurrentCompanyService
{
    private const string StorageKey = "current_company";
    private readonly IJSRuntime _js;
    private string? _current;

    public static readonly string[] Companies =
    {
        "INTERVENT", "INTEREVENTOS", "FRIKAF", "PALANICA"
    };

    public string? Current => _current;
    public bool HasCompany => !string.IsNullOrWhiteSpace(_current);

    public event Action? OnChange;

    public CurrentCompanyService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task LoadAsync()
    {
        try
        {
            var stored = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrWhiteSpace(stored) && Companies.Contains(stored))
                _current = stored;
        }
        catch { /* primer arranque */ }
    }

    public async Task SetAsync(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && !Companies.Contains(name))
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
