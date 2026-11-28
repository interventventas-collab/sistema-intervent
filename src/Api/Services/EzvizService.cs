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
        var tk = await UserTokenAsync();
        if (!tk.Ok || string.IsNullOrEmpty(tk.Token))
            return (false, tk.Error ?? "No se pudo autenticar con EZVIZ", new());

        var baseHost = tk.AreaDomain!;
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

    public record LiveResult(bool Ok, string? Url, string? Error);

    // ─────────────────────────────────────────────────────────────
    // Token de USUARIO (2026-11-28). Las cámaras están en la cuenta de la app de Osmar: el token
    // de appKey ve 0 cámaras. El de usuario sale del link de "Authorization Succeeded" que se
    // pega en la pantalla. Dura ~7 días; cuando vence hay que volver a autorizar.
    // ─────────────────────────────────────────────────────────────
    public async Task<TokenResult> UserTokenAsync()
    {
        var a = await _accounts.GetEntityAsync();
        if (a is null)
            return new TokenResult(false, null, null, "No hay cuenta de EZVIZ cargada");
        if (string.IsNullOrEmpty(a.UserAccessToken))
            return new TokenResult(false, null, null, "Falta autorizar las cámaras (botón \"Dar permiso\").");
        if (a.UserTokenExpiresAt is null || a.UserTokenExpiresAt <= DateTime.UtcNow)
            return new TokenResult(false, null, null, "El permiso para ver las cámaras venció: volvé a autorizar (botón \"Dar permiso\").");
        var host = string.IsNullOrWhiteSpace(a.UserAreaDomain)
            ? await GetDataHostAsync(null) : a.UserAreaDomain.TrimEnd('/');
        return new TokenResult(true, a.UserAccessToken, host, null);
    }

    /// <summary>Lee el link de "Authorization Succeeded" (…#access_token=…&amp;expires_in=…&amp;areaDomain=…),
    /// prueba el token listando cámaras y, si anda, lo guarda.</summary>
    public async Task<(bool ok, string? error, int camaras)> AutorizarDesdeLinkAsync(string? link)
    {
        if (string.IsNullOrWhiteSpace(link))
            return (false, "Pegá el link completo de la página \"Authorization Succeeded\".", 0);
        var txt = link.Trim();
        var k = txt.IndexOfAny(new[] { '#', '?' });
        var qs = k >= 0 ? txt[(k + 1)..] : txt;
        var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in qs.Split('&', '#', '?'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            p[part[..eq]] = Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        if (!p.TryGetValue("access_token", out var token) || string.IsNullOrWhiteSpace(token))
            return (false, "El link no trae el permiso (access_token). Copiá la dirección entera de la pestaña \"Authorization Succeeded\".", 0);

        // expires_in viene como fecha en milisegundos (ej 1796490028343). Por las dudas, si viene
        // chico lo tomamos como segundos desde ahora.
        var expiresAt = DateTime.UtcNow.AddDays(6);
        if (p.TryGetValue("expires_in", out var exStr) && long.TryParse(exStr, out var ex) && ex > 0)
            expiresAt = ex > 1_000_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(ex).UtcDateTime
                : DateTime.UtcNow.AddSeconds(ex);
        if (expiresAt <= DateTime.UtcNow)
            return (false, "Ese permiso ya está vencido. Volvé a autorizar y pegá el link nuevo.", 0);

        p.TryGetValue("areaDomain", out var area);
        var host = string.IsNullOrWhiteSpace(area) ? await GetDataHostAsync(null) : area.Trim().TrimEnd('/');
        if (!Uri.TryCreate(host, UriKind.Absolute, out var hu) || hu.Scheme != "https"
            || !(hu.Host.EndsWith(".ezvizlife.com", StringComparison.OrdinalIgnoreCase) || hu.Host.EndsWith(".ezviz.com", StringComparison.OrdinalIgnoreCase)))
            return (false, "El link no parece de EZVIZ.", 0);

        // Probar antes de guardar.
        var (code, msg, data) = await PostAsync(host, "/api/lapp/camera/list", new()
        {
            ["accessToken"] = token.Trim(), ["pageStart"] = "0", ["pageSize"] = "50"
        });
        if (code != "200")
            return (false, InterpretarCodigo(code, msg), 0);
        var n = data.HasValue && data.Value.ValueKind == JsonValueKind.Array ? data.Value.GetArrayLength() : 0;

        await _accounts.GuardarTokenUsuarioAsync(token.Trim(), expiresAt, host);
        await _accounts.MarcarResultadoAsync(true, null);
        return (true, null, n);
    }

    /// <summary>Saca una foto en el momento (anda también con las encriptadas, sin código).</summary>
    public async Task<(bool ok, string? error, string? picUrl)> CapturarAsync(string deviceSerial, int channelNo)
    {
        var tk = await UserTokenAsync();
        if (!tk.Ok) return (false, tk.Error, null);
        var (code, msg, data) = await PostAsync(tk.AreaDomain!, "/api/lapp/device/capture", new()
        {
            ["accessToken"] = tk.Token!, ["deviceSerial"] = deviceSerial.Trim(), ["channelNo"] = channelNo.ToString()
        });
        if (code != "200") return (false, InterpretarCodigo(code, msg), null);
        var pic = data.HasValue && data.Value.ValueKind == JsonValueKind.Object
                  && data.Value.TryGetProperty("picUrl", out var pu) ? pu.GetString() : null;
        return string.IsNullOrEmpty(pic) ? (false, "EZVIZ no devolvió la foto.", null) : (true, null, pic);
    }

    // ─────────────────────────────────────────────────────────────
    // Video en vivo: link HLS (.m3u8) que el navegador reproduce con hls.js. Solo anda con las
    // cámaras SIN encriptar: EZVIZ no da HLS de las encriptadas (60019) aunque se mande el código.
    // ─────────────────────────────────────────────────────────────
    public async Task<LiveResult> GetLiveAsync(string deviceSerial, int channelNo)
    {
        if (string.IsNullOrWhiteSpace(deviceSerial))
            return new LiveResult(false, null, "Falta el número de serie de la cámara");
        var tk = await UserTokenAsync();
        if (!tk.Ok || string.IsNullOrEmpty(tk.Token))
            return new LiveResult(false, null, tk.Error ?? "No se pudo autenticar con EZVIZ");

        var (code, msg, data) = await PostAsync(tk.AreaDomain!, "/api/lapp/v2/live/address/get", new()
        {
            ["accessToken"] = tk.Token, ["deviceSerial"] = deviceSerial.Trim(),
            ["channelNo"] = channelNo.ToString(), ["protocol"] = "2", ["quality"] = "1"
        });
        if (code == "60019")
            return new LiveResult(false, null, "Esta cámara tiene la imagen encriptada y así EZVIZ no deja verla en vivo desde afuera de su app. Para verla acá, apagá \"Encriptación de video\" de esta cámara en la app EZVIZ.");
        if (code != "200")
            return new LiveResult(false, null, InterpretarCodigo(code, msg));
        var url = data.HasValue && data.Value.ValueKind == JsonValueKind.Object
                  && data.Value.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(url))
            return new LiveResult(false, null, "EZVIZ no devolvió el link del video.");
        return new LiveResult(true, url, null);
    }

    private async Task<(string? code, string? msg, JsonElement? data)> PostAsync(string host, string path, Dictionary<string, string> form)
    {
        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            var resp = await http.PostAsync($"{host.TrimEnd('/')}{path}", new FormUrlEncodedContent(form));
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var c) ? (c.ValueKind == JsonValueKind.String ? c.GetString() : c.ToString()) : null;
            var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : null;
            JsonElement? data = root.TryGetProperty("data", out var d) ? d.Clone() : null;
            return (code, msg, data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EZVIZ] error llamando {Path}", path);
            return ("ERR", "No se pudo conectar con EZVIZ: " + ex.Message, null);
        }
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
            "60019" => "La cámara tiene la imagen encriptada: hace falta el código de verificación." + extra,
            "ERR" => msg ?? "No se pudo conectar con EZVIZ.",
            _ => $"EZVIZ respondió un error (código {code}).{extra}"
        };
    }
}
