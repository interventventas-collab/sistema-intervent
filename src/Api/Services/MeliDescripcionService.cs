using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-09-21 — Descripción de una publicación, para la pantalla nueva (botón 📝 de la fila).
///
/// Osmar: "¿tengo manera de ver la descripción escrita acá?" — y eligió poder verla Y cambiarla
/// ahí mismo, como las fotos. Se lee en vivo de MeLi (no la guardamos en la base) y se guarda
/// con PUT /items/{id}/description; si la publicación nunca tuvo descripción, MeLi pide POST.
/// MeLi sólo acepta texto plano: nada de HTML, y la descripción es una sola para todas las variantes.
/// </summary>
public class MeliDescripcionService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly ILogger<MeliDescripcionService> _logger;

    public MeliDescripcionService(AppDbContext db, IHttpClientFactory httpFactory,
        MeliAccountService accountService, ILogger<MeliDescripcionService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _accountService = accountService;
        _logger = logger;
    }

    public record DescripcionDto(string MeliItemId, string Texto, bool TieneDescripcion, string? Aviso);
    public record GuardarRequest(string? Texto);
    public record ResultadoDto(bool Ok, string Mensaje, DescripcionDto? Descripcion);

    /// <summary>Lee la descripción en vivo de MercadoLibre. Null si la publicación no existe en la base.</summary>
    public async Task<DescripcionDto?> LeerAsync(string meliItemId, CancellationToken ct = default)
    {
        var http = await ClienteAsync(meliItemId, ct);
        if (http is null) return null;
        if (http.Value.error is not null)
            return new DescripcionDto(meliItemId, "", false, http.Value.error);

        var (texto, existe, error) = await LeerDeMeliAsync(http.Value.client!, meliItemId, ct);
        return new DescripcionDto(meliItemId, texto, existe, error);
    }

    /// <summary>TOCA MELI: reemplaza la descripción entera por este texto.</summary>
    public async Task<ResultadoDto> GuardarAsync(string meliItemId, string? texto, CancellationToken ct = default)
    {
        texto = (texto ?? "").Replace("\r\n", "\n").Trim();
        if (texto.Length == 0)
            return new ResultadoDto(false, "La descripción no puede quedar vacía.", null);
        if (texto.Length > 50000)
            return new ResultadoDto(false, $"La descripción es muy larga ({texto.Length:N0} letras). MercadoLibre acepta hasta 50.000.", null);

        var http = await ClienteAsync(meliItemId, ct);
        if (http is null) return new ResultadoDto(false, "Publicación no encontrada.", null);
        if (http.Value.error is not null) return new ResultadoDto(false, http.Value.error, null);
        var client = http.Value.client!;

        // Si nunca tuvo descripción, MeLi no deja hacer PUT: hay que crearla con POST.
        var (_, existe, errorLectura) = await LeerDeMeliAsync(client, meliItemId, ct);
        if (errorLectura is not null) return new ResultadoDto(false, errorLectura, null);

        var body = new StringContent(JsonSerializer.Serialize(new { plain_text = texto }), Encoding.UTF8, "application/json");
        HttpResponseMessage resp;
        try
        {
            resp = existe
                ? await client.PutAsync($"https://api.mercadolibre.com/items/{meliItemId}/description?api_version=2", body, ct)
                : await client.PostAsync($"https://api.mercadolibre.com/items/{meliItemId}/description", body, ct);
        }
        catch (Exception ex)
        {
            return new ResultadoDto(false, "No pude hablar con MercadoLibre: " + ex.Message, null);
        }

        if (!resp.IsSuccessStatusCode)
        {
            var raw = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Descripcion {Mla}: MeLi respondio {Status} {Body}", meliItemId, (int)resp.StatusCode, raw);
            return new ResultadoDto(false, $"MercadoLibre no aceptó la descripción: {MensajeDeMeli(raw) ?? $"error {(int)resp.StatusCode}"}", null);
        }

        // Releer para mostrar lo que quedó de verdad (MeLi a veces limpia caracteres).
        var (textoFinal, _, _) = await LeerDeMeliAsync(client, meliItemId, ct);
        return new ResultadoDto(true, "Descripción guardada en MercadoLibre.",
            new DescripcionDto(meliItemId, textoFinal, true, null));
    }

    private async Task<(HttpClient? client, string? error)?> ClienteAsync(string meliItemId, CancellationToken ct)
    {
        var item = await _db.MeliItems.AsNoTracking().Include(i => i.MeliAccount)
            .Where(i => i.MeliItemId == meliItemId)
            .OrderBy(i => i.VariationId == null ? 0 : 1)
            .FirstOrDefaultAsync(ct);
        if (item?.MeliAccount is null) return null;

        var token = await _accountService.GetValidTokenAsync(item.MeliAccount);
        if (string.IsNullOrWhiteSpace(token))
            return (null, "No hay token de MercadoLibre: reconectá la cuenta en Integraciones.");

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.Timeout = TimeSpan.FromSeconds(30);
        return (http, null);
    }

    private static async Task<(string texto, bool existe, string? error)> LeerDeMeliAsync(
        HttpClient http, string meliItemId, CancellationToken ct)
    {
        try
        {
            var resp = await http.GetAsync($"https://api.mercadolibre.com/items/{meliItemId}/description", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return ("", false, null);
            if (!resp.IsSuccessStatusCode)
                return ("", false, $"MercadoLibre no devolvió la descripción (error {(int)resp.StatusCode}).");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var texto = doc.RootElement.TryGetProperty("plain_text", out var pt) ? pt.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(texto) && doc.RootElement.TryGetProperty("text", out var t))
                texto = t.GetString() ?? "";
            return (texto, true, null);
        }
        catch (Exception ex)
        {
            return ("", false, "No pude leer la descripción de MercadoLibre: " + ex.Message);
        }
    }

    private static string? MensajeDeMeli(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("cause", out var causa) && causa.ValueKind == JsonValueKind.Array)
                foreach (var c in causa.EnumerateArray())
                    if (c.TryGetProperty("message", out var m) && !string.IsNullOrWhiteSpace(m.GetString()))
                        return m.GetString();
            if (doc.RootElement.TryGetProperty("message", out var msg)) return msg.GetString();
        }
        catch { }
        return null;
    }
}
