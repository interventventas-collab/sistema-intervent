using System.Net.Http.Json;
using System.Text.Json;

namespace Api.Services;

/// <summary>
/// Cliente HTTP que habla con el container `playwright` para correr el test
/// de login + scraping de ARCA. La lógica real del browser vive ahí; acá
/// solo proxiamos las llamadas y exponemos DTOs cómodos.
/// </summary>
public class ArcaScrapingService
{
    private readonly HttpClient _http;
    private readonly ILogger<ArcaScrapingService> _logger;

    public ArcaScrapingService(IConfiguration config, ILogger<ArcaScrapingService> logger)
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

    /// <summary>Inicia el test de login + scraping del RUT. No espera resultado.</summary>
    public Task<(bool ok, string? error)> StartTestAsync(string cuit, string? cuitLogin, string password)
        => StartAsync(new { cuit, cuitLogin, password, action = "test" });

    /// <summary>Inicia el flujo de descarga de comprobantes (Emitidos + Recibidos). No espera resultado.</summary>
    public Task<(bool ok, string? error)> StartComprobantesAsync(
        string cuit, string? cuitLogin, string password, RangoFechasRequest rango)
    {
        return StartAsync(new
        {
            cuit,
            cuitLogin,
            password,
            action = "comprobantes",
            rangoFechas = new
            {
                tipo = string.IsNullOrEmpty(rango.Tipo) ? "30dias" : rango.Tipo,
                desde = rango.Desde,
                hasta = rango.Hasta
            }
        });
    }

    private async Task<(bool ok, string? error)> StartAsync(object body)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/arca/test/start", body);
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
            _logger.LogError(ex, "Error iniciando flujo ARCA");
            return (false, "Servicio Playwright no disponible: " + ex.Message);
        }
    }

    /// <summary>Devuelve el estado actual { running, step, result }.</summary>
    public async Task<ArcaTestStatusDto> GetStatusAsync()
    {
        try
        {
            var resp = await _http.GetAsync("/arca/test/status");
            resp.EnsureSuccessStatusCode();
            var dto = await resp.Content.ReadFromJsonAsync<ArcaTestStatusDto>(new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return dto ?? new ArcaTestStatusDto { Running = false, Step = "—", Result = null };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer status del test ARCA");
            return new ArcaTestStatusDto { Running = false, Step = "Servicio no disponible", Result = null };
        }
    }

    /// <summary>PNG del browser activo, o null si no hay test corriendo / no captura.</summary>
    public async Task<byte[]?> GetScreenshotAsync()
    {
        try
        {
            var resp = await _http.GetAsync("/arca/test/screenshot");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo obtener screenshot del test ARCA");
            return null;
        }
    }
}

// ===== DTOs =====
public class ArcaTestStatusDto
{
    public bool Running { get; set; }
    public string Step { get; set; } = string.Empty;
    public ArcaTestResultDto? Result { get; set; }
}

public class ArcaTestResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    // ----- action = "test" -----
    public string? Titular { get; set; }
    public List<ArcaDomicilioDto>? Domicilios { get; set; }
    public List<ArcaActividadDto>? Actividades { get; set; }
    // ----- action = "comprobantes" -----
    public List<ArcaComprobanteDto>? Emitidos { get; set; }
    public List<ArcaComprobanteDto>? Recibidos { get; set; }
    public string? RangoDesde { get; set; }
    public string? RangoHasta { get; set; }
}

public class ArcaDomicilioDto
{
    public string Tipo { get; set; } = string.Empty;
    public string Direccion { get; set; } = string.Empty;
    public string Jurisdiccion { get; set; } = string.Empty;
}

public class ArcaActividadDto
{
    public string Descripcion { get; set; } = string.Empty;
    public string FechaInicio { get; set; } = string.Empty;
}

public class ArcaComprobanteDto
{
    public string Fecha { get; set; } = string.Empty;
    public string NroDoc { get; set; } = string.Empty;
    public string Denominacion { get; set; } = string.Empty;
    public decimal? ImpNeto { get; set; }
    public decimal? TotalIva { get; set; }
    public decimal? ImpTotal { get; set; }
}

/// <summary>Body de la request de comprobantes — tipo "30dias"/"60dias"/"90dias"/"custom".</summary>
public class RangoFechasRequest
{
    public string Tipo { get; set; } = "30dias";
    public string? Desde { get; set; } // yyyy-MM-dd, solo si Tipo = "custom"
    public string? Hasta { get; set; }
}
