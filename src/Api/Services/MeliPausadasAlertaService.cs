using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>2026-11-23: cuando entra stock a una publicación MeLi pausada (out_of_stock), decide
/// si se activa sola y deja la alerta para el admin. Regla del dueño:
/// - deja 40% o más → se activa sola, sin aviso.
/// - deja entre 10% y 40% → se activa sola y queda aviso en la tarjeta MercadoLibre (ACTIVADA_POCO).
/// - deja menos de 10%, da pérdida o no se puede calcular → NO se activa; alerta al centro de la
///   pantalla + Telegram (NO_ACTIVADA).
/// Comisión y envío salen EN VIVO de MeLi (listing_prices + shipping_options/free), así el % es real.
/// Umbrales en AppSettings: meli.pausadas.activar_min_pct (10) y meli.pausadas.avisar_bajo_pct (40).</summary>
public class MeliPausadasAlertaService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accounts;
    private readonly TelegramService _telegram;
    private readonly ILogger<MeliPausadasAlertaService> _logger;

    public MeliPausadasAlertaService(AppDbContext db, IHttpClientFactory httpFactory, MeliAccountService accounts,
        TelegramService telegram, ILogger<MeliPausadasAlertaService> logger)
    {
        _db = db; _httpFactory = httpFactory; _accounts = accounts; _telegram = telegram; _logger = logger;
    }

    public const string NO_ACTIVADA = "NO_ACTIVADA";
    public const string ACTIVADA_POCO = "ACTIVADA_POCO";

    public async Task<(decimal ActivarMin, decimal AvisarBajo)> UmbralesAsync(CancellationToken ct = default)
    {
        var s = await _db.AppSettings.AsNoTracking()
            .Where(x => x.Key == "meli.pausadas.activar_min_pct" || x.Key == "meli.pausadas.avisar_bajo_pct")
            .ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        decimal Leer(string k, decimal def) => s.TryGetValue(k, out var v)
            && decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) && d >= 0 ? d : def;
        return (Leer("meli.pausadas.activar_min_pct", 10m), Leer("meli.pausadas.avisar_bajo_pct", 40m));
    }

    /// <summary>¿El admin ya tocó "Activar" para esta publicación? Entonces el push la despierta igual.</summary>
    public Task<bool> EstaAutorizadaAsync(string meliItemId, CancellationToken ct = default)
        => _db.MeliAlertasPausadas.AnyAsync(a => a.MeliItemId == meliItemId && a.Estado == "AUTORIZADA", ct);

    // ── Datos de la publicación que hacen falta para calcular en vivo ─────────────────────────
    public record DatosPubli(string ItemId, string? CategoryId, string? ListingTypeId, long? SellerId,
        string? LogisticType, bool FreeShipping, decimal PrecioActual, string? Permalink, string? Titulo);

    public static DatosPubli LeerDatos(string itemId, JsonElement doc)
    {
        string? S(string p) => doc.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        long? seller = doc.TryGetProperty("seller_id", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetInt64() : null;
        decimal precio = doc.TryGetProperty("price", out var pv) && pv.ValueKind == JsonValueKind.Number ? pv.GetDecimal() : 0m;
        string? lt = null; bool free = false;
        if (doc.TryGetProperty("shipping", out var sh) && sh.ValueKind == JsonValueKind.Object)
        {
            if (sh.TryGetProperty("logistic_type", out var l) && l.ValueKind == JsonValueKind.String) lt = l.GetString();
            if (sh.TryGetProperty("free_shipping", out var f) && f.ValueKind is JsonValueKind.True) free = true;
        }
        return new DatosPubli(itemId, S("category_id"), S("listing_type_id"), seller, lt, free, precio, S("permalink"), S("title"));
    }

    // ── Comisión, envío y % en vivo ───────────────────────────────────────────────────────────
    private async Task<JsonElement?> GetJsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        for (var i = 0; i < 4; i++)
        {
            try
            {
                var r = await http.GetAsync(url, ct);
                if ((int)r.StatusCode == 429) { await Task.Delay(2500, ct); continue; }
                if (!r.IsSuccessStatusCode) return null;
                return JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { await Task.Delay(1000, ct); }
        }
        return null;
    }

    /// <summary>(comisión total, % de comisión) de MeLi para ese precio.</summary>
    private async Task<(decimal Fee, decimal Pct)?> ComisionAsync(HttpClient http, DatosPubli d, decimal precio, CancellationToken ct)
    {
        if (d.CategoryId is null || d.ListingTypeId is null) return null;
        var j = await GetJsonAsync(http, $"https://api.mercadolibre.com/sites/MLA/listing_prices?price={precio.ToString(CultureInfo.InvariantCulture)}&listing_type_id={d.ListingTypeId}&category_id={d.CategoryId}", ct);
        if (j is null) return null;
        var e = j.Value.ValueKind == JsonValueKind.Array ? (j.Value.GetArrayLength() > 0 ? j.Value[0] : default) : j.Value;
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("sale_fee_amount", out var f) || f.ValueKind != JsonValueKind.Number) return null;
        decimal pct = 0;
        if (e.TryGetProperty("sale_fee_details", out var det) && det.ValueKind == JsonValueKind.Object
            && det.TryGetProperty("percentage_fee", out var p) && p.ValueKind == JsonValueKind.Number) pct = p.GetDecimal();
        return (f.GetDecimal(), pct);
    }

    /// <summary>Lo que nos cobra MeLi por el envío gratis a ese precio (0 si lo paga el comprador).</summary>
    private async Task<decimal?> EnvioAsync(HttpClient http, DatosPubli d, decimal precio, CancellationToken ct)
    {
        // Abajo de $33.000 el envío lo paga el comprador, salvo que la publi ya tenga envío gratis
        // estando abajo de 33k (se lo dimos nosotros).
        var pagamos = precio >= 33000m || (d.FreeShipping && d.PrecioActual < 33000m);
        if (!pagamos) return 0m;
        if (d.SellerId is null) return null;
        var lt = string.IsNullOrEmpty(d.LogisticType) ? "cross_docking" : d.LogisticType;
        var j = await GetJsonAsync(http, $"https://api.mercadolibre.com/users/{d.SellerId}/shipping_options/free?item_id={d.ItemId}&item_price={precio.ToString(CultureInfo.InvariantCulture)}&listing_type_id={d.ListingTypeId}&mode=me2&condition=new&logistic_type={lt}", ct);
        if (j is null) return null;
        if (j.Value.TryGetProperty("coverage", out var c) && c.TryGetProperty("all_country", out var a)
            && a.TryGetProperty("list_cost", out var lc) && lc.ValueKind == JsonValueKind.Number) return lc.GetDecimal();
        return null;
    }

    /// <summary>% de ganancia sobre costo, con la fórmula del sistema: (precio − comisión − envío)/1,21 vs costo.</summary>
    public async Task<decimal?> MargenEnVivoAsync(HttpClient http, DatosPubli d, decimal precio, decimal costo, CancellationToken ct)
    {
        if (costo <= 0 || precio <= 0) return null;
        var com = await ComisionAsync(http, d, precio, ct);
        if (com is null) return null;
        var env = await EnvioAsync(http, d, precio, ct);
        if (env is null) return null;
        var neto = (precio - com.Value.Fee - env.Value) / 1.21m;
        return Math.Round((neto - costo) / costo * 100m, 1);
    }

    /// <summary>Precio más bajo terminado en 99 que deja al menos <paramref name="pct"/>%. Null si no se pudo.
    /// Arranca del piso posible y sube saltando lo que falta (sirve igual para $5.000 que para $150.000);
    /// al encontrarlo prueba bajar de a $100 por si sobra.</summary>
    public async Task<decimal?> PrecioParaMargenAsync(HttpClient http, DatosPubli d, decimal costo, decimal pct, CancellationToken ct)
    {
        if (costo <= 0) return null;
        var com = await ComisionAsync(http, d, d.PrecioActual > 0 ? d.PrecioActual : 20000m, ct);
        if (com is null) return null;
        var pf = com.Value.Pct > 0 ? com.Value.Pct / 100m : 0.14m;
        var netoQuiero = costo * (1 + pct / 100m);            // lo que tiene que quedar sin IVA
        // Piso: sin cargo fijo ni envío (imposible bajar de acá). Desde ahí se sube lo que falte.
        var p = MeliPricePushService.RedondearA99(netoQuiero * 1.21m / (1 - pf));

        async Task<decimal?> Neto(decimal precio)
        {
            var c = await ComisionAsync(http, d, precio, ct);
            var e = await EnvioAsync(http, d, precio, ct);
            return c is null || e is null ? null : (precio - c.Value.Fee - e.Value) / 1.21m;
        }

        for (var i = 0; i < 12; i++)
        {
            var n = await Neto(p);
            if (n is null) return null;
            if (n.Value >= netoQuiero)
            {
                for (var j = 0; j < 5 && p > 199m; j++)
                {
                    var nb = await Neto(p - 100m);
                    if (nb is null || nb.Value < netoQuiero) break;
                    p -= 100m;
                }
                return p;
            }
            var falta = (netoQuiero - n.Value) * 1.21m / (1 - pf);
            p = MeliPricePushService.RedondearA99(p + Math.Max(100m, falta));
        }
        return null;
    }

    // ── Guardar / avisar ──────────────────────────────────────────────────────────────────────
    /// <summary>Crea o actualiza LA alerta abierta de esa publicación. Si el admin la había dejado
    /// pausada ("Dejar pausada") con el mismo stock y costo, no vuelve a molestar.</summary>
    public async Task RegistrarAsync(string tipo, MeliItem row0, DatosPubli d, int stock, decimal? costo,
        decimal? margen, string motivo, HttpClient http, CancellationToken ct)
    {
        try
        {
            var abierta = await _db.MeliAlertasPausadas
                .Where(a => a.MeliItemId == row0.MeliItemId && (a.Estado == "ABIERTA" || a.Estado == "AUTORIZADA"))
                .OrderByDescending(a => a.Id).FirstOrDefaultAsync(ct);
            if (abierta is null)
            {
                var dejada = await _db.MeliAlertasPausadas.AsNoTracking()
                    .Where(a => a.MeliItemId == row0.MeliItemId && a.Estado == "DEJADA")
                    .OrderByDescending(a => a.Id).FirstOrDefaultAsync(ct);
                if (dejada is not null && dejada.Stock == stock && dejada.Costo == costo) return;
            }

            decimal? p40 = null, p50 = null;
            if (costo is > 0)
            {
                var (_, avisar) = await UmbralesAsync(ct);
                p40 = await PrecioParaMargenAsync(http, d, costo.Value, avisar, ct);
                p50 = await PrecioParaMargenAsync(http, d, costo.Value, 50m, ct);
            }

            var nueva = abierta is null || abierta.Tipo != tipo;
            var a = abierta ?? new MeliAlertaPausada { MeliItemId = row0.MeliItemId, CreatedAt = DateTime.UtcNow };
            if (abierta is not null && abierta.Tipo != tipo) { a.CreatedAt = DateTime.UtcNow; a.PospuestaHasta = null; }
            a.MeliAccountId = row0.MeliAccountId;
            a.Tipo = tipo;
            a.Estado = "ABIERTA";
            a.Titulo = d.Titulo ?? row0.Title;
            a.Sku = row0.Sku;
            a.Permalink = d.Permalink ?? row0.Permalink;
            a.Motivo = motivo;
            a.PrecioActual = d.PrecioActual;
            a.MargenPct = margen;
            a.Costo = costo;
            a.Precio40 = p40;
            a.Precio50 = p50;
            a.Stock = stock;
            a.UpdatedAt = DateTime.UtcNow;
            if (abierta is null) _db.MeliAlertasPausadas.Add(a);
            await _db.SaveChangesAsync(ct);

            if (nueva && tipo == NO_ACTIVADA)
            {
                var sb = new StringBuilder();
                sb.AppendLine("🔴 MERCADOLIBRE · ENTRÓ STOCK Y NO SE ACTIVÓ");
                sb.AppendLine(a.Titulo ?? a.MeliItemId);
                sb.AppendLine($"{a.Sku} · {a.MeliItemId} · stock {stock}");
                sb.AppendLine(motivo);
                if (p40 is not null) sb.AppendLine($"Para 40%: {Plata(p40.Value)}" + (p50 is not null ? $" · para 50%: {Plata(p50.Value)}" : ""));
                sb.AppendLine("Resolvela desde el sistema: te aparece al centro de la pantalla.");
                try { await _telegram.SendMessageAsync(sb.ToString().TrimEnd(), ct: ct); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Pausadas] no se pudo registrar la alerta de {Mla}", row0.MeliItemId);
        }
    }

    public static string Plata(decimal v) => "$" + v.ToString("N0", CultureInfo.GetCultureInfo("es-AR"));

    /// <summary>Cierra solas las alertas NO_ACTIVADA cuya publicación ya está activa (alguien la activó
    /// en MeLi o el sistema) y las que quedaron AUTORIZADA hace más de 1 hora sin que el push las despierte.</summary>
    public async Task AutoResolverAsync(CancellationToken ct = default)
    {
        var abiertas = await _db.MeliAlertasPausadas
            .Where(a => a.Tipo == NO_ACTIVADA && (a.Estado == "ABIERTA" || a.Estado == "AUTORIZADA")).ToListAsync(ct);
        if (abiertas.Count == 0) return;
        var ids = abiertas.Select(a => a.MeliItemId).Distinct().ToList();
        var activas = await _db.MeliItems.AsNoTracking().Where(i => ids.Contains(i.MeliItemId) && i.Status == "active")
            .Select(i => i.MeliItemId).Distinct().ToListAsync(ct);
        var cambio = false;
        foreach (var a in abiertas.Where(a => activas.Contains(a.MeliItemId)))
        {
            a.Estado = "RESUELTA"; a.ResueltaAt = DateTime.UtcNow; a.Accion ??= "Se activó"; a.ResueltaPor ??= "sistema";
            cambio = true;
        }
        if (cambio) await _db.SaveChangesAsync(ct);
    }

    /// <summary>Cliente HTTP con el token de la cuenta de la publicación.</summary>
    public async Task<HttpClient?> ClienteParaAsync(int? meliAccountId, CancellationToken ct = default)
    {
        var acc = meliAccountId is int id ? await _db.MeliAccounts.FirstOrDefaultAsync(x => x.Id == id, ct) : null;
        if (acc is null) return null;
        var token = await _accounts.GetValidTokenAsync(acc);
        if (string.IsNullOrEmpty(token)) return null;
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.Timeout = TimeSpan.FromSeconds(30);
        return http;
    }

    /// <summary>Estado actual de la publicación en MeLi (active / paused / under_review...). Null si no respondió.</summary>
    public async Task<string?> EstadoEnVivoAsync(HttpClient http, string meliItemId, CancellationToken ct)
    {
        var j = await GetJsonAsync(http, $"https://api.mercadolibre.com/items/{meliItemId}?attributes=status", ct);
        return j is not null && j.Value.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
    }

    /// <summary>PUT del precio en MeLi. Devuelve null si salió bien, o el mensaje de error.</summary>
    public async Task<string?> CambiarPrecioAsync(HttpClient http, string meliItemId, decimal precio, CancellationToken ct)
    {
        for (var i = 0; i < 4; i++)
        {
            var body = new StringContent(JsonSerializer.Serialize(new { price = precio }), Encoding.UTF8, "application/json");
            var r = await http.PutAsync($"https://api.mercadolibre.com/items/{meliItemId}", body, ct);
            if ((int)r.StatusCode == 429) { await Task.Delay(3000, ct); continue; }
            if (r.IsSuccessStatusCode)
            {
                foreach (var row in await _db.MeliItems.Where(x => x.MeliItemId == meliItemId).ToListAsync(ct)) row.Price = precio;
                await _db.SaveChangesAsync(ct);
                return null;
            }
            var txt = await r.Content.ReadAsStringAsync(ct);
            return $"MeLi no aceptó el precio ({(int)r.StatusCode}): {(txt.Length > 200 ? txt[..200] : txt)}";
        }
        return "MeLi está ocupado (demasiados pedidos), probá de nuevo en un minuto";
    }
}
