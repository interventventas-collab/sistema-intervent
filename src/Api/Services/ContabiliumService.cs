using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Api.Services;

/// <summary>
/// Cliente para la API REST de Contabilium.
///
/// Auth (OAuth2 client_credentials):
///   POST https://rest.contabilium.com/token
///     grant_type=client_credentials
///     client_id=<email>
///     client_secret=<apiKey>
///   → { access_token, expires_in: 86400 }
///
/// El token se cachea en ContabiliumAccounts.AccessToken hasta que vence.
///
/// Endpoints usados:
///   GET /api/conceptos/search?pageSize=N&page=N → lista paginada de productos
///   GET /api/conceptos/{id}                     → detalle con Items[] (componentes si es Combo)
///   GET /api/comprobantes/search?fechaDesde=&fechaHasta= → ventas
/// </summary>
public class ContabiliumService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ContabiliumService> _logger;

    private const string BASE_URL = "https://rest.contabilium.com";

    public ContabiliumService(AppDbContext db, IHttpClientFactory httpFactory, ILogger<ContabiliumService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>Devuelve la cuenta unica (Id=1) si esta cargada.</summary>
    public Task<ContabiliumAccount?> GetAccountAsync()
        => _db.ContabiliumAccounts.FirstOrDefaultAsync();

    /// <summary>Crea o actualiza la cuenta con email + apikey. Tambien valida la conexion contra Contabilium.</summary>
    public async Task<(bool ok, string? error)> ConnectAsync(string email, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(apiKey))
            return (false, "Email y API Key son obligatorios.");

        // Probar autenticacion antes de guardar
        var (token, expiresIn, err) = await RequestTokenAsync(email.Trim(), apiKey.Trim());
        if (token is null) return (false, err ?? "No se pudo autenticar.");

        var acc = await _db.ContabiliumAccounts.FirstOrDefaultAsync();
        if (acc is null)
        {
            acc = new ContabiliumAccount { Email = email.Trim(), ApiKey = apiKey.Trim() };
            _db.ContabiliumAccounts.Add(acc);
        }
        else
        {
            acc.Email = email.Trim();
            acc.ApiKey = apiKey.Trim();
            acc.UpdatedAt = DateTime.UtcNow;
        }
        acc.AccessToken = token;
        acc.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn - 60); // 60s de margen
        await _db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>Obtiene un token valido: usa el cacheado si no vencio, sino pide uno nuevo.</summary>
    public async Task<string?> GetTokenAsync(ContabiliumAccount? acc = null)
    {
        acc ??= await GetAccountAsync();
        if (acc is null) return null;

        if (!string.IsNullOrEmpty(acc.AccessToken) && acc.AccessTokenExpiresAt.HasValue
            && acc.AccessTokenExpiresAt.Value > DateTime.UtcNow)
        {
            return acc.AccessToken;
        }

        var (token, expiresIn, err) = await RequestTokenAsync(acc.Email, acc.ApiKey);
        if (token is null)
        {
            _logger.LogWarning("Contabilium token refresh fallo: {Err}", err);
            return null;
        }
        acc.AccessToken = token;
        acc.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn - 60);
        acc.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return token;
    }

    private async Task<(string? token, int expiresIn, string? err)> RequestTokenAsync(string email, string apiKey)
    {
        try
        {
            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = email,
                ["client_secret"] = apiKey
            });
            var resp = await http.PostAsync($"{BASE_URL}/token", body);
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return (null, 0, $"{(int)resp.StatusCode}: {text}");
            using var doc = JsonDocument.Parse(text);
            var token = doc.RootElement.GetProperty("access_token").GetString();
            var expires = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 86400;
            return (token, expires, null);
        }
        catch (Exception ex)
        {
            return (null, 0, ex.Message);
        }
    }

    // ════════ DTOs ════════
    public record ConceptoDto(int Id, string? Codigo, string? Nombre, string? Tipo,
        decimal? Stock, decimal? PrecioFinal, decimal? CostoInterno, string? Estado,
        List<ConceptoComponenteDto>? Items);
    public record ConceptoComponenteDto(int Id, string? Codigo, decimal Cantidad);
    public record ConceptosPageDto(List<ConceptoDto> Items, int TotalItems);

    /// <summary>Lista productos paginada. pageSize hasta 100 segun la API.</summary>
    public async Task<ConceptosPageDto?> ListConceptosAsync(int page = 1, int pageSize = 50)
    {
        var token = await GetTokenAsync();
        if (token is null) return null;
        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.GetAsync($"{BASE_URL}/api/conceptos/search?pageSize={pageSize}&page={page}");
        if (!resp.IsSuccessStatusCode) return null;
        var text = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ConceptosPageDto>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    /// <summary>Detalle de un producto, incluyendo Items[] con componentes si es Combo.</summary>
    public async Task<ConceptoDto?> GetConceptoAsync(int id)
    {
        var token = await GetTokenAsync();
        if (token is null) return null;
        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await http.GetAsync($"{BASE_URL}/api/conceptos/{id}");
        if (!resp.IsSuccessStatusCode) return null;
        var text = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ConceptoDto>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}
