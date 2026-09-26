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

    public record PushResult(bool Ok, string Message, decimal? PushedPrice = null, decimal? BasePrice = null)
    {
        /// <summary>No se mandó porque con soloSubir el precio no subía.</summary>
        public bool NoSube { get; init; }
    }

    /// <summary>Pushea el precio de una publicación específica. Usada por el endpoint manual
    /// (que ahora también setea SyncPrecio=true para marcarla como "claimed") y por el
    /// auto-push event-driven. Si markAsClaimed=true (caso manual), marca SyncPrecio=true
    /// además de actualizar LastSyncAt.</summary>
    /// <param name="soloSubir">2026-09-26: si el precio calculado NO es mayor al actual, no se manda nada
    /// (lo usa el vigilante de la noche: la primera noche bajó 13 precios solo, una mesa de $74.843 a
    /// $32.999). Devuelve Ok=false con NoSube=true y el precio que habría puesto en BasePrice.</param>
    public async Task<PushResult> PushPrecioForItemAsync(int meliItemDbId, bool markAsClaimed = false, CancellationToken ct = default,
        bool soloSubir = false)
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
            var ganaObjetivo = precioObjetivo.HasValue && precioObjetivo.Value > precioBase;
            var elegido = ganaObjetivo ? precioObjetivo!.Value : precioBase;
            // 2026-09-22 — Osmar: el precio que calcula el sistema quedaba con centavos ($44.175,94).
            // Si la publicación no tiene un redondeo propio, el del objetivo sube hasta terminar en 99
            // (nunca baja: el % queda igual o un poco más). El piso OEM se publica tal cual.
            var modoRedondeo = cfg.AjusteRedondeo;
            if (string.IsNullOrEmpty(modoRedondeo) && ganaObjetivo) modoRedondeo = "99";
            precioFinal = AplicarRedondeoUp(elegido, modoRedondeo);
        }
        else
        {
            var pct = cfg?.AjustePct ?? 0m;
            var fijo = cfg?.AjusteFijo ?? 0m;
            var redondeo = cfg?.AjusteRedondeo;
            var conAjuste = Math.Round(precioBase * (1 + pct / 100m) + fijo, 2);
            precioFinal = AplicarRedondeoUp(conAjuste, redondeo);
        }

        // 2.5) 2026-07-14: CANDADO DE SEGURIDAD — nunca pushear un precio absurdo a MeLi. Un error de datos
        //    (multiplicador OEM mal cargado, comisión/envío raros que devuelve MeLi, división por casi-cero)
        //    puede disparar el precio a millones. Si el precio calculado queda fuera de rango, se FRENA y NO
        //    se manda a MeLi — se marca como error para revisar. (Los productos reales no superan unos cientos de miles.)
        const decimal TopePrecioSeguro = 2_000_000m;
        if (precioFinal <= 0 || precioFinal > TopePrecioSeguro)
        {
            _logger.LogError("[PricePush] ⛔ CANDADO: {Mla} precio calculado ${Precio} fuera de rango (tope ${Tope}) — NO se pushea. Revisar costo/multiplicador/comisión.",
                item.MeliItemId, precioFinal, TopePrecioSeguro);
            return new PushResult(false, $"⛔ Precio ${precioFinal:N0} frenado por seguridad (fuera de rango razonable). Revisá el costo / multiplicador de esta publicación antes de pushear.");
        }

        if (soloSubir && precioFinal <= item.Price)
            return new PushResult(false, precioFinal < item.Price
                ? $"El sistema lo dejaría en ${precioFinal:N0} (más bajo que ${item.Price:N0}): de noche no se baja solo."
                : "El precio que calcula el sistema es el mismo que ya tiene.", BasePrice: precioFinal) { NoSube = true };

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

        // 2026-08-26 — RECAPTURAR LA COMISIÓN. Sin esto quedaba la del precio VIEJO y el margen
        // que mostraba el sistema era mentira: caso real, Mesita Bambini subió de $42.000 a
        // $100.552 y siguió con la comisión de $5.880 (14% de $42.000) → mostraba 89,3% de margen
        // cuando el real era 50%. Le pasó a las 848 publicaciones del piso masivo del 25/08.
        // La comisión de MeLi escala con el precio, así que cambiar uno sin el otro deja el
        // margen roto hasta el refresco nocturno.
        try
        {
            await _itemService.RefreshSaleFeeAsync(item.MeliItemId);
        }
        catch (Exception ex)
        {
            // Si falla, el precio ya quedó bien igual — la comisión la corrige el refresco nocturno.
            _logger.LogWarning(ex, "[PricePush] {Mla}: precio OK pero no se pudo recapturar la comisión", item.MeliItemId);
        }

        _logger.LogInformation("[PricePush] {Mla} OK a ${Final} (claimed={Claimed})",
            item.MeliItemId, precioFinal, markAsClaimed || cfg.SyncPrecio);
        return new PushResult(true, $"Precio actualizado a ${precioFinal:N2}", precioFinal, precioBase);
    }

    /// <summary>Cuando cambia el precio de un producto del sistema, busca todas las publicaciones
    /// MeLi linkeadas (directas o vía componentes) que están "claimed" (SyncPrecio=true) y
    /// les hace push automático. Las no-claimed se ignoran. Llamado desde
    /// CafeProductosController fire-and-forget al editar precio.</summary>
    /// <param name="soloPendientesAntesDe">Si viene (lo usa el job de respaldo), solo pushea las publicaciones cuyo
    /// último push de precio (LastSyncAt) es anterior a esa fecha — las que ya recibieron el precio nuevo se saltean.</param>
    public async Task<int> PushPrecioForProductoAsync(int cafeProductoId, CancellationToken ct = default,
        DateTime? soloPendientesAntesDe = null)
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
            .Join(_db.MeliItemSyncConfigs.Where(c => c.SyncPrecio
                    && (soloPendientesAntesDe == null || c.LastSyncAt == null || c.LastSyncAt < soloPendientesAntesDe)),
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
    /// claimed sin actualizar (LastSyncAt < PriceChangedAt) y pushea SOLO esas. Procesa hasta maxProductos.
    /// Usado por MeliPricePushBackgroundService cada 15 min.
    ///
    /// 2026-09-15 — antes tomaba los 100 productos con PriceChangedAt más viejo SIN mirar si ya se habían
    /// pusheado (nadie limpia PriceChangedAt). Resultado en prod: cada ciclo reenviaba el mismo precio a las
    /// mismas ~352 publicaciones (268 recibieron el mismo precio 5 veces en 2 horas) y los productos que
    /// cambiaban después nunca entraban. Tocar el precio en MeLi puede tumbar descuentos de promociones.
    /// Ahora: solo entra un producto si alguna publicación claimed quedó atrás de su cambio de precio, y
    /// solo se reintenta durante VentanaReintento (una publicación que MeLi rechaza no se martilla para siempre).</summary>
    public async Task<(int Procesados, int Ok)> PushPendingPrecioAsync(int maxProductos = 100, CancellationToken ct = default)
    {
        // Un lote grande (importar OEMs por Excel) puede tardar mucho: mientras corre, los que le faltan figuran
        // como pendientes y el respaldo los mandaria dos veces. Se espera al ciclo siguiente.
        if (Volatile.Read(ref _lotesEnCurso) > 0)
        {
            _logger.LogInformation("[PricePush bg] Hay un lote de precios mandandose — este ciclo no hace nada");
            return (0, 0);
        }

        var ahora = DateTime.UtcNow;
        var desde = ahora - VentanaReintento;
        // Los cambios de los ultimos minutos los esta mandando el push en el momento; no pisarlo.
        var hasta = ahora - EsperaPushEnElMomento;
        var candidatos = await _db.CafeProductos
            .Where(p => p.PriceChangedAt != null && p.PriceChangedAt >= desde && p.PriceChangedAt <= hasta)
            .Where(p =>
                // linkeo directo
                _db.MeliItems.Any(i => i.CafeProductoId == p.Id
                    && (i.Status == "active" || i.Status == "paused")
                    && _db.MeliItemSyncConfigs.Any(c => c.MeliItemId == i.MeliItemId && c.SyncPrecio
                        && (c.LastSyncAt == null || c.LastSyncAt < p.PriceChangedAt)))
                // linkeo vía componentes
                || _db.MeliItemComponentes.Any(mc => mc.CafeProductoId == p.Id
                    && _db.MeliItems.Any(i => i.MeliItemId == mc.MeliItemId
                        && (i.Status == "active" || i.Status == "paused"))
                    && _db.MeliItemSyncConfigs.Any(c => c.MeliItemId == mc.MeliItemId && c.SyncPrecio
                        && (c.LastSyncAt == null || c.LastSyncAt < p.PriceChangedAt))))
            .OrderBy(p => p.PriceChangedAt) // procesar los más viejos primero
            .Take(maxProductos)
            .Select(p => new { p.Id, p.PriceChangedAt })
            .ToListAsync(ct);

        int procesados = 0, ok = 0;
        foreach (var c in candidatos)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var r = await PushPrecioForProductoAsync(c.Id, ct, soloPendientesAntesDe: c.PriceChangedAt);
                procesados++;
                ok += r;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PricePush bg] Producto {Pid} falló", c.Id);
            }
        }
        return (procesados, ok);
    }

    /// <summary>Cuánto tiempo sigue reintentando el job de respaldo un cambio de precio que no llegó a MeLi.</summary>
    private static readonly TimeSpan VentanaReintento = TimeSpan.FromHours(48);

    /// <summary>Cuánto espera el respaldo antes de tocar un cambio reciente (el push en el momento todavía puede estar corriendo).</summary>
    private static readonly TimeSpan EsperaPushEnElMomento = TimeSpan.FromMinutes(10);

    private static int _lotesEnCurso;

    /// <summary>Marca que hay un lote de pushes de precio corriendo (hasta que se haga Dispose). Mientras tanto
    /// el job de respaldo no hace nada, para no mandar dos veces el mismo precio.</summary>
    public static IDisposable MarcarLoteEnCurso()
    {
        Interlocked.Increment(ref _lotesEnCurso);
        return new LoteEnCurso();
    }

    private sealed class LoteEnCurso : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref _lotesEnCurso);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>2026-07-14: si el MeliItem está linkeado a un COMBO compuesto (EsCompuesto=true) con OEM,
    /// devuelve el OEM y su multiplicador. En un compuesto (caja+tapa) el PRECIO y el COSTO salen del OEM del
    /// producto COMPLETO (× MultiplicadorOem), NO de la suma de las piezas — igual que la venta
    /// (CafeVentasController) y el diseño de cajas+tapas. Null si no es un compuesto con OEM.</summary>
    private async Task<(CafeOem Oem, decimal Mult)?> GetCompuestoOemAsync(MeliItem mi, CancellationToken ct)
    {
        if (!mi.CafeComboId.HasValue) return null;
        var combo = await _db.CafeCombos.AsNoTracking().FirstOrDefaultAsync(c => c.Id == mi.CafeComboId.Value, ct);
        if (combo is null || !combo.EsCompuesto || !combo.OemId.HasValue) return null;
        var oem = await _db.CafeOems.AsNoTracking().FirstOrDefaultAsync(o => o.Id == combo.OemId.Value, ct);
        if (oem is null) return null;
        var mult = combo.MultiplicadorOem ?? 1m;
        if (mult <= 0) mult = 1m;

        // 2026-07-18: PACKS — el precio/costo del compuesto tiene que contar la CANTIDAD de cajas del
        // pack, igual que la venta (CafeVentasController: OEM × Mult × Cantidad de la caja). Antes solo
        // usaba Mult → los packs (ej C9419TTX10, caja Cantidad=10) se cotizaban como 1 sola caja, y el
        // objetivo de margen no podía subir el precio (calculaba para 1 caja, daba menos que el precio
        // actual y ganaba el piso). La "caja" = el ÚNICO ítem cuyo producto cuelga del mismo OEM que el
        // compuesto (mismo criterio que la venta). Si no hay exactamente 1, no multiplicamos (combo de
        // varios productos distintos, no caja+tapa). No toca stock ni el precio de venta.
        var cajaCants = await (
            from ci in _db.CafeComboItems
            join p in _db.CafeProductos on ci.ProductoId equals p.Id
            where ci.ComboId == combo.Id && p.OemId == combo.OemId
            select ci.Cantidad
        ).ToListAsync(ct);
        if (cajaCants.Count == 1 && cajaCants[0] > 0)
            mult *= cajaCants[0];

        return (oem, mult);
    }

    /// <summary>Calcula el precio base del sistema para un MeliItem (sin ajuste).
    /// 2026-05-30: respeta el modelo OEM. Si un producto tiene OemId y el OEM tiene PvpConIva,
    /// el precio = OEM.PvpConIva × MultiplicadorOem (default 1). Si no, PrecioOtro × (1 + IvaPct/100).
    /// 2026-07-01: público para que el endpoint bulk-precio-por-ganancia lo reuse.
    /// 2026-07-14: si es un COMPUESTO con OEM, el precio sale del OEM del completo (no la suma de piezas).</summary>
    public async Task<(decimal Price, bool Found)> CalcularPrecioBaseAsync(MeliItem item, CancellationToken ct)
    {
        // 2026-07-14: COMPUESTO con OEM (caja+tapa) → precio del OEM del producto completo, NO la suma de las piezas.
        var comp = await GetCompuestoOemAsync(item, ct);
        if (comp is not null && comp.Value.Oem.PvpConIva is decimal pvpComp && pvpComp > 0)
            return (Math.Round(pvpComp * comp.Value.Mult, 2), true);

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

        // 2026-08-25 — SE SACA la suposición "arriba de $30.000 MeLi no cobra cargo fijo".
        // No era cierta y hacía que el precio quedara corto: caso real MLA1135409963 (galletitas),
        // precio $314.199 con $3.050 de cargo fijo → se pedía 55% de ganancia y quedaba en 53,0%.
        // La diferencia era exactamente el cargo fijo: $3.050 / 1,21 / $124.812 = 2,02 puntos.
        // Medido en prod: 63 publicaciones con objetivo cargado estaban 3,3% cortas de precio.
        // Ahora se usa SIEMPRE el cargo fijo que MeLi devuelve (lc.FixedFee): si no lo cobra viene
        // en 0 y la cuenta da igual que antes; si lo cobra, ahora sí se cuenta.
        var precioObjetivo = (netoConIvaNec + envio + fijoActual) / denom;

        // 2026-09-19 — ESCALÓN DEL ENVÍO. La cuenta de arriba usa los costos del precio de HOY, y
        // cuando el precio nuevo cae del otro lado de los $33.000 esos costos ya no son los que van a
        // regir. Caso real, batea N°1 MLA686863575: estaba a $63.299 con $13.090 de envío gratis a tu
        // cargo; el objetivo 50% dio $27.299 contando ese envío, pero abajo de $33.000 MeLi sacó el
        // envío gratis y quedó dejando ~190%. Además el cargo fijo también cambia con el precio
        // ($3.320 a $27.299, $2.740 a $15.899). Ahora se le pregunta a MeLi cuánto cobraría AL PRECIO
        // NUEVO y se recalcula hasta que el número cierra. Si MeLi no contesta, queda la cuenta de
        // antes (mejor un precio algo alto que uno que funde).
        try
        {
            var ajustado = await AjustarPorEscalonAsync(item, netoConIvaNec, precioObjetivo, ct);
            if (ajustado is > 0)
            {
                if (Math.Abs(ajustado.Value - precioObjetivo) > precioObjetivo * 0.01m)
                    _logger.LogInformation("[PricePush] {Mla}: objetivo recalculado con los costos al precio nuevo ${Antes:N0} → ${Despues:N0}",
                        item.MeliItemId, precioObjetivo, ajustado.Value);
                precioObjetivo = ajustado.Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PricePush] {Mla}: no se pudo recalcular con los costos al precio nuevo — queda la cuenta con los de hoy", item.MeliItemId);
        }
        return Math.Round(precioObjetivo, 2);
    }

    /// <summary>Precio donde MeLi cambia de régimen: abajo cobra cargo fijo y no obliga el envío
    /// gratis; arriba lo empuja (mismo valor que MeliPrecioManualService).</summary>
    private const decimal ESCALON_ENVIO = 33_000m;

    /// <summary>2026-09-19: resuelve el precio del objetivo con los costos que MeLi cobraría A ESE
    /// precio (SimularCostosAsync), primero abajo del escalón y, si no alcanza, arriba.
    /// Quién paga el envío, según lo que se vio en MeLi:
    ///   • abajo del escalón, solo si HOY ya lo das gratis estando abajo (lo elegiste vos);
    ///   • arriba, si hoy lo das gratis o si venís de abajo (MeLi suele obligarlo: se cuenta, por las dudas).</summary>
    private async Task<decimal?> AjustarPorEscalonAsync(MeliItem item, decimal netoConIvaNec, decimal precioInicial,
        CancellationToken ct)
    {
        bool pagaAbajo = item.FreeShipping && item.Price < ESCALON_ENVIO;
        bool pagaArriba = item.FreeShipping || item.Price < ESCALON_ENVIO;

        async Task<decimal?> Resolver(decimal desde, bool pagaEnvio)
        {
            var p = desde;
            for (var i = 0; i < 4; i++)
            {
                var sim = await _itemService.SimularCostosAsync(item.MeliItemId, p, ct);
                if (sim is null) return null;
                var envio = (pagaEnvio ? sim.ShippingCost : 0m) + sim.ListingFeeAmount;
                var denom = 1m - (sim.SaleFeeAmount - sim.FixedFee) / p;
                if (denom <= 0.05m) return null;
                var nuevo = (netoConIvaNec + envio + sim.FixedFee) / denom;
                if (Math.Abs(nuevo - p) <= p * 0.002m) return nuevo;
                p = nuevo;
            }
            return p;
        }

        // El precio que se publica es MAX(objetivo, precio de lista). Si la lista ya está arriba del
        // escalón, lo de abajo no sirve: se va a publicar arriba y ahí corre el envío. Caso medido:
        // publicación a $47.156 con lista $43.200 — sin esto daba $31.306, ganaba la lista y quedaba
        // dejando 31% en vez de 50%.
        var (piso, hayPiso) = await CalcularPrecioBaseAsync(item, ct);
        var pisoAbajo = !hayPiso || piso < ESCALON_ENVIO;

        if (pisoAbajo)
        {
            var abajo = await Resolver(Math.Min(precioInicial, ESCALON_ENVIO - 1m), pagaAbajo);
            if (abajo is null) return null;
            if (abajo.Value < ESCALON_ENVIO) return abajo;
        }

        var arriba = await Resolver(Math.Max(precioInicial, ESCALON_ENVIO), pagaArriba);
        return arriba is null ? null : Math.Max(arriba.Value, ESCALON_ENVIO);
    }

    /// <summary>2026-07-18: margen % actual de una publicación (al precio dado, o el suyo), usando la
    /// comisión REAL cacheada (sale_fee) SOLO si el precio no se movió >5%, y el costo pack-aware.
    /// Devuelve (null, false) = "no se puede calcular con confianza" (sin costo, o sin comisión real
    /// fresca). Quien llama debe, ante la duda, quedarse del lado seguro (no arriesgar).</summary>
    public async Task<(decimal? MarginPct, bool Confident)> CalcularMargenActualAsync(MeliItem item, decimal? livePrice = null, CancellationToken ct = default)
    {
        var costo = await CalcularCostoTotalAsync(item, ct);
        if (costo is null || costo.Value <= 0) return (null, false);
        var price = (livePrice.HasValue && livePrice.Value > 0) ? livePrice.Value : item.Price;
        if (price <= 0) return (null, false);
        if (item.SaleFeeAmount is decimal fee && fee > 0
            && item.SaleFeePriceSnapshot is decimal snap && snap > 0
            && Math.Abs(price - snap) / Math.Max(price, 1m) < 0.05m)
        {
            var netoConIva = price - fee - (item.SaleFeeShippingCost ?? 0m);
            var netoSinIva = netoConIva / 1.21m;
            var margin = (netoSinIva - costo.Value) / costo.Value * 100m;
            return (margin, true);
        }
        return (null, false);
    }

    /// <summary>2026-07-01: costo total del producto/combo linkeado a un MeliItem, mismo cálculo
    /// que el endpoint /product-cost del controller. Usado por el bulk-precio-por-ganancia.</summary>
    public async Task<decimal?> CalcularCostoTotalAsync(MeliItem mi, CancellationToken ct)
    {
        // 2026-07-14: COMPUESTO con OEM (caja+tapa) → costo del OEM del producto completo, NO la suma de las piezas.
        var comp = await GetCompuestoOemAsync(mi, ct);
        if (comp is not null && comp.Value.Oem.Costo > 0)
            return Math.Round(comp.Value.Oem.Costo * comp.Value.Mult, 2);

        // 1) Modelo nuevo: MeliItemComponentes
        var mecs = await (
            from c in _db.MeliItemComponentes
            join p in _db.CafeProductos on c.CafeProductoId equals p.Id
            where c.MeliItemId == mi.MeliItemId
            select new { p.Sku, p.Costo, c.Cantidad, c.CafeProductoId, c.MeliVariationId }
        ).ToListAsync(ct);
        if (mecs.Count > 0)
        {
            // 2026-09-18: publicación con colores → el costo de UN color, no la suma (ver MeliCostoPorColor).
            var productosDeLosColores = await ProductosDeLosColoresAsync(mi.MeliItemId, ct);
            mecs = MeliCostoPorColor.ComponentesDeUnaUnidad(mecs, x => x.CafeProductoId, x => x.MeliVariationId,
                x => x.Costo, productosDeLosColores, mi.VariationId, mi.CafeProductoId);
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

    /// <summary>Productos vinculados a las filas-color (variaciones) de una publicación.</summary>
    public async Task<List<int>> ProductosDeLosColoresAsync(string meliItemId, CancellationToken ct)
        => await _db.MeliItems.AsNoTracking()
            .Where(r => r.MeliItemId == meliItemId && r.VariationId != null && r.CafeProductoId != null)
            .Select(r => r.CafeProductoId!.Value)
            .Distinct()
            .ToListAsync(ct);

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

    /// <summary>Sube hasta la próxima centena terminada en 99 ($44.175,94 → $44.199). Público para
    /// que las pantallas muestren el mismo número que después se publica.</summary>
    public static decimal RedondearA99(decimal valor) => valor <= 0 ? valor : RoundUpToEnding(valor, 100m, 99m);

    private static decimal RoundUpToEnding(decimal valor, decimal unidad, decimal ending)
    {
        var lower = Math.Floor(valor / unidad) * unidad + ending;
        return lower >= valor ? lower : lower + unidad;
    }
}
