using Microsoft.JSInterop;

namespace Web.Services;

/// <summary>
/// Mantiene cual es la EMPRESA con la que el usuario esta trabajando ahora.
/// Persiste el ID en localStorage.
///
/// Conceptos:
/// - <see cref="Companies"/> son los IDs estables (INTERVENT, INTEREVENTOS, FRIKAF, PALANICA).
///   Los IDs nunca cambian — son los que se guardan en la base (ej. en Brands.Companies).
/// - <see cref="DisplayNames"/> son los nombres visibles que el admin (OSMAR) puede editar
///   desde el engranaje del modal de seleccion de empresa.
/// </summary>
public class CurrentCompanyService
{
    private const string StorageKey = "current_company";
    private readonly IJSRuntime _js;
    private string? _current;
    private Dictionary<string, string> _displayNames = new(StringComparer.OrdinalIgnoreCase);

    public static readonly string[] Companies =
    {
        "INTERVENT", "INTEREVENTOS", "FRIKAF", "PALANICA"
    };

    public string? Current => _current;
    public bool HasCompany => !string.IsNullOrWhiteSpace(_current);

    /// <summary>Mapa ID -> nombre visible. Si una empresa no esta presente, se usa el ID como fallback.</summary>
    public IReadOnlyDictionary<string, string> DisplayNames => _displayNames;

    /// <summary>Devuelve el nombre visible del ID indicado. Si no hay nombre custom, devuelve el ID tal cual.</summary>
    public string GetDisplayName(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        return _displayNames.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : id;
    }

    /// <summary>Nombre visible de la empresa actualmente seleccionada.</summary>
    public string CurrentDisplayName => HasCompany ? GetDisplayName(_current!) : "";

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

    /// <summary>Reemplaza el mapa de display names. Usado por el layout despues de cargarlos del backend.</summary>
    public void SetDisplayNames(Dictionary<string, string> map)
    {
        _displayNames = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        OnChange?.Invoke();
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

    // ===== Helpers de visibilidad por empresa =====

    /// <summary>Parsea un CSV de empresas a una lista normalizada (trim, uppercase, dedupe).</summary>
    public static List<string> ParseCompanies(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return new();
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => s.Trim().ToUpperInvariant())
                  .Where(s => s.Length > 0)
                  .Distinct()
                  .ToList();
    }

    /// <summary>True si el CSV de empresas (vacio o null = sin restriccion) incluye a la empresa indicada.</summary>
    public static bool IsVisibleForCompany(string? companiesCsv, string company)
    {
        var list = ParseCompanies(companiesCsv);
        if (list.Count == 0) return true;
        return list.Contains((company ?? "").ToUpperInvariant());
    }
}
