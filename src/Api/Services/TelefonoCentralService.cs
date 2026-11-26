using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-11-26: habla con la central telefonica (Asterisk, carpeta telefonia/) por ARI:
/// quien esta conectado y hacer que el sistema llame y lea un texto (mensajes programados).
/// Claves: sha256("TEL_SECRET:algo"), 24 letras; la central calcula lo mismo (arrancar.sh).
/// </summary>
public class TelefonoCentralService
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<TelefonoCentralService> _log;
    public TelefonoCentralService(IHttpClientFactory http, ILogger<TelefonoCentralService> log) { _http = http; _log = log; }

    public static string Secreto => Environment.GetEnvironmentVariable("TEL_SECRET") ?? "";

    public static string Clave(string que)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes($"{Secreto}:{que}"));
        return Convert.ToHexString(h).ToLowerInvariant()[..24];
    }

    private static string AriBase => Environment.GetEnvironmentVariable("TELEFONO_ARI_URL") ?? "http://172.19.0.1:8088";

    private HttpRequestMessage Pedido(HttpMethod m, string ruta)
    {
        var req = new HttpRequestMessage(m, AriBase + ruta);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"sistema:{Clave("ari")}")));
        return req;
    }

    private static (DateTime at, List<string> lista)? _cache;

    /// <summary>Internos conectados ahora (101, 201, 210...). Cache de 4 segundos.</summary>
    public async Task<List<string>> ConectadosAsync()
    {
        if (_cache is { } c && DateTime.UtcNow - c.at < TimeSpan.FromSeconds(4)) return c.lista;
        var online = new List<string>();
        try
        {
            var cli = _http.CreateClient();
            cli.Timeout = TimeSpan.FromSeconds(3);
            using var resp = await cli.SendAsync(Pedido(HttpMethod.Get, "/ari/endpoints/PJSIP"));
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                foreach (var e in doc.RootElement.EnumerateArray())
                    if (e.GetProperty("state").GetString() == "online")
                        online.Add(e.GetProperty("resource").GetString() ?? "");
            }
        }
        catch { /* central apagada: todos grises */ }
        _cache = (DateTime.UtcNow, online);
        return online;
    }

    /// <summary>
    /// Hace que la central llame a 'interno' y, al atender, lea 'texto' en voz alta (dos veces).
    /// Va por Local/{interno}@internos para que suene en todas las compus de esa persona.
    /// </summary>
    public async Task<bool> LlamarYLeerAsync(string interno, string texto, int mensajeId)
    {
        try
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(texto));
            var qs = $"?endpoint={Uri.EscapeDataString($"Local/{interno}@internos/n")}" +
                     "&context=mensaje&extension=s&priority=1&timeout=40" +
                     $"&callerId={Uri.EscapeDataString("\"Mensaje\" <300>")}";
            var req = Pedido(HttpMethod.Post, "/ari/channels" + qs);
            req.Content = new StringContent(JsonSerializer.Serialize(new
            {
                variables = new Dictionary<string, string>
                {
                    ["TEXTOB64"] = b64,
                    ["MSGID"] = mensajeId.ToString(),
                    ["MSGA"] = interno,
                }
            }), Encoding.UTF8, "application/json");
            var cli = _http.CreateClient();
            cli.Timeout = TimeSpan.FromSeconds(5);
            using var resp = await cli.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("Telefono: la central no acepto la llamada a {Interno}: {Code} {Body}", interno, (int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Telefono: no se pudo llamar a {Interno}", interno);
            return false;
        }
    }

    /// <summary>Internos a los que se manda un mensaje: uno, o "todos" = los conectados.</summary>
    public async Task<List<string>> DestinosAsync(string destino)
    {
        if (!destino.Equals("todos", StringComparison.OrdinalIgnoreCase)) return new() { destino };
        var con = await ConectadosAsync();
        return new[] { "101", "201", "202", "203", "210" }.Where(con.Contains).ToList();
    }
}

/// <summary>
/// 2026-11-26: cada minuto revisa los mensajes programados (Telefono_Mensajes) y, a la hora
/// argentina indicada, hace que la central llame y lea el texto.
/// </summary>
public class TelefonoMensajesBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelefonoMensajesBackgroundService> _log;
    public TelefonoMensajesBackgroundService(IServiceScopeFactory s, ILogger<TelefonoMensajesBackgroundService> log) { _scopeFactory = s; _log = log; }

    // L M X J V S D (lunes primero), como en la pantalla
    public static char LetraDia(DayOfWeek d) => "DLMXJVS"[(int)d];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(); } catch (Exception ex) { _log.LogWarning(ex, "Telefono: fallo revisando mensajes programados"); }
            // esperar hasta el proximo minuto redondo (+2 s)
            var ahora = DateTime.UtcNow;
            var espera = TimeSpan.FromSeconds(62 - ahora.Second);
            try { await Task.Delay(espera, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var central = scope.ServiceProvider.GetRequiredService<TelefonoCentralService>();
        var ahora = PanoramaService.AhoraAr();
        var hhmm = ahora.ToString("HH:mm");
        var minuto = new DateTime(ahora.Year, ahora.Month, ahora.Day, ahora.Hour, ahora.Minute, 0);
        var letra = LetraDia(ahora.DayOfWeek);

        var lista = await db.TelefonoMensajes.Where(m => m.Activo && m.Hora == hhmm).ToListAsync();
        foreach (var m in lista)
        {
            if (m.UltimoEnvioAr is { } u && u >= minuto) continue;   // ya salio este minuto
            var toca = string.IsNullOrEmpty(m.Dias)
                ? m.Fecha?.Date == ahora.Date
                : m.Dias.Contains(letra);
            if (!toca) continue;
            m.UltimoEnvioAr = minuto;
            if (string.IsNullOrEmpty(m.Dias)) m.Activo = false;   // era de una sola vez
            await db.SaveChangesAsync();
            foreach (var interno in await central.DestinosAsync(m.Destino))
                await central.LlamarYLeerAsync(interno, m.Texto, m.Id);
        }
    }
}
