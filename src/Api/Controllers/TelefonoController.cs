using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// Telefono dentro del sistema (central telefonica Asterisk, carpeta telefonia/).
/// 2026-11-26: un interno por persona. Cada compu se conecta con el interno de quien
/// esta usandola:
///   - usuario DEPOSITO            -> 210 (suenan todas las compus del deposito;
///                                    211-215 = "busca a ALEXIS/WALTER/..." en esas compus)
///   - operador OSMAR/GERMAN/GABRIEL (PIN) -> 201/202/203
///   - cualquier otro (ej. contadora) -> sin telefono
/// Claves: sha256("TEL_SECRET:interno"), 24 letras; la central calcula lo mismo (arrancar.sh).
/// </summary>
[ApiController]
[Route("api/telefono")]
[Authorize]
public class TelefonoController : ControllerBase
{
    public record InternoDto(string Nombre, string Numero, string Grupo);
    public record TelefonoConfigDto(bool Habilitado, string WsUrl, string Dominio, string Usuario, string Clave, string MiNombre, List<InternoDto> Internos);
    public record EstadoDto(List<string> Conectados);

    // Orden fijo: asi aparece en la lista del telefono.
    private static readonly List<InternoDto> Guia = new()
    {
        new("Teléfono fijo", "101", "Oficina"),
        new("Osmar", "201", "Oficina"),
        new("Germán", "202", "Oficina"),
        new("Gabriel", "203", "Oficina"),
        new("Depósito", "210", "Depósito"),
        new("Alexis", "211", "Depósito"),
        new("Walter", "212", "Depósito"),
        new("Rodrigo", "213", "Depósito"),
        new("Benjamín", "214", "Depósito"),
        new("Gonzalo", "215", "Depósito"),
    };

    private static readonly Dictionary<string, string> InternoDeOperador = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OSMAR"] = "201", ["GERMAN"] = "202", ["GABRIEL"] = "203",
    };

    private readonly IHttpClientFactory _http;
    public TelefonoController(IHttpClientFactory http) { _http = http; }

    private static string Secreto => Environment.GetEnvironmentVariable("TEL_SECRET") ?? "";

    private static string Clave(string interno)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes($"{Secreto}:{interno}"));
        return Convert.ToHexString(h).ToLowerInvariant()[..24];
    }

    private string? MiInterno()
    {
        var usuario = (User.Identity?.Name ?? "").Trim();
        if (usuario.Equals("DEPOSITO", StringComparison.OrdinalIgnoreCase)) return "210";
        var operador = Request.Headers["X-Operator-Name"].ToString().Trim();
        return InternoDeOperador.TryGetValue(operador, out var n) ? n : null;
    }

    [HttpGet("config")]
    public ActionResult<TelefonoConfigDto> GetConfig()
    {
        var ws = Environment.GetEnvironmentVariable("TELEFONO_WS_URL") ?? "";
        var mio = MiInterno();
        if (mio == null || string.IsNullOrWhiteSpace(Secreto) || string.IsNullOrWhiteSpace(ws))
            return new TelefonoConfigDto(false, "", "", "", "", "", Guia);
        var nombre = Guia.First(g => g.Numero == mio).Nombre;
        if (mio == "210") nombre = "Depósito";
        return new TelefonoConfigDto(true, ws, "179.43.123.134", mio, Clave(mio), nombre, Guia);
    }

    // Quien esta conectado ahora (para el puntito verde). Se lo pregunta a la central por ARI.
    private static (DateTime at, List<string> lista)? _cache;

    [HttpGet("estado")]
    public async Task<ActionResult<EstadoDto>> GetEstado()
    {
        if (_cache is { } c && DateTime.UtcNow - c.at < TimeSpan.FromSeconds(4))
            return new EstadoDto(c.lista);
        var online = new List<string>();
        try
        {
            var url = (Environment.GetEnvironmentVariable("TELEFONO_ARI_URL") ?? "http://172.19.0.1:8088") + "/ari/endpoints/PJSIP";
            var cli = _http.CreateClient();
            cli.Timeout = TimeSpan.FromSeconds(3);
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"sistema:{Clave("ari")}")));
            using var resp = await cli.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                foreach (var e in doc.RootElement.EnumerateArray())
                    if (e.GetProperty("state").GetString() == "online")
                        online.Add(e.GetProperty("resource").GetString() ?? "");
            }
        }
        catch { /* central apagada: todos grises */ }
        // 211-215 suenan en las compus del deposito: estan "conectados" si el 210 lo esta
        if (online.Contains("210")) online.AddRange(new[] { "211", "212", "213", "214", "215" });
        _cache = (DateTime.UtcNow, online);
        return new EstadoDto(online);
    }
}
