using System.Net.Http.Json;
using System.Text.Json;

namespace Api.Services;

/// <summary>
/// Cliente HTTP que habla con el container `playwright` para correr el login
/// (y más adelante la descarga de movimientos) del Office Banking de Galicia.
/// La lógica del browser vive allá; acá solo proxiamos.
/// </summary>
public class GaliciaScrapingService
{
    private readonly HttpClient _http;
    private readonly ILogger<GaliciaScrapingService> _logger;

    public GaliciaScrapingService(IConfiguration config, ILogger<GaliciaScrapingService> logger)
    {
        _logger = logger;
        var baseUrl = Environment.GetEnvironmentVariable("PLAYWRIGHT_URL")
                      ?? config["PlaywrightUrl"]
                      ?? "http://playwright:3001";
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMinutes(3)
        };
    }

    /// <summary>
    /// Inicia la prueba de login. submit=false solo abre el formulario y saca foto
    /// SIN enviar (para verificar sin arriesgar el bloqueo por intentos).
    /// No espera el resultado — el cliente pollea GetStatusAsync.
    /// </summary>
    public async Task<(bool ok, string? error)> StartLoginTestAsync(string usuario, string? password, bool submit)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/galicia/test/start", new { usuario, password, submit });
            if (resp.IsSuccessStatusCode) return (true, null);

            var content = await resp.Content.ReadAsStringAsync();
            try
            {
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("error", out var err))
                    return (false, err.GetString());
            }
            catch { }
            return (false, $"HTTP {(int)resp.StatusCode}: {content}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error iniciando login Galicia");
            return (false, "Servicio Playwright no disponible: " + ex.Message);
        }
    }

    /// <summary>Inicia login + descarga de movimientos (CSV). El CSV vuelve en el status → result.csvBase64.</summary>
    public async Task<(bool ok, string? error)> StartMovimientosAsync(string usuario, string password)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/galicia/movimientos/start", new { usuario, password });
            if (resp.IsSuccessStatusCode) return (true, null);

            var content = await resp.Content.ReadAsStringAsync();
            try
            {
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("error", out var err))
                    return (false, err.GetString());
            }
            catch { }
            return (false, $"HTTP {(int)resp.StatusCode}: {content}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error iniciando descarga movimientos Galicia");
            return (false, "Servicio Playwright no disponible: " + ex.Message);
        }
    }

    /// <summary>Inicia login + descarga de los 3 listados de cheques (.XLS). Vuelven en el status →
    /// result.ChequesRecibidosB64 / ChequesEmitidosB64 / ChequesEndosadosB64.</summary>
    public async Task<(bool ok, string? error)> StartChequesAsync(string usuario, string password)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/galicia/cheques/start", new { usuario, password });
            if (resp.IsSuccessStatusCode) return (true, null);

            var content = await resp.Content.ReadAsStringAsync();
            try
            {
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("error", out var err))
                    return (false, err.GetString());
            }
            catch { }
            return (false, $"HTTP {(int)resp.StatusCode}: {content}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error iniciando descarga cheques Galicia");
            return (false, "Servicio Playwright no disponible: " + ex.Message);
        }
    }

    public async Task<GaliciaTestStatusDto> GetStatusAsync()
    {
        try
        {
            var resp = await _http.GetAsync("/galicia/test/status");
            resp.EnsureSuccessStatusCode();
            var dto = await resp.Content.ReadFromJsonAsync<GaliciaTestStatusDto>(new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return dto ?? new GaliciaTestStatusDto { Running = false, Step = "—", Result = null };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer status del login Galicia");
            return new GaliciaTestStatusDto { Running = false, Step = "Servicio no disponible", Result = null };
        }
    }

    public async Task<byte[]?> GetScreenshotAsync()
    {
        try
        {
            var resp = await _http.GetAsync("/galicia/test/screenshot");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo obtener screenshot del login Galicia");
            return null;
        }
    }
}

// ===== DTOs =====
public class GaliciaTestStatusDto
{
    public bool Running { get; set; }
    public string Step { get; set; } = string.Empty;
    public GaliciaTestResultDto? Result { get; set; }
}

public class GaliciaTestResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public bool? Submitted { get; set; }
    public bool? LoggedIn { get; set; }
    public bool? NeedsToken { get; set; }
    public string? Url { get; set; }
    public string? CsvBase64 { get; set; }
    // Cheques: los 3 listados en base64 (.XLS) + errores parciales por tipo.
    public string? ChequesRecibidosB64 { get; set; }
    public string? ChequesEmitidosB64 { get; set; }
    public string? ChequesEndosadosB64 { get; set; }
    public List<string>? ChequesErrores { get; set; }
}
