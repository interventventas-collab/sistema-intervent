using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-18: AVISOS CON LA PANTALLA CERRADA para la pantalla de WhatsApp del celular.
///
/// Hasta ahora el celu solo avisaba con la pantalla abierta (un timer que miraba si había algo
/// nuevo). Si cerraban el navegador, no se enteraban de nada. Esto usa Web Push: el teléfono queda
/// suscripto al servicio de notificaciones de su propio navegador (Google/Apple) y nosotros le
/// pedimos a ese servicio que lo despierte.
///
/// DECISIÓN IMPORTANTE — se manda un push VACÍO (sin texto adentro):
///  - Mandar texto obliga a cifrar el contenido (AES128GCM + claves del navegador), que es la parte
///    complicada y frágil de Web Push, y encima haría viajar el mensaje del cliente por los
///    servidores de Google/Apple.
///  - Sin texto solo hace falta firmar el pedido (VAPID, un JWT ES256), que .NET hace solo.
///  - El aviso lo arma el propio teléfono: el service worker, al recibir el empujón, le pregunta al
///    sistema quién escribió (con la sesión del usuario) y muestra el nombre. Ver wwwroot/sw.js.
///
/// Las suscripciones y las claves viven en AppSettings (clave/valor) para no tener que crear tablas
/// nuevas: son 3 o 4 teléfonos, no hace falta más.
/// </summary>
public class WaPushService
{
    private const string KeyPublica = "wa.push.vapid.public";
    private const string KeyPrivada = "wa.push.vapid.private";
    private const string PrefijoSub = "wa.push.sub.";
    /// <summary>Contacto que exige la especificación VAPID (lo usa el servicio de push si hay un problema).</summary>
    private const string Subject = "mailto:soporte@frikaf.online";

    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<WaPushService> _logger;

    public WaPushService(AppDbContext db, IHttpClientFactory http, ILogger<WaPushService> logger)
    {
        _db = db; _http = http; _logger = logger;
    }

    // ═══════════════ Claves VAPID ═══════════════

    /// <summary>Devuelve la clave pública (base64url). La primera vez genera el par y lo guarda.</summary>
    public async Task<string> ObtenerClavePublicaAsync()
    {
        var pub = await LeerAsync(KeyPublica);
        var priv = await LeerAsync(KeyPrivada);
        if (!string.IsNullOrWhiteSpace(pub) && !string.IsNullOrWhiteSpace(priv)) return pub!;

        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ec.ExportParameters(true);
        // Formato que espera el navegador: 0x04 || X || Y
        var publicBytes = new byte[65];
        publicBytes[0] = 0x04;
        Buffer.BlockCopy(p.Q.X!, 0, publicBytes, 1, 32);
        Buffer.BlockCopy(p.Q.Y!, 0, publicBytes, 33, 32);

        pub = B64Url(publicBytes);
        priv = B64Url(p.D!);
        await GuardarAsync(KeyPublica, pub);
        await GuardarAsync(KeyPrivada, priv);
        _logger.LogInformation("[WaPush] par de claves VAPID generado");
        return pub;
    }

    // ═══════════════ Suscripciones (un teléfono cada una) ═══════════════

    public async Task SuscribirAsync(string endpoint, string? nombre)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return;
        var valor = JsonSerializer.Serialize(new { endpoint, nombre, at = DateTime.UtcNow });
        await GuardarAsync(PrefijoSub + Hash(endpoint), valor);
        _logger.LogInformation("[WaPush] teléfono suscripto ({Nombre})", nombre ?? "sin nombre");
    }

    public async Task BajaAsync(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return;
        await BorrarAsync(PrefijoSub + Hash(endpoint));
    }

    /// <summary>Cuántos teléfonos están suscriptos (para mostrarlo en la pantalla).</summary>
    public async Task<int> CantidadAsync()
        => await _db.AppSettings.AsNoTracking().CountAsync(s => s.Key.StartsWith(PrefijoSub));

    // ═══════════════ Envío ═══════════════

    /// <summary>Despierta a todos los teléfonos suscriptos. Best-effort: nunca tira excepción hacia
    /// afuera, y da de baja solos los que el navegador ya no reconoce (404/410).</summary>
    public async Task AvisarAsync()
    {
        var subs = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith(PrefijoSub))
            .ToListAsync();
        if (subs.Count == 0) return;

        var pub = await LeerAsync(KeyPublica);
        var priv = await LeerAsync(KeyPrivada);
        if (string.IsNullOrWhiteSpace(pub) || string.IsNullOrWhiteSpace(priv)) return;

        var cli = _http.CreateClient();
        cli.Timeout = TimeSpan.FromSeconds(10);

        foreach (var s in subs)
        {
            string? endpoint = null;
            try
            {
                using var doc = JsonDocument.Parse(s.Value);
                endpoint = doc.RootElement.TryGetProperty("endpoint", out var e) ? e.GetString() : null;
                if (string.IsNullOrWhiteSpace(endpoint)) continue;

                var uri = new Uri(endpoint);
                var jwt = FirmarVapid($"{uri.Scheme}://{uri.Host}", priv!);

                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
                req.Headers.TryAddWithoutValidation("Authorization", $"vapid t={jwt}, k={pub}");
                req.Headers.TryAddWithoutValidation("TTL", "120");
                // Sin cuerpo: el aviso lo arma el teléfono (ver el comentario de arriba).
                req.Content = new ByteArrayContent(Array.Empty<byte>());
                req.Content.Headers.ContentLength = 0;

                var resp = await cli.SendAsync(req);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound
                    || resp.StatusCode == System.Net.HttpStatusCode.Gone)
                {
                    // El teléfono desinstaló la pantalla o limpió los permisos.
                    await BorrarAsync(s.Key);
                    _logger.LogInformation("[WaPush] suscripción vencida dada de baja");
                }
                else if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[WaPush] el servicio de push contestó {Code} para {Host}",
                        (int)resp.StatusCode, uri.Host);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[WaPush] no se pudo avisar a un teléfono");
            }
        }
    }

    // ═══════════════ VAPID (JWT ES256 firmado con la clave privada) ═══════════════

    private static string FirmarVapid(string audiencia, string privadaB64Url)
    {
        var header = B64Url(Encoding.UTF8.GetBytes("""{"typ":"JWT","alg":"ES256"}"""));
        var exp = DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds();
        var claims = B64Url(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new Dictionary<string, object> { ["aud"] = audiencia, ["exp"] = exp, ["sub"] = Subject })));
        var firmar = Encoding.UTF8.GetBytes($"{header}.{claims}");

        var d = FromB64Url(privadaB64Url);
        using var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d });
        // IeeeP1363 = r||s crudo (64 bytes), que es lo que pide la especificación (no DER).
        var firma = ec.SignData(firmar, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{header}.{claims}.{B64Url(firma)}";
    }

    // ═══════════════ Utilidades ═══════════════

    private static string B64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        switch (t.Length % 4) { case 2: t += "=="; break; case 3: t += "="; break; }
        return Convert.FromBase64String(t);
    }

    private static string Hash(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..24].ToLowerInvariant();

    private async Task<string?> LeerAsync(string key)
        => await _db.AppSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync();

    private async Task GuardarAsync(string key, string value)
    {
        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null) _db.AppSettings.Add(new AppSetting { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
        else { row.Value = value; row.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
    }

    private async Task BorrarAsync(string key)
    {
        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null) return;
        _db.AppSettings.Remove(row);
        await _db.SaveChangesAsync();
    }
}
