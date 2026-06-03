using Web.Services;

namespace Web.Services;

/// <summary>
/// Singleton: cachea el dict de visibilidad del menú por rol.
///
/// 2026-06-03 — MODELO INVERTIDO (blacklist):
/// Antes: una fila en la tabla MenuVisibility = "este item SE VE para este rol" (whitelist).
/// Ahora: una fila en la tabla MenuVisibility = "este item ESTÁ OCULTO para este rol" (blacklist).
/// Por default todos los items del MenuCatalog se ven; el admin va al panel y oculta lo que no quiere.
///
/// El cache sigue conteniendo lo mismo (set de keys por rol). Solo cambia la interpretación.
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

    /// <summary>2026-06-03: ahora indica si un item ESTÁ OCULTO para un rol (true = oculto).</summary>
    public bool IsHidden(string role, string key)
    {
        if (_cache is null || string.IsNullOrEmpty(role) || string.IsNullOrEmpty(key)) return false;
        return _cache.TryGetValue(role, out var keys) && keys.Contains(key);
    }

    /// <summary>Devuelve la lista de keys VISIBLES para un rol (todas las del catálogo MENOS las ocultas).
    /// Recibe el catálogo completo de keys disponibles.</summary>
    public IReadOnlyList<string> GetVisibleKeys(string role, IEnumerable<string> allCatalogKeys)
    {
        if (_cache is null || string.IsNullOrEmpty(role)) return allCatalogKeys.ToList();
        if (!_cache.TryGetValue(role, out var hidden)) return allCatalogKeys.ToList();
        return allCatalogKeys.Where(k => !hidden.Contains(k)).ToList();
    }

    /// <summary>Set/unset oculto. Si hidden=true, agrega la fila (= ocultar). Si hidden=false, la borra (= mostrar).</summary>
    public async Task SetHiddenAsync(string role, string key, bool hidden)
    {
        // El endpoint del backend sigue siendo el mismo: "agregar fila" o "borrar fila".
        // Como invertimos la semántica, "hidden=true" -> agregar fila (=enabled true en el viejo API).
        await _api.SetMenuVisibilityAsync(role, key, hidden);
        if (_cache is null) _cache = new(StringComparer.OrdinalIgnoreCase);
        if (!_cache.TryGetValue(role, out var keys))
        {
            keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _cache[role] = keys;
        }
        if (hidden) keys.Add(key);
        else keys.Remove(key);
        OnChange?.Invoke();
    }
}
