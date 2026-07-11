using System.Text.Json;
using Api.Models;

namespace Api.Services;

/// <summary>
/// Habla con la API oficial de EZVIZ (EZVIZ Open Platform). Flujo:
///   1) Con appKey + appSecret pide un accessToken (POST /api/lapp/token/get). El token dura
///      ~7 días; lo cacheamos en la cuenta y solo lo renovamos cuando está por vencer.
///   2) Con el token lista las cámaras (POST /api/lapp/camera/list).
///   3) Arma el link "ezopen://" que el reproductor web (EZUIKit) usa para el video en vivo.
///
/// Las respuestas de EZVIZ vienen como { "code":"200", "msg":"...", "data": ... }. code=="200"
/// es éxito; cualquier otro es error (ver InterpretarCodigo).
/// </summary>
public class EzvizService
{
    private readonly EzvizAccountService _accounts;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<EzvizService> _logger;

    public EzvizService(EzvizAccountService accounts, IHttpClientFactory httpFactory, ILogger<EzvizService> logger)
    {
        _accounts = accounts;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public record EzvizCamera(string DeviceSerial, int ChannelNo, string? ChannelName,
        string? DeviceName, int Status, bool IsEncrypt, string? PicUrl);

    public record TokenResult(bool Ok, string? Token, string? AreaDomain, string? Error);

    // ─────────────────────────────────────────────────────────────
    // Token: lo pide/renueva solo. Deja un margen de 1 día antes de vencer.
    // ─────────────────────────────────────────────────────────────
    public async Task<TokenResult> EnsureTokenAsync()
    {
        var a = await _accounts.GetEntityAsync();
        if (a is null || string.IsNullOrEmpty(a.AppKey) || string.IsNullOrEmpty(a.AppSecret))
            return new TokenResult(false, null, null, "No hay appKey/appSecret de EZVIZ cargados");

        // ¿Token cacheado y aún vigente (con margen de 1 día)?
        if (!string.IsNullOrEmpty(a.AccessToken) && a.TokenExpiresAt.HasValue
            && a.TokenExpiresAt.Value > DateTime.UtcNow.AddDays(1))
            return new TokenResult(true, a.AccessToken, a.AreaDomain, null);

        // Pedir uno nuevo.
        var host = string.IsNullOrWhiteSpace(a.ApiHost) ? "https://open.ezvizlife.com" : a.ApiHost.TrimEnd('/');
        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["appKey"] = a.AppKey,
                ["appSecret"] = a.AppSecret
            });
            var resp = await http.PostAsync($"{host}/api/lapp/token/get", form);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
            if (code != "200" || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : null;
                var error = InterpretarCodigo(code, msg);
                await _accounts.MarcarResultadoAsync(false, error);
                return new TokenResult(false, null, null, error);
            }

            var token = data.TryGetProperty("accessToken", out var tEl) ? tEl.GetString() : null;
            var areaDomain = data.TryGetProperty("areaDomain", out var adEl) ? adEl.GetString() : null;
            // expireTime viene como epoch en milisegundos.
            DateTime expiresAt = DateTime.UtcNow.AddDays(6);
            if (data.TryGetProperty("expireTime", out var exEl) && exEl.TryGetInt64(out var ms) && ms > 0)
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

            if (string.IsNullOrEmpty(token))
            {
                await _accounts.MarcarResultadoAsync(false, "EZVIZ no devolvió un token válido.");
                return new TokenResult(false, null, null, "EZVIZ no devolvió un token válido.");
            }

            await _accounts.GuardarTokenAsync(token, expiresAt, areaDomain);
            await _accounts.MarcarResultadoAsync(true, null);
            return new TokenResult(true, token, areaDomain, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EZVIZ] error pidiendo token");
            var error = "No se pudo conectar con EZVIZ: " + ex.Message;
            await _accounts.MarcarResultadoAsync(false, error);
            return new TokenResult(false, null, null, error);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Lista de cámaras (con nombre, estado online/offline y foto de vista previa).
    // ─────────────────────────────────────────────────────────────
    public async Task<(bool ok, string? error, List<EzvizCamera> camaras)> ListarCamarasAsync()
    {
        var tk = await EnsureTokenAsync();
        if (!tk.Ok || string.IsNullOrEmpty(tk.Token))
            return (false, tk.Error ?? "No se pudo autenticar con EZVIZ", new());

        var baseHost = await GetDataHostAsync(tk.AreaDomain);
        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["accessToken"] = tk.Token,
                ["pageStart"] = "0",
                ["pageSize"] = "50"
            });
            var resp = await http.PostAsync($"{baseHost}/api/lapp/camera/list", form);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
            if (code != "200")
            {
                var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : null;
                var error = InterpretarCodigo(code, msg);
                await _accounts.MarcarResultadoAsync(false, error);
                return (false, error, new());
            }

            var camaras = new List<EzvizCamera>();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in data.EnumerateArray())
                {
                    var serial = c.TryGetProperty("deviceSerial", out var s) ? s.GetString() : null;
                    if (string.IsNullOrEmpty(serial)) continue;
                    var channel = c.TryGetProperty("channelNo", out var ch) && ch.TryGetInt32(out var chVal) ? chVal : 1;
                    var channelName = c.TryGetProperty("channelName", out var cn) ? cn.GetString() : null;
                    var deviceName = c.TryGetProperty("deviceName", out var dn) ? dn.GetString() : null;
                    var status = c.TryGetProperty("status", out var st) && st.TryGetInt32(out var stVal) ? stVal : 0;
                    var isEncrypt = c.TryGetProperty("isEncrypt", out var ie) && ie.TryGetInt32(out var ieVal) && ieVal == 1;
                    var picUrl = c.TryGetProperty("picUrl", out var pu) ? pu.GetString() : null;
                    camaras.Add(new EzvizCamera(serial, channel, channelName, deviceName, status, isEncrypt, picUrl));
                }
            }
            await _accounts.MarcarResultadoAsync(true, null);
            return (true, null, camaras);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EZVIZ] error listando cámaras");
            var error = "No se pudo leer la lista de cámaras: " + ex.Message;
            await _accounts.MarcarResultadoAsync(false, error);
            return (false, error, new());
        }
    }

    public record LiveResult(bool Ok, string? Url, string? AccessToken, string? Error);

    // ─────────────────────────────────────────────────────────────
    // Datos para el reproductor web (EZUIKit): el accessToken + el link ezopen:// de la cámara.
    // Para cámaras con clave de encriptación, el código de verificación se agrega en el front.
    // ─────────────────────────────────────────────────────────────
    public async Task<LiveResult> GetLiveAsync(string deviceSerial, int channelNo)
    {
        if (string.IsNullOrWhiteSpace(deviceSerial))
            return new LiveResult(false, null, null, "Falta el número de serie de la cámara");
        var tk = await EnsureTokenAsync();
        if (!tk.Ok || string.IsNullOrEmpty(tk.Token))
            return new LiveResult(false, null, null, tk.Error ?? "No se pudo autenticar con EZVIZ");

        // Link estándar de EZVIZ para el reproductor web. El host "open.ys7.com" es el que
        // espera EZUIKit para el esquema ezopen://.
        var url = $"ezopen://open.ys7.com/{deviceSerial.Trim()}/{channelNo}.live";
        return new LiveResult(true, url, tk.Token, null);
    }

    // Para las llamadas de datos, EZVIZ pide usar el "areaDomain" que devuelve junto al token.
    private async Task<string> GetDataHostAsync(string? areaDomainFromToken)
    {
        if (!string.IsNullOrWhiteSpace(areaDomainFromToken))
            return areaDomainFromToken.TrimEnd('/');
        var a = await _accounts.GetEntityAsync();
        if (a is not null && !string.IsNullOrWhiteSpace(a.AreaDomain))
            return a.AreaDomain.TrimEnd('/');
        return string.IsNullOrWhiteSpace(a?.ApiHost) ? "https://open.ezvizlife.com" : a.ApiHost.TrimEnd('/');
    }

    private static string InterpretarCodigo(string? code, string? msg)
    {
        var extra = string.IsNullOrWhiteSpace(msg) ? "" : $" ({msg})";
        return code switch
        {
            "10001" => "Los datos de EZVIZ son inválidos (revisá el appKey y el appSecret)." + extra,
            "10002" => "El token de EZVIZ venció o es inválido. Se reintenta solo; si sigue, volvé a probar la conexión." + extra,
            "10005" => "El appKey de EZVIZ está deshabilitado o vencido. Revisalo en la web de desarrolladores." + extra,
            "10017" => "El appKey de EZVIZ no existe. Revisá que esté bien copiado." + extra,
            "10030" => "El appKey y el appSecret no coinciden, o son de otra región. Revisalos." + extra,
            "20002" => "La cámara no existe en esta cuenta de EZVIZ." + extra,
            "20006" => "Problema de red con EZVIZ. Probá de nuevo en un momento." + extra,
            "20007" => "La cámara está offline (apagada o sin internet)." + extra,
            "60016" => "La cámara tiene la imagen encriptada: hace falta el código de verificación." + extra,
            _ => $"EZVIZ respondió un error (código {code}).{extra}"
        };
    }
}
