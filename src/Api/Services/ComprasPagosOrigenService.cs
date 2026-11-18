using System.Net.Http.Headers;
using System.Text.Json;
using Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Services;

/// <summary>
/// 18/11/2026 — Para "Facturas recibidas": de dónde salió cada pago, leyendo (solo lectura) las APIs:
/// · Mercado Libre: las COMPRAS que hicimos como comprador (con las 2 cuentas): qué, a qué vendedor,
///   cuánto. El importe de la orden coincide exacto con la factura que nos hace el vendedor.
/// · Mercado Pago: la descripción de cada pago que salió de la cuenta ("Movistar Línea Móvil",
///   "Producto de Casa Fernandes"…). El reporte de movimientos no la trae.
/// Todo en memoria (compras 20 min, descripciones 1 día): no guarda nada en la base.
/// </summary>
public class ComprasPagosOrigenService
{
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _http;
    private readonly MeliAccountService _meli;
    private readonly MpAccountService _mp;
    private readonly ILogger<ComprasPagosOrigenService> _log;

    public ComprasPagosOrigenService(AppDbContext db, IMemoryCache cache, IHttpClientFactory http, MeliAccountService meli,
        MpAccountService mp, ILogger<ComprasPagosOrigenService> log)
    { _db = db; _cache = cache; _http = http; _meli = meli; _mp = mp; _log = log; }

    public record CompraMeli(long OrderId, DateTime Fecha, decimal Total, string Vendedor, string Titulo, string Cuenta, List<string> PagosMp);

    /// <summary>Compras en Mercado Libre desde <paramref name="desde"/> (las canceladas no).</summary>
    public async Task<List<CompraMeli>> ComprasAsync(DateTime desde)
    {
        var key = "compras-meli-" + desde.ToString("yyyyMMdd");
        if (_cache.TryGetValue(key, out List<CompraMeli>? c) && c is not null) return c;
        var res = new List<CompraMeli>();
        var http = _http.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(25);
        foreach (var acc in await _db.MeliAccounts.ToListAsync())
        {
            try
            {
                var tok = await _meli.GetValidTokenAsync(acc);
                if (string.IsNullOrEmpty(tok)) continue;
                for (var off = 0; off < 1000; off += 50)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        $"https://api.mercadolibre.com/orders/search?buyer={acc.MeliUserId}&sort=date_desc&limit=50&offset={off}");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tok);
                    using var resp = await http.SendAsync(req);
                    if (!resp.IsSuccessStatusCode) break;
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (!doc.RootElement.TryGetProperty("results", out var rs) || rs.ValueKind != JsonValueKind.Array) break;
                    var n = 0; var viejo = false;
                    foreach (var o in rs.EnumerateArray())
                    {
                        n++;
                        var fecha = o.TryGetProperty("date_created", out var dc) && DateTimeOffset.TryParse(dc.GetString(), out var dto) ? dto.UtcDateTime : DateTime.MinValue;
                        if (fecha < desde) { viejo = true; break; }
                        if (o.TryGetProperty("status", out var st) && st.GetString() == "cancelled") continue;
                        var titulo = "";
                        if (o.TryGetProperty("order_items", out var its) && its.ValueKind == JsonValueKind.Array && its.GetArrayLength() > 0
                            && its[0].TryGetProperty("item", out var it) && it.TryGetProperty("title", out var t))
                            titulo = t.GetString() ?? "";
                        var vend = o.TryGetProperty("seller", out var se) && se.TryGetProperty("nickname", out var nn) ? nn.GetString() ?? "" : "";
                        var pagos = new List<string>();
                        if (o.TryGetProperty("payments", out var ps) && ps.ValueKind == JsonValueKind.Array)
                            foreach (var p in ps.EnumerateArray())
                                if (p.TryGetProperty("id", out var pid)) pagos.Add(pid.ToString());
                        res.Add(new CompraMeli(o.GetProperty("id").GetInt64(), fecha,
                            o.TryGetProperty("total_amount", out var ta) ? ta.GetDecimal() : 0, vend, titulo, acc.Nickname, pagos));
                    }
                    if (viejo || n < 50) break;
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Compras MeLi de {Cuenta}", acc.Nickname); }
        }
        _cache.Set(key, res, TimeSpan.FromMinutes(20));
        return res;
    }

    /// <summary>Descripción de cada pago de MP por su id ("" si no se pudo leer).</summary>
    public async Task<Dictionary<string, string>> DescripcionesMpAsync(IEnumerable<string> ids)
    {
        var res = new Dictionary<string, string>();
        var faltan = new List<string>();
        foreach (var id in ids.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct())
        {
            if (_cache.TryGetValue("mp-desc-" + id, out string? d) && d is not null) res[id] = d;
            else faltan.Add(id);
        }
        if (faltan.Count == 0) return res;
        var tok = await _mp.GetTokenAsync();
        if (string.IsNullOrEmpty(tok)) return res;
        var http = _http.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        foreach (var id in faltan.Take(120))
        {
            var desc = "";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.mercadopago.com/v1/payments/{Uri.EscapeDataString(id)}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tok);
                using var resp = await http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    var r = doc.RootElement;
                    desc = r.TryGetProperty("description", out var de) && de.ValueKind == JsonValueKind.String ? de.GetString() ?? "" : "";
                    if (r.TryGetProperty("operation_type", out var op) && op.GetString() == "investment") desc = "(pasado a inversión)";
                }
            }
            catch (Exception ex) { _log.LogDebug(ex, "Pago MP {Id}", id); }
            res[id] = desc;
            _cache.Set("mp-desc-" + id, desc, TimeSpan.FromDays(1));
        }
        return res;
    }
}
