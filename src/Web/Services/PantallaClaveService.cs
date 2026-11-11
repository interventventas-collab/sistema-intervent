using System.Net.Http.Json;

namespace Web.Services;

/// <summary>
/// 2026-11-11: pantallas con CLAVE (pedido del dueño). La lista de direcciones protegidas viene de
/// api/pantallas-clave; MainLayout no muestra una de esas pantallas hasta que se pone la clave.
/// Una vez puesta, queda abierta mientras no se recargue la página (o se cierre la pestaña).
/// Protege la pantalla, no los datos de la API que usa por detrás.
/// </summary>
public class PantallaClaveService
{
    private readonly HttpClient _http;
    private HashSet<string> _protegidas = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _abiertas = new(StringComparer.OrdinalIgnoreCase);
    private bool _cargado;

    public event Action? OnChange;

    public PantallaClaveService(HttpClient http) { _http = http; }

    public static string Normalizar(string? href)
    {
        var h = (href ?? "").Trim();
        var q = h.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) h = h[..q];
        return ("/" + h.Trim('/')).ToLowerInvariant();
    }

    public async Task CargarAsync(bool forzar = false)
    {
        if (_cargado && !forzar) return;
        try
        {
            var l = await _http.GetFromJsonAsync<List<string>>("/api/pantallas-clave") ?? new();
            _protegidas = new HashSet<string>(l.Select(Normalizar), StringComparer.OrdinalIgnoreCase);
            _cargado = true;
            OnChange?.Invoke();
        }
        catch { }
    }

    public bool TieneClave(string href) => _protegidas.Contains(Normalizar(href));
    /// <summary>¿Hay que pedir la clave antes de mostrar esta pantalla?</summary>
    public bool Bloqueada(string href) => TieneClave(href) && !_abiertas.Contains(Normalizar(href));

    private record VerifResp(bool Ok, string? Error);

    /// <summary>Devuelve null si la clave es correcta; si no, el motivo.</summary>
    public async Task<string?> AbrirAsync(string href, string clave)
    {
        var r = await _http.PostAsJsonAsync("/api/pantallas-clave/verificar", new { Href = Normalizar(href), Clave = clave });
        if ((int)r.StatusCode == 429) return "Demasiados intentos. Esperá 5 minutos.";
        if (!r.IsSuccessStatusCode) return "No se pudo verificar la clave.";
        var v = await r.Content.ReadFromJsonAsync<VerifResp>();
        if (v?.Ok != true) return "Clave incorrecta.";
        _abiertas.Add(Normalizar(href));
        OnChange?.Invoke();
        return null;
    }

    /// <summary>Pone (clave con texto) o saca (null/vacía) la clave de una pantalla.</summary>
    public async Task PonerAsync(string href, string? clave)
    {
        var r = await _http.PutAsJsonAsync("/api/pantallas-clave", new { Href = Normalizar(href), Clave = clave });
        if (!r.IsSuccessStatusCode)
        {
            var txt = await r.Content.ReadAsStringAsync();
            throw new Exception(txt.Contains("4 caracteres") ? "La clave tiene que tener al menos 4 caracteres" : "No se pudo guardar la clave");
        }
        // Quien la pone ya la sabe: queda abierta para él.
        if (!string.IsNullOrWhiteSpace(clave)) _abiertas.Add(Normalizar(href));
        await CargarAsync(forzar: true);
    }

    /// <summary>Vuelve a cerrar todas (botón "🔒 cerrar" de la pantalla).</summary>
    public void CerrarTodas() { _abiertas.Clear(); OnChange?.Invoke(); }
}
