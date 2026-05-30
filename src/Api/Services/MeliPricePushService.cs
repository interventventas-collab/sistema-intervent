using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Api.Services;

/// <summary>
/// Push event-driven de PRECIO sistema → MeLi (paralelo a MeliStockPushService).
///
/// Regla "claimed" (2026-05-30):
///   - Una publicación queda "claimed" cuando se pushea precio por primera vez en forma
///     manual desde /publicaciones (botón 💵). Al claimearse se setea SyncPrecio=true.
///   - Solo las claimed reciben auto-push cuando cambia el precio del sistema. Las demás
///     siguen siendo "manual" y el operador decide cuándo pushear.
///
/// Fórmula del precio final = round(PrecioOtro × (1 + IvaPct/100), 2) ×
///                            (1 + AjustePct/100) + AjusteFijo
///                            luego redondeo hacia arriba según AjusteRedondeo.
///
/// Para publicaciones con variantes: MeLi obliga precio uniforme entre variantes, así que
/// pusheamos el mismo número a todas (igual que PushPrecioAjustado en MeliController).
/// </summary>
public class MeliPricePushService
{
    private readonly AppDbContext _db;
    private readonly MeliAccountService _accSvc;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MeliPricePushService> _logger;

    public MeliPricePushService(AppDbContext db, MeliAccountService accSvc,
        IHttpClientFactory httpFactory, ILogger<MeliPricePushService> logger)
    {
        _db = db;
        _accSvc = accSvc;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public record PushResult(bool Ok, string Message, decimal? PushedPrice = null, decimal? BasePrice = null);

    /// <summary>Pushea el precio de una publicación específica. Usada por el endpoint manual
    /// (que ahora también setea SyncPrecio=true para marcarla como "claimed") y por el
    /// auto-push event-driven. Si markAsClaimed=true (caso manual), marca SyncPrecio=true
    /// además de actualizar LastSyncAt.</summary>
    public async Task<PushResult> PushPrecioForItemAsync(int meliItemDbId, bool markAsClaimed = false, CancellationToken ct = default)
    {
        var item = await _db.MeliItems.Include(i => i.MeliAccount).FirstOrDefaultAsync(i => i.Id == meliItemDbId, ct);
        if (item is null) return new PushResult(false, "Item no encontrado");
        if (item.MeliAccount is null) return new PushResult(false, "Cuenta MeLi no cargada");
        if (item.Status == "closed" || item.Status == "deleted")
            return new PushResult(false, $"Publicación en estado '{item.Status}' — no se pushea");

        // 1. Calcular precio base del sistema (mismo cálculo que PushPrecioAjustado).
        var (precioBase, hasBase) = await CalcularPrecioBaseAsync(item, ct);
        if (!hasBase) return new PushResult(false, "No se pudo calcular precio base (sin PrecioOtro)");

        // 2. Aplicar ajuste configurado.
        var cfg = await _db.MeliItemSyncConfigs.FindAsync(new object[] { item.MeliItemId }, ct);
        var pct = cfg?.AjustePct ?? 0m;
        var fijo = cfg?.AjusteFijo ?? 0m;
        var redondeo = cfg?.AjusteRedondeo;
        var conAjuste = Math.Round(precioBase * (1 + pct / 100m) + fijo, 2);
        var precioFinal = AplicarRedondeoUp(conAjuste, redondeo);

        // 3. PUT a MeLi (detectar variantes).
        var token = await _accSvc.GetValidTokenAsync(item.MeliAccount);
        if (string.IsNullOrWhiteSpace(token)) return new PushResult(false, "Token MeLi inválido");

        using var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var getResp = await http.GetAsync($"https://api.mercadolibre.com/items/{item.MeliItemId}?attributes=variations", ct);
        if (!getResp.IsSuccessStatusCode)
            return new PushResult(false, $"GET fallido ({(int)getResp.StatusCode})");

        var getJson = await getResp.Content.ReadAsStringAsync(ct);
        var liveVariantIds = new List<long>();
        using (var doc = JsonDocument.Parse(getJson))
        {
            if (doc.RootElement.TryGetProperty("variations", out var vs) && vs.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in vs.EnumerateArray())
                    liveVariantIds.Add(v.GetProperty("id").GetInt64());
            }
        }

        object payload = liveVariantIds.Count > 0
            ? new { variations = liveVariantIds.Select(vId => new { id = vId, price = precioFinal }).ToList() }
            : (object)new { price = precioFinal };

        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = await http.PutAsync($"https://api.mercadolibre.com/items/{item.MeliItemId}", body, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("[PricePush] {Mla} rechazado: {Code} {Err}", item.MeliItemId, (int)resp.StatusCode, err);
            return new PushResult(false, $"MeLi rechazó ({(int)resp.StatusCode})");
        }

        // 4. Actualizar cache local + marcar claimed si es manual.
        item.Price = precioFinal;
        item.UpdatedAt = DateTime.UtcNow;
        if (cfg is null)
        {
            cfg = new MeliItemSyncConfig
            {
                MeliItemId = item.MeliItemId,
                SyncPrecio = markAsClaimed, // solo se claima si el push fue manual
                LastSyncAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };
            _db.MeliItemSyncConfigs.Add(cfg);
        }
        else
        {
            if (markAsClaimed) cfg.SyncPrecio = true;
            cfg.LastSyncAt = DateTime.UtcNow;
            cfg.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("[PricePush] {Mla} OK a ${Final} (claimed={Claimed})",
            item.MeliItemId, precioFinal, markAsClaimed || cfg.SyncPrecio);
        return new PushResult(true, $"Precio actualizado a ${precioFinal:N2}", precioFinal, precioBase);
    }

    /// <summary>Cuando cambia el precio de un producto del sistema, busca todas las publicaciones
    /// MeLi linkeadas (directas o vía componentes) que están "claimed" (SyncPrecio=true) y
    /// les hace push automático. Las no-claimed se ignoran. Llamado desde
    /// CafeProductosController fire-and-forget al editar precio.</summary>
    public async Task<int> PushPrecioForProductoAsync(int cafeProductoId, CancellationToken ct = default)
    {
        // Linkeo directo (item.CafeProductoId)
        var itemsDirectos = await _db.MeliItems
            .Where(i => i.CafeProductoId == cafeProductoId
                && (i.Status == "active" || i.Status == "paused"))
            .Select(i => i.Id)
            .ToListAsync(ct);

        // Linkeo vía componentes
        var meliItemIdsViaComp = await _db.MeliItemComponentes
            .Where(c => c.CafeProductoId == cafeProductoId)
            .Select(c => c.MeliItemId)
            .Distinct()
            .ToListAsync(ct);

        List<int> itemsViaComp = new();
        if (meliItemIdsViaComp.Count > 0)
        {
            itemsViaComp = await _db.MeliItems
                .Where(i => meliItemIdsViaComp.Contains(i.MeliItemId)
                    && (i.Status == "active" || i.Status == "paused"))
                .Select(i => i.Id)
                .ToListAsync(ct);
        }

        var allItemIds = itemsDirectos.Concat(itemsViaComp).Distinct().ToList();
        if (allItemIds.Count == 0) return 0;

        // Filtrar solo los "claimed" (SyncPrecio=true)
        var claimedItems = await _db.MeliItems
            .Where(i => allItemIds.Contains(i.Id))
            .Join(_db.MeliItemSyncConfigs.Where(c => c.SyncPrecio),
                  i => i.MeliItemId, c => c.MeliItemId,
                  (i, c) => i.Id)
            .ToListAsync(ct);

        if (claimedItems.Count == 0)
        {
            _logger.LogDebug("[PricePush] Producto {Pid}: {Total} items linkeados, ninguno claimed",
                cafeProductoId, allItemIds.Count);
            return 0;
        }

        int ok = 0;
        foreach (var itemId in claimedItems)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var r = await PushPrecioForItemAsync(itemId, markAsClaimed: false, ct);
                if (r.Ok) ok++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PricePush] Falló item {ItemId}", itemId);
            }
        }
        _logger.LogInformation("[PricePush] Producto {Pid}: {Ok}/{Total} publicaciones claimed pusheadas",
            cafeProductoId, ok, claimedItems.Count);
        return ok;
    }

    /// <summary>Backup: busca productos con PriceChangedAt reciente que tengan publicaciones
    /// claimed sin actualizar (LastSyncAt < PriceChangedAt). Procesa hasta maxProductos.
    /// Usado por MeliPricePushBackgroundService cada 15 min.</summary>
    public async Task<(int Procesados, int Ok)> PushPendingPrecioAsync(int maxProductos = 100, CancellationToken ct = default)
    {
        var candidatos = await _db.CafeProductos
            .Where(p => p.PriceChangedAt != null)
            .OrderBy(p => p.PriceChangedAt) // procesar los más viejos primero
            .Take(maxProductos)
            .Select(p => p.Id)
            .ToListAsync(ct);

        int procesados = 0, ok = 0;
        foreach (var pid in candidatos)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var r = await PushPrecioForProductoAsync(pid, ct);
                procesados++;
                ok += r;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PricePush bg] Producto {Pid} falló", pid);
            }
        }
        return (procesados, ok);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Calcula el precio base del sistema para un MeliItem (sin ajuste).
    /// Mirror de la lógica en MeliController.PushPrecioAjustado.</summary>
    private async Task<(decimal Price, bool Found)> CalcularPrecioBaseAsync(MeliItem item, CancellationToken ct)
    {
        if (item.CafeProductoId.HasValue)
        {
            var p = await _db.CafeProductos.FindAsync(new object[] { item.CafeProductoId.Value }, ct);
            if (p?.PrecioOtro is decimal po && po > 0)
            {
                return (Math.Round(po * (1 + p.IvaPct / 100m), 2), true);
            }
            return (0m, false);
        }

        var comps = await _db.MeliItemComponentes.Where(c => c.MeliItemId == item.MeliItemId).ToListAsync(ct);
        var compsForItem = comps.Where(c =>
        {
            if (!string.IsNullOrEmpty(item.VariationId))
                return c.MeliVariationId == item.VariationId || string.IsNullOrEmpty(c.MeliVariationId);
            return string.IsNullOrEmpty(c.MeliVariationId);
        }).ToList();
        if (compsForItem.Count == 0) compsForItem = comps;
        if (compsForItem.Count == 0) return (0m, false);

        var prodIds = compsForItem.Select(c => c.CafeProductoId).Distinct().ToList();
        var prods = await _db.CafeProductos.Where(p => prodIds.Contains(p.Id))
            .Select(p => new { p.Id, p.PrecioOtro, p.IvaPct }).ToDictionaryAsync(p => p.Id, ct);
        decimal sum = 0m;
        decimal iva = 21m;
        bool any = false;
        foreach (var c in compsForItem)
        {
            if (!prods.TryGetValue(c.CafeProductoId, out var pc)) continue;
            if (!pc.PrecioOtro.HasValue || pc.PrecioOtro.Value <= 0) continue;
            sum += pc.PrecioOtro.Value * c.Cantidad;
            if (!any) { iva = pc.IvaPct; any = true; }
        }
        if (!any) return (0m, false);
        return (Math.Round(sum * (1 + iva / 100m), 2), true);
    }

    private static decimal AplicarRedondeoUp(decimal valor, string? modo)
    {
        if (string.IsNullOrEmpty(modo) || valor <= 0) return valor;
        return modo switch
        {
            "99" => RoundUpToEnding(valor, 100m, 99m),
            "999" => RoundUpToEnding(valor, 1000m, 999m),
            "000" => Math.Ceiling(valor / 1000m) * 1000m,
            _ => valor
        };
    }

    private static decimal RoundUpToEnding(decimal valor, decimal unidad, decimal ending)
    {
        var lower = Math.Floor(valor / unidad) * unidad + ending;
        return lower >= valor ? lower : lower + unidad;
    }
}
