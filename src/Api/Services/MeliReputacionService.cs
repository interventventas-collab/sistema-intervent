using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Services;

/// <param name="Cantidad">Cuántos casos cuenta MeLi en el período.</param>
/// <param name="Porcentaje">El % que calcula MeLi (ya en 0-100).</param>
/// <param name="Tope">El % máximo para no perder el nivel (ya en 0-100).</param>
/// <param name="TeEntran">Cuántos casos más entran antes de llegar al tope.</param>
public record MeliMetricaDto(string Nombre, int Cantidad, decimal Porcentaje, decimal Tope, int TeEntran);

/// <param name="Color">verde | amarillo | naranja | rojo | (vacío si MeLi no lo informa).</param>
/// <param name="Lider">platinum | gold | silver | null.</param>
/// <param name="Periodo">Texto de MeLi: "60 days" o "365 days".</param>
public record MeliReputacionCuentaDto(string Cuenta, string? Color, string? Lider, int Ventas, string? Periodo,
                                      List<MeliMetricaDto> Metricas, string? Problema);

public record MeliReputacionDto(DateTime ConsultadoAt, List<MeliReputacionCuentaDto> Cuentas);

/// <summary>
/// 2026-11-12 — Reputación de cada cuenta de MercadoLibre para el cuadro del final de Panorama.
/// Sale de GET /users/{id} (seller_reputation). MeLi da los números pero NO los topes:
/// los topes están acá fijos, según la ayuda de MeLi Argentina:
///  - cuenta MercadoLíder: reclamos 1%, cancelaciones 0,5%, demoras 8% (pasarse = perder el MercadoLíder)
///  - cuenta común:        reclamos 1,5%, cancelaciones 1%, demoras 10% (pasarse = dejar el verde)
/// Se guarda 30 minutos para no preguntarle a MeLi cada vez que alguien abre la pantalla.
/// </summary>
public class MeliReputacionService
{
    private const string ClaveCache = "meli-reputacion";

    private readonly MeliAccountService _cuentas;
    private readonly IHttpClientFactory _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MeliReputacionService> _log;

    public MeliReputacionService(MeliAccountService cuentas, IHttpClientFactory http,
                                 IMemoryCache cache, ILogger<MeliReputacionService> log)
    {
        _cuentas = cuentas;
        _http = http;
        _cache = cache;
        _log = log;
    }

    public async Task<MeliReputacionDto> GetAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(ClaveCache, out MeliReputacionDto? guardado) && guardado is not null)
            return guardado;

        var lista = new List<MeliReputacionCuentaDto>();
        foreach (var cuenta in (await _cuentas.GetAllAccountEntitiesAsync()).OrderBy(c => c.Id))
            lista.Add(await ConsultarAsync(cuenta, ct));

        var resultado = new MeliReputacionDto(DateTime.UtcNow, lista);
        var hayProblema = lista.Any(c => c.Problema is not null);
        _cache.Set(ClaveCache, resultado, TimeSpan.FromMinutes(hayProblema ? 2 : 30));
        return resultado;
    }

    private async Task<MeliReputacionCuentaDto> ConsultarAsync(Models.MeliAccount cuenta, CancellationToken ct)
    {
        var nombre = cuenta.Nickname;
        try
        {
            var token = await _cuentas.GetValidTokenAsync(cuenta);
            if (string.IsNullOrEmpty(token))
                return Vacia(nombre, "No se pudo entrar a la cuenta de MercadoLibre.");

            var http = _http.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.mercadolibre.com/users/{cuenta.MeliUserId}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Reputación MeLi {Cuenta}: HTTP {Status}", nombre, (int)resp.StatusCode);
                return Vacia(nombre, "MercadoLibre no contestó, se reintenta en unos minutos.");
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("seller_reputation", out var rep) || rep.ValueKind != JsonValueKind.Object)
                return Vacia(nombre, "MercadoLibre no informó la reputación.");

            var lider = Texto(rep, "power_seller_status");
            var color = Color(Texto(rep, "level_id"));

            var ventas = 0;
            string? periodo = null;
            var metricas = new List<MeliMetricaDto>();
            if (rep.TryGetProperty("metrics", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                if (m.TryGetProperty("sales", out var s))
                {
                    ventas = Entero(s, "completed");
                    periodo = Texto(s, "period");
                }
                var esLider = !string.IsNullOrEmpty(lider);
                metricas.Add(Metrica(m, "claims", "Reclamos", esLider ? 1m : 1.5m, ventas));
                metricas.Add(Metrica(m, "cancellations", "Cancelaciones", esLider ? 0.5m : 1m, ventas));
                metricas.Add(Metrica(m, "delayed_handling_time", "Demoras", esLider ? 8m : 10m, ventas));
            }

            return new MeliReputacionCuentaDto(nombre, color, lider, ventas, periodo, metricas, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Reputación MeLi {Cuenta}: falló la consulta", nombre);
            return Vacia(nombre, "MercadoLibre no contestó, se reintenta en unos minutos.");
        }
    }

    private static MeliMetricaDto Metrica(JsonElement metrics, string clave, string nombre, decimal tope, int ventas)
    {
        var cantidad = 0;
        var tasa = 0m;
        if (metrics.TryGetProperty(clave, out var x) && x.ValueKind == JsonValueKind.Object)
        {
            cantidad = Entero(x, "value");
            if (x.TryGetProperty("rate", out var r) && r.ValueKind == JsonValueKind.Number)
                tasa = r.GetDecimal();
        }

        // MeLi divide por su propia base (no siempre igual a "ventas"): la sacamos de cantidad/tasa
        // para que "te entran X más" dé la misma cuenta que la de MeLi.
        decimal baseMeli = tasa > 0 && cantidad > 0 ? cantidad / tasa : ventas;
        var maximo = (int)Math.Floor(baseMeli * tope / 100m);
        var teEntran = Math.Max(0, maximo - cantidad);

        return new MeliMetricaDto(nombre, cantidad, Math.Round(tasa * 100m, 2), tope, teEntran);
    }

    private static MeliReputacionCuentaDto Vacia(string nombre, string problema)
        => new(nombre, null, null, 0, null, new(), problema);

    private static string? Texto(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Entero(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDecimal() : 0;

    // level_id viene como "5_green", "4_light_green", "3_yellow", "2_orange", "1_red".
    private static string? Color(string? levelId) => levelId switch
    {
        null or "" => null,
        var l when l.EndsWith("light_green") => "verde claro",
        var l when l.EndsWith("green") => "verde",
        var l when l.EndsWith("yellow") => "amarillo",
        var l when l.EndsWith("orange") => "naranja",
        var l when l.EndsWith("red") => "rojo",
        _ => null
    };
}
