using System.Net.Http.Headers;
using System.Text.Json;

namespace Api.Services;

/// <summary>
/// Lee el saldo de Mercado Pago usando la API oficial (https://api.mercadopago.com) con el
/// Access Token de la cuenta. Flujo: /users/me (id + nickname + site) -> el saldo de la
/// cuenta. Guarda el resultado en MpAccount. Lo usan el boton manual y el job automatico.
///
/// Sin scraping ni robot: es una llamada HTTP directa, rapida y confiable.
/// </summary>
public class MpSyncService
{
    private readonly MpAccountService _accounts;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MpSyncService> _logger;

    private const string ApiBase = "https://api.mercadopago.com";

    public MpSyncService(MpAccountService accounts, IHttpClientFactory httpFactory, ILogger<MpSyncService> logger)
    {
        _accounts = accounts;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public record SyncResult(bool Ok, decimal? Disponible, decimal? Total, string? Error);

    public async Task<SyncResult> SincronizarAsync()
    {
        var dto = await _accounts.GetAsync();
        if (dto is null || !dto.HasToken)
            return new SyncResult(false, null, null, "No hay Access Token de Mercado Pago cargado");
        var token = await _accounts.GetTokenAsync();
        if (string.IsNullOrEmpty(token))
            return new SyncResult(false, null, null, "No se pudo leer el Access Token");

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1) Datos de la cuenta.
        long? mpUserId = dto.MpUserId;
        string? nickname = dto.Nickname;
        string? siteId = dto.SiteId;
        try
        {
            var meResp = await http.GetAsync($"{ApiBase}/users/me");
            var meBody = await meResp.Content.ReadAsStringAsync();
            if (!meResp.IsSuccessStatusCode)
                return await Fallar(dto, InterpretarError((int)meResp.StatusCode, meBody));

            using var meDoc = JsonDocument.Parse(meBody);
            var root = meDoc.RootElement;
            if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var idVal)) mpUserId = idVal;
            if (root.TryGetProperty("nickname", out var nickEl)) nickname = nickEl.GetString();
            if (root.TryGetProperty("site_id", out var siteEl)) siteId = siteEl.GetString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MP] error leyendo /users/me");
            return await Fallar(dto, "No se pudo conectar con Mercado Pago: " + ex.Message);
        }

        if (mpUserId is null || mpUserId <= 0)
            return await Fallar(dto, "No se pudo identificar la cuenta de Mercado Pago (sin ID de usuario).");

        // 2) Saldo de la cuenta.
        try
        {
            var balResp = await http.GetAsync($"{ApiBase}/users/{mpUserId}/mercadopago_account/balance");
            var balBody = await balResp.Content.ReadAsStringAsync();
            if (!balResp.IsSuccessStatusCode)
                return await Fallar(dto, InterpretarError((int)balResp.StatusCode, balBody), mpUserId, nickname, siteId);

            using var balDoc = JsonDocument.Parse(balBody);
            var root = balDoc.RootElement;
            decimal? disponible = ReadDecimal(root, "available_balance");
            decimal? total = ReadDecimal(root, "total_amount");
            decimal? noDisponible = ReadDecimal(root, "unavailable_balance");
            // Si la API no trae total, lo reconstruimos.
            if (total is null && disponible is not null)
                total = disponible + (noDisponible ?? 0m);

            if (disponible is null && total is null)
                return await Fallar(dto, "Mercado Pago respondio pero no se encontro el saldo en la respuesta.", mpUserId, nickname, siteId);

            await _accounts.GuardarSaldoAsync(disponible, total, true, null, mpUserId, nickname, siteId);
            return new SyncResult(true, disponible, total, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MP] error leyendo saldo");
            return await Fallar(dto, "No se pudo leer el saldo: " + ex.Message, mpUserId, nickname, siteId);
        }
    }

    private static decimal? ReadDecimal(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String && decimal.TryParse(el.GetString(),
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ds)) return ds;
        return null;
    }

    private static string InterpretarError(int status, string body)
    {
        var snippet = string.IsNullOrEmpty(body) ? "" : (body.Length > 300 ? body.Substring(0, 300) : body);
        return status switch
        {
            401 => "El Access Token no es valido o expiro. Revisalo en Mercado Pago Developers y volvelo a pegar.",
            403 => "El Access Token no tiene permiso para leer el saldo de esta cuenta. Verifica que sea el token de produccion de tu cuenta.",
            404 => "Mercado Pago no encontro el saldo para esta cuenta (404). " + snippet,
            _ => $"Mercado Pago respondio error {status}. {snippet}"
        };
    }

    private async Task<SyncResult> Fallar(MpAccountService.MpAccountDto dto, string? error,
        long? mpUserId = null, string? nickname = null, string? siteId = null)
    {
        try { await _accounts.GuardarSaldoAsync(null, null, false, error, mpUserId, nickname, siteId); } catch { }
        return new SyncResult(false, dto.LastSaldoDisponible, dto.LastSaldoTotal, error);
    }
}
