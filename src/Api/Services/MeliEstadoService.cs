using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-27 — Pausar y activar una publicación desde la fila de la pantalla nueva.
///
/// Idea de Osmar: el cartelito que dice "Activa" / "Pausada" es lo último de cada fila, y hasta hoy
/// sólo informaba. Ahora se toca y cambia el estado — la cosa y el botón para cambiarla son lo mismo.
///
/// Por qué vale la pena, más allá de la comodidad: **hasta hoy no había forma de pausar**. Para
/// frenar una publicación se le colgaba el SKU trucho `PAUSAR` (un producto con costo $1.500.000),
/// que la sacaba de circulación por precio — y de paso dejaba precios de $4.392.300 ensuciando
/// todos los informes. Con esto ese truco deja de hacer falta.
///
/// ⚠ Acá NO se elimina nada. Eliminar en MeLi es irreversible y no se pierde sólo la publicación:
/// se pierde la antigüedad, el historial de ventas, las preguntas y la posición en el buscador.
/// Pausada tampoco vende, y se puede volver. Si algún día se agrega, va de a UNA y con confirmación
/// escrita — nunca en lote.
/// </summary>
public class MeliEstadoService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly MeliStockPushService _stockPush;
    private readonly MeliPricePushService _pricePush;
    private readonly ILogger<MeliEstadoService> _logger;

    public MeliEstadoService(AppDbContext db, IHttpClientFactory httpFactory,
        MeliAccountService accountService, MeliStockPushService stockPush,
        MeliPricePushService pricePush, ILogger<MeliEstadoService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _accountService = accountService;
        _stockPush = stockPush;
        _pricePush = pricePush;
        _logger = logger;
    }

    public record Resultado(bool Ok, string Mensaje, string? EstadoNuevo, string? Detalle);

    /// <summary>TOCA MELI: deja de venderse. No se pierde nada, se puede volver a activar.</summary>
    public async Task<Resultado> PausarAsync(string meliItemId, CancellationToken ct = default)
    {
        var (item, error) = await BuscarAsync(meliItemId, ct);
        if (error is not null) return error;

        if (item!.Status == "paused")
            return new Resultado(true, "Ya estaba pausada.", "paused", null);

        var (token, sinToken) = await TokenAsync(item, ct);
        if (sinToken is not null) return sinToken;

        var (ok, err) = await MandarAsync(token!, meliItemId, new { status = "paused" }, ct);
        if (!ok) return new Resultado(false, err!, null, null);

        await MarcarEstadoAsync(meliItemId, "paused", ct);
        _logger.LogWarning("[Estado] {Mla} PAUSADA a mano", meliItemId);
        return new Resultado(true, "Pausada. Deja de venderse, pero no se pierde nada: podés activarla cuando quieras.",
            "paused", null);
    }

    /// <summary>TOCA MELI: vuelve a venderse. Además le manda el stock real y, si tiene objetivo de
    /// ganancia con el sincro prendido, le aplica el precio antes de que alguien le compre.</summary>
    public async Task<Resultado> ActivarAsync(string meliItemId, CancellationToken ct = default)
    {
        var (item, error) = await BuscarAsync(meliItemId, ct);
        if (error is not null) return error;

        if (item!.Status == "active")
            return new Resultado(true, "Ya estaba activa.", "active", null);

        var (token, sinToken) = await TokenAsync(item, ct);
        if (sinToken is not null) return sinToken;

        // ⚠ MeLi NO deja activar con stock 0 (item_status_invalid): hay que mandar una cantidad
        // junto con el status. Se pone 1 provisorio y enseguida se pushea el stock REAL de abajo.
        // Si tiene variantes, la cantidad va por variante o lo rechaza igual.
        object payload = new { status = "active", available_quantity = 1 };
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            var get = await http.GetAsync($"https://api.mercadolibre.com/items/{meliItemId}?attributes=variations", ct);
            if (get.IsSuccessStatusCode)
            {
                var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync(ct)).RootElement;
                if (doc.TryGetProperty("variations", out var vars)
                    && vars.ValueKind == JsonValueKind.Array && vars.GetArrayLength() > 0)
                {
                    var lista = new List<object>();
                    foreach (var v in vars.EnumerateArray())
                    {
                        var vid = v.GetProperty("id").GetInt64();
                        var q = v.TryGetProperty("available_quantity", out var qq) && qq.ValueKind == JsonValueKind.Number
                            ? qq.GetInt32() : 0;
                        lista.Add(new { id = vid, available_quantity = q > 0 ? q : 1 });
                    }
                    payload = new { status = "active", variations = lista };
                }
            }
        }
        catch { /* si el GET falla se sigue con el payload simple */ }

        var (ok, err) = await MandarAsync(token!, meliItemId, payload, ct);
        if (!ok) return new Resultado(false, err!, null, null);

        await MarcarEstadoAsync(meliItemId, "active", ct);

        // Mientras estuvo pausada nadie le tocó el stock ni el precio. Se ponen al día ahora,
        // antes de que alguien le compre con el stock provisorio de 1 o con un precio viejo.
        var detalle = new List<string>();
        try
        {
            var r = await _stockPush.PushStockForMeliItemsAsync(new List<string> { meliItemId }, ct);
            var msg = r.Mensajes.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(msg)) detalle.Add(msg!);
        }
        catch (Exception ex) { detalle.Add("⚠ activada, pero el stock no se pudo mandar: " + ex.Message); }

        try
        {
            var cfg = await _db.MeliItemSyncConfigs.AsNoTracking()
                .FirstOrDefaultAsync(c => c.MeliItemId == meliItemId, ct);
            if (cfg?.GananciaObjetivoPct is decimal obj && obj > 0 && cfg.SyncPrecio)
            {
                var pr = await _pricePush.PushPrecioForItemAsync(item.Id, markAsClaimed: false, ct);
                detalle.Add(pr.Ok
                    ? $"🎯 objetivo {obj:0.#}% aplicado → ${pr.PushedPrice:N0}"
                    : $"⚠ no se pudo aplicar el objetivo: {pr.Message}");
            }
        }
        catch (Exception ex) { detalle.Add("⚠ objetivo: " + ex.Message); }

        _logger.LogWarning("[Estado] {Mla} ACTIVADA a mano · {Detalle}", meliItemId, string.Join(" · ", detalle));
        return new Resultado(true, "Activada. Ya vuelve a venderse.", "active",
            detalle.Count > 0 ? string.Join(" · ", detalle) : null);
    }

    // ─── 2026-08-27 · devolver el SKU que se había perdido al marcarla ───

    /// <summary>TOCA MELI, pero SÓLO el SKU: le devuelve a la publicación el SKU que tenía antes de
    /// que la marcaran para revisar.
    ///
    /// ⚠ **NO la activa y NO le toca el precio.** Queda pausada igual que estaba. Activarla es una
    /// decisión aparte y sigue siendo del usuario, con el cartel de estado de la fila — que además
    /// avisa qué margen deja antes de despertarla. Osmar fue explícito: nada se reactiva solo.</summary>
    public async Task<Resultado> DevolverSkuAsync(string meliItemId, CancellationToken ct = default)
    {
        var (item, error) = await BuscarAsync(meliItemId, ct);
        if (error is not null) return error;

        var cfg = await _db.MeliItemSyncConfigs.FirstOrDefaultAsync(c => c.MeliItemId == meliItemId, ct);
        var skuViejo = cfg?.SkuAnterior?.Trim();
        if (string.IsNullOrWhiteSpace(skuViejo))
            return new Resultado(false, "De esta publicación no tengo guardado el SKU anterior.", null, null);

        if (string.Equals(item!.Sku?.Trim(), skuViejo, StringComparison.OrdinalIgnoreCase))
        {
            // Ya lo tiene puesto: limpiar la marca y listo, sin molestar a MeLi.
            cfg!.SkuAnterior = null;
            cfg.SkuAnteriorAt = null;
            cfg.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return new Resultado(true, $"Ya tenía el SKU {skuViejo}.", item.Status, null);
        }

        var (token, sinToken) = await TokenAsync(item, ct);
        if (sinToken is not null) return sinToken;

        // Los dos campos: seller_custom_field para las categorías viejas y el atributo SELLER_SKU
        // para las nuevas. Es el mismo par que ya usa el cambio de SKU de los combos.
        var payload = new
        {
            seller_custom_field = skuViejo,
            attributes = new[] { new { id = "SELLER_SKU", value_name = skuViejo } }
        };

        var (ok, err) = await MandarAsync(token!, meliItemId, payload, ct);
        if (!ok) return new Resultado(false, err!, null, null);

        // Reflejarlo en nuestra copia y soltar la marca: ya cumplió su función.
        var filas = await _db.MeliItems.Where(i => i.MeliItemId == meliItemId).ToListAsync(ct);
        foreach (var f in filas) { f.Sku = skuViejo; f.UpdatedAt = DateTime.UtcNow; }
        cfg!.SkuAnterior = null;
        cfg.SkuAnteriorAt = null;
        cfg.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning("[SkuMarca] {Mla}: SKU devuelto a «{Sku}» (sigue {Estado})",
            meliItemId, skuViejo, item.Status);

        return new Resultado(true,
            $"Listo, le devolví el SKU {skuViejo}. Sigue {(item.Status == "paused" ? "pausada" : "activa")}: " +
            "no la desperté. Cuando quieras, activala con el cartel de la derecha.",
            item.Status, null);
    }

    // ── auxiliares ──

    /// <summary>Busca la publicación y descarta los casos que no tiene sentido intentar.
    /// NO toca la red: pedir el token puede disparar un refresh contra MeLi, y hacerlo para después
    /// descubrir que la publicación ya estaba en el estado pedido es gasto al pedo.</summary>
    private async Task<(Models.MeliItem? Item, Resultado? Error)> BuscarAsync(
        string meliItemId, CancellationToken ct)
    {
        var item = await _db.MeliItems.Include(i => i.MeliAccount)
            .FirstOrDefaultAsync(i => i.MeliItemId == meliItemId && i.VariationId == null, ct);

        if (item?.MeliAccount is null)
            return (null, new Resultado(false, "No encuentro esta publicación en el sistema.", null, null));

        // Cerrada o borrada en MeLi no vuelve.
        if (item.Status is "closed" or "deleted")
            return (null, new Resultado(false,
                $"La publicación está {(item.Status == "closed" ? "cerrada" : "borrada")} en MercadoLibre y no se puede cambiar.",
                null, null));

        return (item, null);
    }

    private async Task<(string? Token, Resultado? Error)> TokenAsync(Models.MeliItem item, CancellationToken ct)
    {
        var token = await _accountService.GetValidTokenAsync(item.MeliAccount!);
        return string.IsNullOrWhiteSpace(token)
            ? (null, new Resultado(false, "Sin token de MercadoLibre. Reconectá la cuenta.", null, null))
            : (token, null);
    }

    private async Task<(bool Ok, string? Error)> MandarAsync(string token, string meliItemId, object payload,
        CancellationToken ct)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = await http.PutAsync($"https://api.mercadolibre.com/items/{meliItemId}", body, ct);
        if (resp.IsSuccessStatusCode) return (true, null);

        var err = await resp.Content.ReadAsStringAsync(ct);
        _logger.LogWarning("[Estado] {Mla} rechazado: {Code} {Err}", meliItemId, (int)resp.StatusCode, err);

        // Los dos rechazos que de verdad pasan, dichos en castellano.
        var amable = err.Contains("under_review") || err.Contains("not_modifiable")
            ? "MercadoLibre tiene esta publicación en revisión y no deja cambiarla."
            : err.Contains("item_status_invalid")
                ? "MercadoLibre no deja cambiarle el estado a esta publicación (suele pasar si no tiene stock cargado)."
                : $"MercadoLibre rechazó el cambio ({(int)resp.StatusCode}).";
        return (false, amable);
    }

    /// <summary>Refleja el estado en nuestra copia. Van TODAS las filas del MLA (las variantes
    /// se guardan como filas aparte y si no quedan con el estado viejo).</summary>
    private async Task MarcarEstadoAsync(string meliItemId, string estado, CancellationToken ct)
    {
        var filas = await _db.MeliItems.Where(i => i.MeliItemId == meliItemId).ToListAsync(ct);
        foreach (var f in filas) { f.Status = estado; f.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync(ct);
    }
}
