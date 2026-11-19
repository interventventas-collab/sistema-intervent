using System.Net.Http.Json;
using System.Text.Json;

namespace Api.Services;

/// <summary>Habla con el container playwright para el flujo de Shell Flota (login + OTP por mail + saldo).</summary>
public class ShellScrapingService
{
    private readonly HttpClient _http;
    private readonly ILogger<ShellScrapingService> _logger;

    public ShellScrapingService(IConfiguration config, ILogger<ShellScrapingService> logger)
    {
        _logger = logger;
        var baseUrl = Environment.GetEnvironmentVariable("PLAYWRIGHT_URL") ?? config["PlaywrightUrl"] ?? "http://playwright:3001";
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(4) };
    }

    public async Task<(bool ok, string? error)> StartSaldoAsync(string usuario, string password, string gmailUser, string gmailPass)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/shell/saldo/start", new { usuario, password, gmailUser, gmailPass });
            if (resp.IsSuccessStatusCode) return (true, null);
            var content = await resp.Content.ReadAsStringAsync();
            try { using var doc = JsonDocument.Parse(content); if (doc.RootElement.TryGetProperty("error", out var err)) return (false, err.GetString()); } catch { }
            return (false, $"HTTP {(int)resp.StatusCode}: {content}");
        }
        catch (Exception ex) { _logger.LogError(ex, "Error iniciando saldo Shell"); return (false, "Servicio Playwright no disponible: " + ex.Message); }
    }

    public async Task<ShellTestStatusDto> GetStatusAsync()
    {
        try
        {
            var resp = await _http.GetAsync("/shell/test/status");
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<ShellTestStatusDto>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new ShellTestStatusDto { Running = false, Step = "—" };
        }
        catch (Exception ex) { _logger.LogWarning(ex, "No se pudo leer status Shell"); return new ShellTestStatusDto { Running = false, Step = "Servicio no disponible" }; }
    }

    public async Task<byte[]?> GetScreenshotAsync()
    {
        try { var resp = await _http.GetAsync("/shell/test/screenshot"); return resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync() : null; }
        catch { return null; }
    }
}

public class ShellTestStatusDto
{
    public bool Running { get; set; }
    public string Step { get; set; } = string.Empty;
    public ShellTestResultDto? Result { get; set; }
}

public class ShellTestResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public bool? LoggedIn { get; set; }
    public string? Saldo { get; set; }
    /// <summary>18/11/2026: cargas del reporte transaccional (null = no se pudieron leer).</summary>
    public List<ShellCargaLeidaDto>? Cargas { get; set; }
}

public class ShellCargaLeidaDto
{
    public string? Placa { get; set; }
    public string? Tarjeta { get; set; }
    public string? Marca { get; set; }
    public string? Modelo { get; set; }
    public string? Estacion { get; set; }
    public string? Transaccion { get; set; }
    public string? Producto { get; set; }
    public string? Fecha { get; set; }
    public string? Hora { get; set; }
    public decimal? Precio { get; set; }
    public string? Referencia { get; set; }
    public decimal? Odometro { get; set; }
    public string? Conductor { get; set; }
    public decimal? Litros { get; set; }
    public decimal? Importe { get; set; }
}
