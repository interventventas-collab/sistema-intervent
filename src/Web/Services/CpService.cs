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
}

public class CpEntry
{
    public string l { get; set; } = "";
    public string p { get; set; } = "";
}
