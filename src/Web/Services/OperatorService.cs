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

    // 2026-05-28: MAXI eliminado (ya no trabaja). Lista global = todos los que SÍ trabajan.
    public static readonly string[] Operators =
    {
        "OSMAR", "GERMAN", "GABRIEL", "MIGUEL", "ALEXIS", "WALTER", "RODRIGO"
    };

    // Operadores por rol (para mostrar solo los del equipo correspondiente en el modal).
    public static readonly string[] OperatorsOficina = { "OSMAR", "GERMAN", "GABRIEL" };
    public static readonly string[] OperatorsDeposito = { "ALEXIS", "WALTER", "RODRIGO" };

    /// <summary>Devuelve la lista de operadores que el usuario logueado puede elegir, en base
    /// a sus permisos. Admin (con "cafe") ve todos. OFICINA ve los de su equipo. DEPOSITO ve los suyos.</summary>
    public static string[] ForPermissions(List<string> perms)
    {
        if (perms.Contains("cafe")) return Operators;       // admin / interno: ve todos
        if (perms.Contains("oficina")) return OperatorsOficina;
        if (perms.Contains("deposito")) return OperatorsDeposito;
        return Operators;                                    // fallback
    }

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
