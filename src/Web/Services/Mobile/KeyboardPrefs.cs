using Microsoft.JSInterop;

namespace Web.Services.Mobile;

/// <summary>
/// Preferencias del operario para los componentes móviles (teclado, favoritos, prefijos fijados).
/// Persiste en localStorage. Singleton, así es consistente en toda la app.
/// Default: layout ABC, 7 prefijos rápidos predefinidos.
/// </summary>
public class KeyboardPrefs
{
    private readonly IJSRuntime _js;
    private const string KEY_LAYOUT = "mobile.kbd.layout";       // "abc" | "qwerty"
    private const string KEY_FAVS = "mobile.search.favoritos";   // JSON: lista de SKUs
    private const string KEY_USOS = "mobile.search.usos";        // JSON: dict SKU → cantUsos
    private const string KEY_PREFIJOS = "mobile.kbd.prefijos";   // JSON: lista de prefijos custom
    private const string KEY_USOS_PREFIJOS = "mobile.kbd.prefijos.usos"; // JSON: dict prefijo → cantUsos

    public string Layout { get; private set; } = "abc";  // default ABC alfabético (decidido 2026-05-26)
    private HashSet<string> _favoritos = new();
    private Dictionary<string, int> _usos = new();
    private Dictionary<string, int> _usosPrefijos = new();
    private List<string>? _prefijosCustom;

    // Default: top 7 prefijos del catálogo (analizado 2026-05-26). El orden inicial
    // viene de las estadísticas, pero se reordena en runtime según uso del operario.
    public static readonly List<string> PrefijosDefault = new() { "C", "M", "FR", "D", "V", "F", "HE" };

    public KeyboardPrefs(IJSRuntime js) { _js = js; }

    /// <summary>
    /// Devuelve los prefijos ordenados según uso (los más tocados primero).
    /// Si el operario nunca tocó nada, mantiene el orden default del catálogo.
    /// </summary>
    public List<string> Prefijos
    {
        get
        {
            var baseList = _prefijosCustom ?? PrefijosDefault;
            // Ordenar por uso DESC, los empatados conservan el orden original
            return baseList
                .Select((p, idx) => new { Prefijo = p, Uso = _usosPrefijos.TryGetValue(p, out var u) ? u : 0, OrigIdx = idx })
                .OrderByDescending(x => x.Uso)
                .ThenBy(x => x.OrigIdx)
                .Select(x => x.Prefijo)
                .ToList();
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            var layout = await _js.InvokeAsync<string?>("localStorage.getItem", KEY_LAYOUT);
            if (!string.IsNullOrEmpty(layout)) Layout = layout;

            var favsJson = await _js.InvokeAsync<string?>("localStorage.getItem", KEY_FAVS);
            if (!string.IsNullOrEmpty(favsJson))
                _favoritos = System.Text.Json.JsonSerializer.Deserialize<HashSet<string>>(favsJson) ?? new();

            var usosJson = await _js.InvokeAsync<string?>("localStorage.getItem", KEY_USOS);
            if (!string.IsNullOrEmpty(usosJson))
                _usos = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(usosJson) ?? new();

            var prefsJson = await _js.InvokeAsync<string?>("localStorage.getItem", KEY_PREFIJOS);
            if (!string.IsNullOrEmpty(prefsJson))
                _prefijosCustom = System.Text.Json.JsonSerializer.Deserialize<List<string>>(prefsJson);

            var usosPrefsJson = await _js.InvokeAsync<string?>("localStorage.getItem", KEY_USOS_PREFIJOS);
            if (!string.IsNullOrEmpty(usosPrefsJson))
                _usosPrefijos = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(usosPrefsJson) ?? new();
        }
        catch { /* primera carga: sin nada */ }
    }

    /// <summary>Registra que el operario tocó un prefijo (para reordenar la fila).</summary>
    public async Task RegistrarUsoPrefijoAsync(string prefijo)
    {
        if (string.IsNullOrEmpty(prefijo)) return;
        _usosPrefijos[prefijo] = (_usosPrefijos.TryGetValue(prefijo, out var n) ? n : 0) + 1;
        var json = System.Text.Json.JsonSerializer.Serialize(_usosPrefijos);
        await _js.InvokeVoidAsync("localStorage.setItem", KEY_USOS_PREFIJOS, json);
    }

    public async Task SetLayoutAsync(string layout)
    {
        Layout = layout;
        await _js.InvokeVoidAsync("localStorage.setItem", KEY_LAYOUT, layout);
    }

    public bool IsFavorito(string sku) => _favoritos.Contains(sku);

    public async Task ToggleFavoritoAsync(string sku)
    {
        if (_favoritos.Contains(sku)) _favoritos.Remove(sku);
        else _favoritos.Add(sku);
        var json = System.Text.Json.JsonSerializer.Serialize(_favoritos);
        await _js.InvokeVoidAsync("localStorage.setItem", KEY_FAVS, json);
    }

    public List<string> GetFavoritos() => _favoritos.ToList();

    public int GetUsosFrecuentes(string sku) => _usos.TryGetValue(sku, out var n) ? n : 0;

    public async Task RegistrarUsoAsync(string sku)
    {
        if (string.IsNullOrEmpty(sku)) return;
        _usos[sku] = (_usos.TryGetValue(sku, out var n) ? n : 0) + 1;
        var json = System.Text.Json.JsonSerializer.Serialize(_usos);
        await _js.InvokeVoidAsync("localStorage.setItem", KEY_USOS, json);
    }

    public List<string> GetTopUsados(int n = 10)
        => _usos.OrderByDescending(kv => kv.Value).Take(n).Select(kv => kv.Key).ToList();
}
