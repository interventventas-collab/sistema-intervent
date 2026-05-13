using System.Net.Http.Json;

namespace Web.Services;

/// <summary>
/// Lookup de codigos postales argentinos (CP4 → localidad + provincia).
/// Carga el dataset estatico /data/cps-ar.json (~125 KB, ~2480 CPs) una sola vez por sesion.
/// </summary>
public class CpService
{
    private readonly HttpClient _http;
    private Dictionary<string, CpEntry>? _data;
    private Task<Dictionary<string, CpEntry>>? _loadTask;

    public CpService(HttpClient http) { _http = http; }

    private Task<Dictionary<string, CpEntry>> LoadAsync()
    {
        if (_data is not null) return Task.FromResult(_data);
        return _loadTask ??= LoadInternalAsync();
    }

    private async Task<Dictionary<string, CpEntry>> LoadInternalAsync()
    {
        try
        {
            var d = await _http.GetFromJsonAsync<Dictionary<string, CpEntry>>("data/cps-ar.json")
                    ?? new Dictionary<string, CpEntry>();
            _data = d;
            return d;
        }
        catch
        {
            _data = new();
            return _data;
        }
    }

    /// <summary>Busca un CP exacto (4 digitos). Devuelve null si no existe.</summary>
    public async Task<CpEntry?> LookupAsync(string? cp)
    {
        if (string.IsNullOrWhiteSpace(cp)) return null;
        var key = cp.Trim();
        if (key.Length != 4 || !key.All(char.IsDigit)) return null;
        var d = await LoadAsync();
        return d.TryGetValue(key, out var e) ? e : null;
    }

    private List<string>? _provinciasCache;
    private Dictionary<string, List<string>>? _localidadesCache;

    /// <summary>Devuelve las 24 jurisdicciones argentinas (provincias + CABA), ordenadas alfabeticamente.</summary>
    public async Task<List<string>> GetProvinciasAsync()
    {
        if (_provinciasCache is not null) return _provinciasCache;
        var d = await LoadAsync();
        _provinciasCache = d.Values
            .Select(v => v.p)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct()
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return _provinciasCache;
    }

    /// <summary>Devuelve todas las combinaciones "Localidad, Provincia" del dataset, ordenadas.
    /// Util para campos sueltos que mezclan ambos (ej: "Direccion fiscal del negocio").</summary>
    public async Task<List<string>> GetLocalidadConProvinciaAsync()
    {
        var d = await LoadAsync();
        return d.Values
            .Select(v => string.IsNullOrEmpty(v.p) ? v.l : $"{v.l}, {v.p}")
            .Distinct()
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Devuelve las localidades unicas de una provincia, ordenadas alfabeticamente.
    /// Si la provincia no matchea con ninguna conocida, devuelve lista vacia.</summary>
    public async Task<List<string>> GetLocalidadesAsync(string? provincia)
    {
        if (string.IsNullOrWhiteSpace(provincia)) return new List<string>();
        if (_localidadesCache is null)
        {
            var d = await LoadAsync();
            _localidadesCache = d.Values
                .GroupBy(v => v.p)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => x.l)
                          .Where(l => !string.IsNullOrEmpty(l))
                          .Distinct()
                          .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
                          .ToList(),
                    StringComparer.OrdinalIgnoreCase);
        }
        return _localidadesCache.TryGetValue(provincia.Trim(), out var list)
            ? list
            : new List<string>();
    }
}

public class CpEntry
{
    public string l { get; set; } = "";
    public string p { get; set; } = "";
}
