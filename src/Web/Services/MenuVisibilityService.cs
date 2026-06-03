using Web.Services;

namespace Web.Services;

/// <summary>
/// Singleton: cachea el dict de visibilidad del menú por rol.
/// Lo consume el sidebar para saber qué mostrar a DEPOSITO/OFICINA, y el modo edición
/// del admin lo modifica con SetAsync (persiste al backend y refresca el cache).
/// Pedido del usuario 2026-05-28.
/// </summary>
public class MenuVisibilityService
{
    private readonly ApiClient _api;
    private Dictionary<string, HashSet<string>>? _cache;
    private bool _loading = false;

    public event Action? OnChange;

    public MenuVisibilityService(ApiClient api) { _api = api; }

    public async Task EnsureLoadedAsync()
    {
        if (_cache != null || _loading) return;
        _loading = true;
        try
        {
            var dict = await _api.GetMenuVisibilityAsync();
            _cache = new(StringComparer.OrdinalIgnoreCase);
            if (dict != null)
            {
                foreach (var kv in dict)
                {
                    _cache[kv.Key] = new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
            _cache = new(StringComparer.OrdinalIgnoreCase); // arrancar vacío si falla
        }
        finally { _loading = false; }
    }

    public bool IsEnabled(string role, string key)
    {
        if (_cache is null || string.IsNullOrEmpty(role) || string.IsNullOrEmpty(key)) return false;
        return _cache.TryGetValue(role, out var keys) && keys.Contains(key);
    }

    /// <summary>Devuelve la lista de keys habilitadas para un rol (en el orden de inserción).</summary>
    public IReadOnlyList<string> GetEnabledKeys(string role)
    {
        if (_cache is null || string.IsNullOrEmpty(role)) return Array.Empty<string>();
        return _cache.TryGetValue(role, out var keys) ? keys.ToList() : Array.Empty<string>();
    }

    public async Task SetAsync(string role, string key, bool enabled)
    {
        await _api.SetMenuVisibilityAsync(role, key, enabled);
        // Actualizar cache local sin re-fetchear
        if (_cache is null) _cache = new(StringComparer.OrdinalIgnoreCase);
        if (!_cache.TryGetValue(role, out var keys))
        {
            keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _cache[role] = keys;
        }
        if (enabled) keys.Add(key);
        else keys.Remove(key);
        OnChange?.Invoke();
    }
}
