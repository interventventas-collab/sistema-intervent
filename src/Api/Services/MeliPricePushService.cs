using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.DTOs;
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
    private readonly MeliItemService _itemService;

    public MeliPricePushService(AppDbContext db, MeliAccountService accSvc,
        IHttpClientFactory httpFactory, ILogger<MeliPricePushService> logger,
        MeliItemService itemService)
    {
        _db = db;
        _accSvc = accSvc;
        _httpFactory = httpFactory;
        _logger = logger;
        _itemService = itemService;
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

        // 2. Determinar el precio final.
        //    2026-07-13: si la publicación tiene un OBJETIVO de ganancia cargado (cfg.GananciaObjetivoPct),
        //    el precio se calcula para dejar ese % sobre costo — PERO nunca por debajo del sugerido del
        //    sistema (precioBase = piso). Es decir: precio = MAX(precio_para_tu_objetivo, sugerido).
        //    Si NO hay objetivo, se usa el comportamiento histórico: precioBase + ajuste configurado.
        var cfg = await _db.MeliItemSyncConfigs.FindAsync(new object[] { item.MeliItemId }, ct);
        decimal precioFinal;
        if (cfg?.GananciaObjetivoPct is decimal objetivoPct && objetivoPct > 0)
        {
            var precioObjetivo = await CalcularPrecioParaGananciaAsync(item, objetivoPct, ct);
            // El objetivo solo puede SUBIR desde el piso sugerido; nunca lo baja.
            var elegido = (precioObjetivo.HasValue && precioObjetivo.Value > precioBase)
                ? precioObjetivo.Value
                : precioBase;
            precioFinal = AplicarRedondeoUp(elegido, cfg.AjusteRedondeo);
        }
        else
        {
            var pct = cfg?.AjustePct ?? 0m;
            var fijo = cfg?.AjusteFijo ?? 0m;
            var redondeo = cfg?.AjusteRedondeo;
            var conAjuste = Math.Round(precioBase * (1 + pct / 100m) + fijo, 2);
            precioFinal = AplicarRedondeoUp(conAjuste, redondeo);
        }

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
    /// 2026-05-30: respeta el modelo OEM. Si un producto tiene OemId y el OEM tiene PvpConIva,
    /// el precio = OEM.PvpConIva × MultiplicadorOem (default 1). Si no, PrecioOtro × (1 + IvaPct/100).
    /// 2026-07-01: público para que el endpoint bulk-precio-por-ganancia lo reuse.</summary>
    public async Task<(decimal Price, bool Found)> CalcularPrecioBaseAsync(MeliItem item, CancellationToken ct)
    {
        // Helper local que devuelve precio c/IVA de un producto, respetando OEM.
        async Task<decimal?> PrecioCIvaAsync(int prodId)
        {
            var p = await _db.CafeProductos.FindAsync(new object[] { prodId }, ct);
            if (p is null) return null;
            if (p.OemId.HasValue)
            {
                var oem = await _db.CafeOems.FindAsync(new object[] { p.OemId.Value }, ct);
                if (oem?.PvpConIva is decimal pvp && pvp > 0)
                {
                    var mult = p.MultiplicadorOem ?? 1m;
                    if (mult <= 0) mult = 1m;
                    return Math.Round(pvp * mult, 2);
                }
            }
            if (p.PrecioOtro is decimal po && po > 0)
                return Math.Round(po * (1 + p.IvaPct / 100m), 2);
            return null;
        }

        if (item.CafeProductoId.HasValue)
        {
            var price = await PrecioCIvaAsync(item.CafeProductoId.Value);
            return price.HasValue ? (price.Value, true) : (0m, false);
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

        decimal sum = 0m;
        bool any = false;
        foreach (var c in compsForItem)
        {
            var pCIva = await PrecioCIvaAsync(c.CafeProductoId);
            if (pCIva == null) continue;
            sum += pCIva.Value * c.Cantidad;
            any = true;
        }
        if (!any) return (0m, false);
        return (Math.Round(sum, 2), true);
    }

    /// <summary>2026-07-13: precio necesario para que ESTA publicación deje `gananciaPct`% sobre costo,
    /// usando la comisión real de la publicación (misma fórmula que el bulk-precio-por-ganancia del
    /// MeliController). Contempla que la parte % de la comisión escala con el precio y el cargo fijo no.
    /// Devuelve null si no hay costo cargado o la comisión es imposible (>95%).</summary>
    public async Task<decimal?> CalcularPrecioParaGananciaAsync(MeliItem item, decimal gananciaPct, CancellationToken ct = default)
    {
        var costo = await CalcularCostoTotalAsync(item, ct);
        if (costo is null || costo.Value <= 0) return null;

        // 2026-07-13: traer costos EN VIVO de MeLi (comisión desglosada + ENVÍO a cargo del vendedor + listing fee).
        // Antes esta cuenta NO contaba el envío → en productos grandes el precio mantenido salía mal (daba
        // distinto que "Aplicar una vez y pushear"). Ahora usa la MISMA fórmula del simulador de la ficha
        // (CalcPrecioCrudo del frontend). GetListingCostsAsync además refresca la comisión cacheada del item.
        ListingCostDto lc;
        try { lc = await _itemService.GetListingCostsAsync(item.MeliItemId); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PricePush] No se pudieron traer costos en vivo de {Mla} para el objetivo — gana el piso", item.MeliItemId);
            return null; // sin costos en vivo no arriesgamos un precio mal calculado → gana el piso
        }

        var price = lc.Price > 0 ? lc.Price : item.Price;
        if (price <= 0) return null;

        // % que escalan con el precio: comisión variable (sin el cargo fijo) + financiación de cuotas.
        var pctEscalable = (lc.SaleFeeAmount - lc.FixedFee + lc.FinancingFee) / price;
        var denom = 1m - pctEscalable;
        if (denom <= 0.05m) return null;

        var netoConIvaNec = costo.Value * (1 + gananciaPct / 100m) * 1.21m;
        var envio = lc.ShippingCost + lc.ListingFeeAmount;   // ← el ENVÍO que antes faltaba
        var fijoActual = lc.FixedFee;

        // Igual que el frontend: si el precio resultante queda alto (>= $30.000) MeLi no cobra cargo fijo.
        var pSinFijo = (netoConIvaNec + envio) / denom;
        if (pSinFijo >= 30000m) return Math.Round(pSinFijo, 2);
        var pConFijo = (netoConIvaNec + envio + fijoActual) / denom;
        return Math.Round(pConFijo, 2);
    }

    /// <summary>2026-07-01: costo total del producto/combo linkeado a un MeliItem, mismo cálculo
    /// que el endpoint /product-cost del controller. Usado por el bulk-precio-por-ganancia.</summary>
    public async Task<decimal?> CalcularCostoTotalAsync(MeliItem mi, CancellationToken ct)
    {
        // 1) Modelo nuevo: MeliItemComponentes
        var mecs = await (
            from c in _db.MeliItemComponentes
            join p in _db.CafeProductos on c.CafeProductoId equals p.Id
            where c.MeliItemId == mi.MeliItemId
            select new { p.Sku, p.Costo, c.Cantidad }
        ).ToListAsync(ct);
        if (mecs.Count > 0)
        {
            // Dedup por SKU (misma lógica que /product-cost)
            var uniq = mecs.GroupBy(x => x.Sku).Select(g => g.First()).ToList();
            return uniq.Sum(x => x.Costo * x.Cantidad);
        }
        // 2) Legacy: combo directo
        if (mi.CafeComboId.HasValue)
        {
            var items = await (
                from ci in _db.CafeComboItems
                join p in _db.CafeProductos on ci.ProductoId equals p.Id
                where ci.ComboId == mi.CafeComboId.Value
                select new { p.Costo, ci.Cantidad }
            ).ToListAsync(ct);
            return items.Sum(x => x.Costo * x.Cantidad);
        }
        // 3) Legacy: producto directo
        if (mi.CafeProductoId.HasValue)
        {
            var p = await _db.CafeProductos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mi.CafeProductoId.Value, ct);
            if (p is null) return null;
            decimal cant = 1m;
            if (!string.IsNullOrEmpty(mi.Sku))
            {
                if (mi.Sku.EndsWith(".4")) cant = 0.25m;
                else if (mi.Sku.EndsWith(".2")) cant = 0.5m;
            }
            return p.Costo * cant;
        }
        return null;
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
