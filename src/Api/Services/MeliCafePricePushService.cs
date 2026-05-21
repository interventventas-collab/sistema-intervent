using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Pushea precios y stock a las publicaciones MeLi de cafes, usando el PriceRatioOverIva
/// (capturado al inicio) para respetar el incremento por cuotas+envio+comision de cada item.
///
/// Para cada MeliItem linkeado a un café del sistema:
///   precio_meli = precio_sistema_neto × 1.21 × PriceRatioOverIva
///   stock_meli  = StockGramos / gramosPorFormato (1000/500/250)
///
/// Si el item tiene variations (multivariante), aplica price+available_quantity a TODAS las variations.
/// Si no, al item raíz.
///
/// Decision usuario 2026-05-21: solo CAFES. Otros productos no se pushean por ahora.
/// </summary>
public class MeliCafePricePushService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _meliAccounts;
    private readonly ILogger<MeliCafePricePushService> _logger;
    private const decimal IVA = 1.21m;

    public MeliCafePricePushService(AppDbContext db, IHttpClientFactory httpFactory,
        MeliAccountService meliAccounts, ILogger<MeliCafePricePushService> logger)
    {
        _db = db; _httpFactory = httpFactory; _meliAccounts = meliAccounts; _logger = logger;
    }

    public record PushResult(int Procesadas, int Ok, int Errores, List<string> Mensajes);

    /// <summary>Calcula el precio del sistema (neto, antes de IVA) para un café en un formato.
    /// Toma el PrecioOtro (PVP sugerido) y aplica la regla de fraccionamiento del setting actual.</summary>
    public decimal CalcularPrecioSistemaNeto(CafeProducto p, string formato, CafeSetting cfg)
    {
        var precioBase = p.PrecioOtro ?? p.Pvp2 ?? p.PrecioPorKg ?? 0m;
        if (precioBase <= 0) return 0m;
        var costoFracc = cfg.CostoFraccionamiento;
        return formato.ToUpperInvariant() switch
        {
            "1KG" => precioBase,
            "MEDIO" => RoundUpToMultiple((precioBase / 2m) + costoFracc, cfg.RedondeoMultiplo, true),
            "CUARTO" => (precioBase / 4m) + costoFracc,
            _ => precioBase
        };
    }
    private static decimal RoundUpToMultiple(decimal value, decimal multiple, bool aplicar)
    {
        if (!aplicar || multiple <= 0) return value;
        return Math.Ceiling(value / multiple) * multiple;
    }
    private static int GramosPorFormato(string formato) => formato.ToUpperInvariant() switch
    {
        "1KG" => 1000, "MEDIO" => 500, "CUARTO" => 250, _ => 1000
    };

    public async Task<PushResult> PushAllCafesAsync(CancellationToken ct = default)
    {
        var cfg = await _db.CafeSettings.FindAsync(new object[] { 1 }, ct) ?? new CafeSetting { Id = 1 };
        // Cargo todos los MeliItems linkeados a cafés con ratio capturado.
        var items = await _db.MeliItems
            .Include(mi => mi.MeliAccount)
            .Where(mi => mi.CafeProductoId != null && mi.CafeFormato != null && mi.PriceRatioOverIva != null && mi.PriceRatioOverIva > 0)
            .ToListAsync(ct);

        if (items.Count == 0)
            return new PushResult(0, 0, 0, new() { "Sin items para procesar — no hay café linkeado con ratio." });

        // Pre-cargar los productos café
        var prodIds = items.Select(i => i.CafeProductoId!.Value).Distinct().ToList();
        var prods = await _db.CafeProductos.Where(p => prodIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

        int ok = 0, err = 0;
        var mensajes = new List<string>();

        // Agrupar por cuenta para reutilizar token.
        foreach (var grupo in items.GroupBy(i => i.MeliAccountId))
        {
            var acc = grupo.First().MeliAccount;
            if (acc is null) { err += grupo.Count(); mensajes.Add($"Cuenta {grupo.Key} no encontrada"); continue; }
            var token = await _meliAccounts.GetValidTokenAsync(acc);
            if (token is null) { err += grupo.Count(); mensajes.Add($"Token invalido para {acc.Nickname}"); continue; }

            using var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            http.Timeout = TimeSpan.FromSeconds(30);

            foreach (var mi in grupo)
            {
                if (ct.IsCancellationRequested) break;
                if (!prods.TryGetValue(mi.CafeProductoId!.Value, out var prod)) { err++; continue; }
                try
                {
                    var precioNeto = CalcularPrecioSistemaNeto(prod, mi.CafeFormato!, cfg);
                    if (precioNeto <= 0) { err++; mensajes.Add($"{mi.Sku}: precio sistema = 0"); continue; }

                    var precioMeli = Math.Round(precioNeto * IVA * mi.PriceRatioOverIva!.Value, 0);
                    var gramos = GramosPorFormato(mi.CafeFormato!);
                    var stockMeli = (int)Math.Floor(prod.StockGramos / gramos);
                    if (stockMeli < 0) stockMeli = 0;

                    var (success, msg) = await PushItemAsync(http, mi.MeliItemId, precioMeli, stockMeli);
                    if (success) { ok++; mi.Price = precioMeli; mi.AvailableQuantity = stockMeli; mi.LastUpdated = DateTime.UtcNow; }
                    else { err++; mensajes.Add($"{mi.Sku} ({mi.MeliItemId}): {msg}"); }

                    await Task.Delay(150, ct); // rate limit defensivo
                }
                catch (Exception ex)
                {
                    err++;
                    mensajes.Add($"{mi.Sku}: {ex.Message}");
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        return new PushResult(items.Count, ok, err, mensajes);
    }

    /// <summary>Pushea SOLO una publicación específica. Útil para piloto/testing antes de hacer el push masivo.</summary>
    public async Task<PushResult> PushSingleAsync(string meliItemId, CancellationToken ct = default)
    {
        var cfg = await _db.CafeSettings.FindAsync(new object[] { 1 }, ct) ?? new CafeSetting { Id = 1 };
        var mi = await _db.MeliItems
            .Include(x => x.MeliAccount)
            .FirstOrDefaultAsync(x => x.MeliItemId == meliItemId
                && x.CafeProductoId != null && x.CafeFormato != null
                && x.PriceRatioOverIva != null && x.PriceRatioOverIva > 0, ct);
        if (mi is null)
            return new PushResult(0, 0, 1, new() { $"{meliItemId}: no se encontró la publicación o no es un café linkeado con ratio." });

        var prod = await _db.CafeProductos.FindAsync(new object[] { mi.CafeProductoId!.Value }, ct);
        if (prod is null)
            return new PushResult(1, 0, 1, new() { $"{meliItemId}: producto café no encontrado en sistema." });

        if (mi.MeliAccount is null)
            return new PushResult(1, 0, 1, new() { $"{meliItemId}: cuenta MeLi no asociada." });

        var token = await _meliAccounts.GetValidTokenAsync(mi.MeliAccount);
        if (token is null)
            return new PushResult(1, 0, 1, new() { $"Token inválido para {mi.MeliAccount.Nickname}" });

        var precioNeto = CalcularPrecioSistemaNeto(prod, mi.CafeFormato!, cfg);
        if (precioNeto <= 0)
            return new PushResult(1, 0, 1, new() { $"{mi.Sku}: precio sistema = 0" });

        var precioMeli = Math.Round(precioNeto * IVA * mi.PriceRatioOverIva!.Value, 0);
        var gramos = GramosPorFormato(mi.CafeFormato!);
        var stockMeli = (int)Math.Floor(prod.StockGramos / gramos);
        if (stockMeli < 0) stockMeli = 0;

        using var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.Timeout = TimeSpan.FromSeconds(30);

        var (success, msg) = await PushItemAsync(http, mi.MeliItemId, precioMeli, stockMeli);
        if (success)
        {
            mi.Price = precioMeli;
            // Solo actualizamos stock local si efectivamente lo pusheamos (no en Full).
            var esFull = msg?.Contains("FULL", StringComparison.OrdinalIgnoreCase) == true;
            if (!esFull) mi.AvailableQuantity = stockMeli;
            mi.LastUpdated = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            var detalle = esFull
                ? $"⚠ {mi.Sku} en FULL: actualicé solo precio=${precioMeli} (MeLi no permite modificar stock de items en Full)"
                : $"✅ {mi.Sku}: precio=${precioMeli}, stock={stockMeli}";
            return new PushResult(1, 1, 0, new() { detalle });
        }
        return new PushResult(1, 0, 1, new() { $"❌ {mi.Sku}: {msg}" });
    }

    private async Task<(bool ok, string? msg)> PushItemAsync(HttpClient http, string meliItemId, decimal price, int stock)
    {
        // Leer el item para detectar logistic_type y variations.
        var getResp = await http.GetAsync($"https://api.mercadolibre.com/items/{meliItemId}");
        if (!getResp.IsSuccessStatusCode) return (false, $"GET {(int)getResp.StatusCode}");
        var json = await getResp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json).RootElement;

        // ¿Es Mercado Envíos Full? Si sí, MeLi NO permite modificar stock por API.
        var isFulfillment = false;
        if (doc.TryGetProperty("shipping", out var shipping) && shipping.ValueKind == JsonValueKind.Object
            && shipping.TryGetProperty("logistic_type", out var lt) && lt.ValueKind == JsonValueKind.String)
        {
            isFulfillment = string.Equals(lt.GetString(), "fulfillment", StringComparison.OrdinalIgnoreCase);
        }

        // Variations
        var variantIds = new List<long>();
        if (doc.TryGetProperty("variations", out var variations) && variations.ValueKind == JsonValueKind.Array && variations.GetArrayLength() > 0)
        {
            foreach (var v in variations.EnumerateArray())
                variantIds.Add(v.GetProperty("id").GetInt64());
        }

        // Construir payload: si es fulfillment, solo PRICE (sin stock).
        Dictionary<string, object> payload;
        if (variantIds.Count > 0)
        {
            payload = new Dictionary<string, object>
            {
                ["variations"] = variantIds.Select(id => isFulfillment
                    ? (object)new { id, price }
                    : (object)new { id, price, available_quantity = stock }).ToList()
            };
        }
        else
        {
            payload = isFulfillment
                ? new Dictionary<string, object> { ["price"] = price }
                : new Dictionary<string, object> { ["price"] = price, ["available_quantity"] = stock };
        }

        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = await http.PutAsync($"https://api.mercadolibre.com/items/{meliItemId}", body);
        if (resp.IsSuccessStatusCode)
        {
            return (true, isFulfillment ? "FULL: solo precio (stock no modificable)" : null);
        }
        var errBody = await resp.Content.ReadAsStringAsync();
        return (false, $"PUT {(int)resp.StatusCode}: {errBody.Substring(0, Math.Min(200, errBody.Length))}");
    }
}
