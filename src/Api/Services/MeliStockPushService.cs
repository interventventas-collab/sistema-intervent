using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Pushea SOLO STOCK (no precio) a las publicaciones MeLi linkeadas a un CafeProducto.
///
/// Se llama event-driven desde los puntos criticos del sistema:
///   - Despues de descontar stock por venta directa (CafeVentas)
///   - Despues de ajustar stock manual (CafeProductos)
///   - Despues de procesar orden MeLi (MeliStockSync)
///
/// Logica del calculo de stock disponible para una publicacion:
///   - Si la publicacion mapea a 1 sólo componente → stock = floor(stock_componente / cantidad)
///   - Si la publicacion mapea a N componentes (combo MeLi) → stock = min(stock_i / cantidad_i)
///   - Si es CAFE → usa StockGramos / gramos_por_formato (1KG/MEDIO/CUARTO)
///   - Si es OTROS → usa StockUnidades
///
/// NUNCA pushea a publicaciones Full (MeLi no permite modificar stock por API en Full).
///
/// IMPORTANTE: este service NO toca precios. El push de precios se hace por separado
/// con MeliCafePricePushService.PushAllCafesAsync(). Esta separacion es a proposito —
/// el usuario quiere que MeLi mantenga los precios actuales mientras se sigue ajustando
/// solo el stock al momento real del sistema.
/// </summary>
public class MeliStockPushService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly MeliCambioDetectadoService _cambios;
    private readonly ILogger<MeliStockPushService> _logger;

    public MeliStockPushService(AppDbContext db, IHttpClientFactory httpFactory,
        MeliAccountService accountService, MeliCambioDetectadoService cambios,
        ILogger<MeliStockPushService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _accountService = accountService;
        _cambios = cambios;
        _logger = logger;
    }

    public record PushStockResult(int Procesadas, int Ok, int Skipped, int Errores, List<string> Mensajes);

    /// <summary>MASTER KILL SWITCH 2026-05-23: chequea AppSettings["meli.stock_push.master_enabled"].
    /// Si está en "false" o "0", NINGÚN push de stock a MeLi se ejecuta (ni event-driven ni background).
    /// Default = true (la app funciona normal). Para ROLLBACK: setear "false".
    /// </summary>
    private async Task<bool> IsMasterEnabledAsync(CancellationToken ct = default)
    {
        var s = await _db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == "meli.stock_push.master_enabled", ct);
        // Default true si la clave no existe (no rompemos lo existente).
        if (s is null) return true;
        var v = s.Value?.Trim().ToLowerInvariant();
        return v is null || (v != "false" && v != "0" && v != "off");
    }

    /// <summary>
    /// Pushea stock a MeLi de TODAS las publicaciones afectadas por un cambio de stock
    /// en este CafeProducto. Considera el producto como componente de combos MeLi tambien.
    /// </summary>
    public async Task<PushStockResult> PushStockForProductoAsync(int cafeProductoId, CancellationToken ct = default)
    {
        // MASTER KILL SWITCH: si está OFF, no pushear nada (ni event-driven ni background).
        if (!await IsMasterEnabledAsync(ct))
            return new PushStockResult(0, 0, 1, 0, new() { "Push DESHABILITADO (master kill switch). Para activar: AppSettings['meli.stock_push.master_enabled']='true'" });

        // 1) Identificar todas las publicaciones MeLi afectadas por este producto.
        //    a) Linkeo legacy: MeliItem.CafeProductoId == X
        //    b) Linkeo nuevo: MeliItemComponente.CafeProductoId == X
        var legacyMeliItemIds = await _db.MeliItems
            .Where(mi => mi.CafeProductoId == cafeProductoId)
            .Select(mi => mi.MeliItemId)
            .Distinct()
            .ToListAsync(ct);

        var compMeliItemIds = await _db.MeliItemComponentes
            .Where(c => c.CafeProductoId == cafeProductoId)
            .Select(c => c.MeliItemId)
            .Distinct()
            .ToListAsync(ct);

        var allMeliItemIds = legacyMeliItemIds.Concat(compMeliItemIds).Distinct().ToList();
        if (allMeliItemIds.Count == 0)
            return new PushStockResult(0, 0, 0, 0, new() { $"Producto {cafeProductoId}: no tiene publicaciones MeLi linkeadas" });

        return await PushStockForMeliItemsAsync(allMeliItemIds, ct);
    }

    /// <summary>
    /// Pushea stock para una lista especifica de MeliItemIds. Util cuando se sabe exactamente
    /// que publicaciones tocar (por ej. el job de respaldo).
    /// </summary>
    public async Task<PushStockResult> PushStockForMeliItemsAsync(List<string> meliItemIds, CancellationToken ct = default, bool conservativeMode = false, bool safeBulkMode = false)
    {
        // MASTER KILL SWITCH: si está OFF, no pushear nada.
        if (!await IsMasterEnabledAsync(ct))
            return new PushStockResult(0, 0, meliItemIds.Count, 0, new() { "Push DESHABILITADO (master kill switch)" });

        if (meliItemIds.Count == 0)
            return new PushStockResult(0, 0, 0, 0, new());

        // Cargar las filas de MeliItems (una por publicacion+variation).
        // Si una publicacion tiene N variantes, hay N filas; cuando se publica el PUT,
        // se hace una sola request con todas las variantes en el body.
        var meliItems = await _db.MeliItems
            .Include(mi => mi.MeliAccount)
            .Where(mi => meliItemIds.Contains(mi.MeliItemId))
            .ToListAsync(ct);

        if (meliItems.Count == 0)
            return new PushStockResult(0, 0, 0, 0, new() { "No se encontraron MeliItems para los ids dados" });

        // Cargar TODOS los componentes de estas publicaciones (no solo los del producto que cambio,
        // porque para combos MeLi necesitamos el stock minimo entre TODOS los componentes).
        var componentes = await _db.MeliItemComponentes
            .Where(c => meliItemIds.Contains(c.MeliItemId))
            .ToListAsync(ct);

        // Cargar todos los CafeProductos referenciados.
        var prodIds = componentes.Select(c => c.CafeProductoId)
            .Concat(meliItems.Where(mi => mi.CafeProductoId.HasValue).Select(mi => mi.CafeProductoId!.Value))
            .Distinct().ToList();
        var productos = await _db.CafeProductos
            .Where(p => prodIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        // 2026-05-29: regla "Full desenlazado". Para publicaciones cross_docking el push
        // debe usar SOLO el stock del depósito 9 de Abril (DepositoId=1), no el total que
        // incluye Full MeLi. Cargamos un dict auxiliar (StockUnidades, StockGramos) por
        // producto. Si el producto no tiene fila en Cafe_StockPorDeposito para 9 de Abril
        // (caso raro), retornamos (0, 0) -> nada disponible.
        const int DEPOSITO_9_ABRIL_ID = 1;
        var stock9Abril = await _db.CafeStockPorDeposito
            .Where(s => s.DepositoId == DEPOSITO_9_ABRIL_ID && prodIds.Contains(s.ProductoId))
            .ToDictionaryAsync(s => s.ProductoId, s => (s.StockUnidades, s.StockGramos), ct);

        int ok = 0, skipped = 0, err = 0;
        var mensajes = new List<string>();

        // 2026-05-30 — Dedup por UserProductId ANTES de agrupar: cuando 2+ publicaciones MeLi
        // comparten el mismo user_product_id (caso "catálogo MeLi" = misma publicación aparece
        // como normal + catálogo con stock compartido), pushear a una sola alcanza. Antes se
        // hacía 1 PUT por MLA, llegando 2 PUTs al mismo destino.
        var seenUpgKeys = new HashSet<string>();
        var skipMeliItemIds = new HashSet<string>();
        foreach (var rep in meliItems
            .GroupBy(m => m.MeliItemId)
            .Select(g => g.First()))
        {
            if (string.IsNullOrEmpty(rep.UserProductId)) continue;
            var key = $"{rep.MeliAccountId}|{rep.UserProductId}";
            if (!seenUpgKeys.Add(key))
            {
                skipMeliItemIds.Add(rep.MeliItemId);
                _logger.LogInformation("[StockPush] Skip {Mla}: UserProductId {Upg} ya procesado en este batch (catálogo compartido)",
                    rep.MeliItemId, rep.UserProductId);
            }
        }

        // Agrupar por (cuenta MeLi, MeliItemId) para emitir 1 PUT por publicacion.
        // (las multiples filas con variation se procesan adentro del mismo PUT).
        var grupos = meliItems
            .Where(mi => !skipMeliItemIds.Contains(mi.MeliItemId))
            .GroupBy(mi => new { mi.MeliAccountId, mi.MeliItemId })
            .ToList();

        // Cachear token por cuenta para reutilizar.
        var tokenCache = new Dictionary<int, string?>();

        foreach (var grupo in grupos)
        {
            if (ct.IsCancellationRequested) break;

            var firstRow = grupo.First();
            var account = firstRow.MeliAccount;
            if (account is null)
            {
                err++;
                mensajes.Add($"{firstRow.MeliItemId}: MeliAccount null");
                continue;
            }

            if (!tokenCache.TryGetValue(account.Id, out var token))
            {
                token = await _accountService.GetValidTokenAsync(account);
                tokenCache[account.Id] = token;
            }
            if (token is null)
            {
                err++;
                mensajes.Add($"{firstRow.MeliItemId}: token invalido para {account.Nickname}");
                continue;
            }

            try
            {
                var (resultStatus, msg) = await PushOneMeliItemAsync(
                    grupo.Key.MeliItemId,
                    grupo.ToList(),
                    componentes.Where(c => c.MeliItemId == grupo.Key.MeliItemId).ToList(),
                    productos, stock9Abril, token, ct, conservativeMode, safeBulkMode);

                switch (resultStatus)
                {
                    case PushOutcome.Ok: ok++; break;
                    case PushOutcome.Skipped: skipped++; break;
                    case PushOutcome.Error: err++; break;
                }
                if (!string.IsNullOrEmpty(msg)) mensajes.Add($"{grupo.Key.MeliItemId}: {msg}");

                // rate limit defensivo (~6 req/s max)
                await Task.Delay(150, ct);
            }
            catch (Exception ex)
            {
                err++;
                mensajes.Add($"{grupo.Key.MeliItemId}: {ex.Message}");
                _logger.LogWarning(ex, "Error pusheando stock a MeliItem {Id}", grupo.Key.MeliItemId);
            }
        }

        // Marcar productos como pusheados.
        // Solo marcamos LastPushedToMeli para los productos que efectivamente forman parte de
        // alguna publicacion donde el PUT fue OK. Por simplicidad: marcamos todos los productos
        // referenciados. Si el push fallo, el job de respaldo va a reintentar.
        if (ok > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var prod in productos.Values)
            {
                prod.LastPushedToMeli = now;
            }
            await _db.SaveChangesAsync(ct);
        }

        return new PushStockResult(grupos.Count, ok, skipped, err, mensajes);
    }

    private enum PushOutcome { Ok, Skipped, Error }

    private async Task<(PushOutcome, string?)> PushOneMeliItemAsync(
        string meliItemId,
        List<MeliItem> rows,
        List<MeliItemComponente> compsThisItem,
        Dictionary<int, CafeProducto> productos,
        Dictionary<int, (int StockUnidades, decimal StockGramos)> stock9Abril,
        string token, CancellationToken ct,
        bool conservativeMode = false,
        bool safeBulkMode = false)
    {
        using var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.Timeout = TimeSpan.FromSeconds(30);

        // Leer item para detectar logistic_type + variations reales en MeLi.
        var getResp = await http.GetAsync($"https://api.mercadolibre.com/items/{meliItemId}", ct);
        if (!getResp.IsSuccessStatusCode)
            return (PushOutcome.Error, $"GET {(int)getResp.StatusCode}");
        var doc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync()).RootElement;

        var isFulfillment = doc.TryGetProperty("shipping", out var sh)
            && sh.ValueKind == JsonValueKind.Object
            && sh.TryGetProperty("logistic_type", out var lt)
            && lt.ValueKind == JsonValueKind.String
            && string.Equals(lt.GetString(), "fulfillment", StringComparison.OrdinalIgnoreCase);

        // 2026-05-29 (final): regla aclarada por el usuario. Una publicación puede tener
        // stock Full + stock propio simultáneamente. El sistema empuja SOLO su parte (stock
        // de 9 de Abril − reserva) al "selling_address". Lo que MeLi tenga en "meli_facility"
        // (Full) no se toca — eso lo administra MeLi.
        if (isFulfillment)
        {
            if (!doc.TryGetProperty("user_product_id", out var upgProp) || upgProp.ValueKind != JsonValueKind.String)
                return (PushOutcome.Skipped, "FULL: sin user_product_id, no se puede pushear selling_address");
            var upgId = upgProp.GetString();
            if (string.IsNullOrEmpty(upgId))
                return (PushOutcome.Skipped, "FULL: user_product_id vacío");

            // Calcular stock con la misma regla que cross_docking: SOLO 9 de Abril − reserva.
            int stockFullProp;
            if (compsThisItem.Count > 0)
                stockFullProp = CalcStockMinComponentes(compsThisItem, productos, stock9Abril);
            else
                stockFullProp = CalcStockLegacy(rows.First(), productos, stock9Abril);
            // Reserva ya aplicada DENTRO de los calculadores (al producto base, antes de dividir).
            var qtySellingAddress = Math.Max(0, stockFullProp);

            return await PushSellingAddressForFullAsync(http, upgId!, qtySellingAddress, ct);
        }

        // ¿Tiene variations en MeLi?
        var meliVariationIds = new List<long>();
        if (doc.TryGetProperty("variations", out var variations)
            && variations.ValueKind == JsonValueKind.Array
            && variations.GetArrayLength() > 0)
        {
            foreach (var v in variations.EnumerateArray())
                meliVariationIds.Add(v.GetProperty("id").GetInt64());
        }

        // Leer status actual para decidir si hay que re-activar (paused → active al subir stock).
        var statusActual = doc.TryGetProperty("status", out var st) ? st.GetString() : null;

        // RESERVA INTERNA — desde 2026-05-27 la reserva (StockMinimoMeLi) se aplica
        // DENTRO de CalcStockMinComponentes y CalcStockLegacy al producto BASE,
        // antes de dividir por la cantidad del componente. Esto arregla el bug HE410X6
        // donde restar la reserva al stock final del pack daba 1 menos de lo correcto.
        // Variable `reserva` retenida = 0 acá porque ya se aplicó adentro.
        int reserva = 0;

        // Calcular stock disponible.
        if (meliVariationIds.Count > 0)
        {
            // ── MODO CONSERVADOR (no pausa, no activa, solo baja) ──
            // Skip total si la publicacion esta paused: NO la despertamos.
            if (conservativeMode && string.Equals(statusActual, "paused", StringComparison.OrdinalIgnoreCase))
                return (PushOutcome.Skipped, "ConservativeMode: paused no se toca");

            // ── MODO SAFE-BULK (no pausa publicaciones, no las reactiva, permite subir/bajar) ──
            // Skip si la publicacion esta paused: NO la despertamos.
            if (safeBulkMode && string.Equals(statusActual, "paused", StringComparison.OrdinalIgnoreCase))
                return (PushOutcome.Skipped, "SafeBulk: paused no se toca");

            // Multi-variante: una entry por cada variation en el PUT.
            var varEntries = new List<object>();
            int sumStock = 0;
            bool algunoBaja = false; // en conservative: solo pushear si AL MENOS UNA variante baja stock real
            bool todasMayorACero = true; // en safeBulk: si alguna variante queda en 0 → skip pub completa (no pausamos)
            foreach (var vid in meliVariationIds)
            {
                var vidStr = vid.ToString();
                var compsForVar = compsThisItem
                    .Where(c => c.MeliVariationId == vidStr || string.IsNullOrEmpty(c.MeliVariationId))
                    .ToList();

                int stock;
                if (compsForVar.Count > 0)
                {
                    stock = CalcStockMinComponentes(compsForVar, productos, stock9Abril);
                }
                else
                {
                    var legacyRow = rows.FirstOrDefault(r => r.VariationId == vidStr) ?? rows.First();
                    stock = CalcStockLegacy(legacyRow, productos, stock9Abril);
                }
                var stockMeliCalc = Math.Max(0, stock - reserva);

                // En modo conservador: si esta variante daria 0 o subiria/igualaria, mantener el valor actual de MeLi
                // (asi no se pausa ni se sube stock virtual).
                int stockFinal = stockMeliCalc;
                if (conservativeMode)
                {
                    var rowVar = rows.FirstOrDefault(r => r.VariationId == vidStr);
                    int meliQtyActual = rowVar?.AvailableQuantity ?? 0;
                    if (stockMeliCalc <= 0)
                    {
                        // No pausar: dejar la qty actual de MeLi
                        stockFinal = meliQtyActual;
                    }
                    else if (stockMeliCalc >= meliQtyActual)
                    {
                        // No subir ni igualar: dejar la qty actual
                        stockFinal = meliQtyActual;
                    }
                    else
                    {
                        // baja real: stockFinal queda en stockMeliCalc
                        algunoBaja = true;
                    }
                }
                // En modo safeBulk: si la variante quedaría en 0, marcamos para skip total de la pub
                // (no queremos que MeLi pause publicaciones al recibir 0).
                if (safeBulkMode && stockFinal <= 0)
                {
                    todasMayorACero = false;
                }
                varEntries.Add(new { id = vid, available_quantity = stockFinal });
                sumStock += stockFinal;
            }

            // Si conservative y NINGUNA variante baja: skip total (no hay cambios reales)
            if (conservativeMode && !algunoBaja)
                return (PushOutcome.Skipped, "ConservativeMode: ninguna variante baja stock");

            // SafeBulk: si alguna variante quedaría en 0, skip total para no pausar nada
            if (safeBulkMode && !todasMayorACero)
                return (PushOutcome.Skipped, "SafeBulk: alguna variante daria 0, skip para no pausar");

            // ── POLÍTICA 2026-07-16 (incidente cápsulas KDOR): el push NUNCA despierta una
            // publicación pausada. Antes acá se mandaba status=active si stock>0 → la publi se
            // reactivaba con el PRECIO VIEJO y se vendía a pérdida (pusheamos stock, no precio).
            // Ahora: si está paused y tiene stock para vender, NO tocamos nada y registramos el
            // aviso PAUSADA_CON_STOCK → Mis Alertas (Telegram/campanita) + cartel en el sistema.
            // El usuario revisa el precio y la activa él desde /cafe/cambios-meli.
            // OJO: tampoco pusheamos el stock — MeLi reactiva solo las pausadas por falta de
            // stock cuando les llega available_quantity > 0, así que ni eso es seguro.
            if (!conservativeMode && !safeBulkMode
                && string.Equals(statusActual, "paused", StringComparison.OrdinalIgnoreCase))
            {
                if (sumStock > 0)
                {
                    var precio = doc.TryGetProperty("price", out var prV) && prV.ValueKind == JsonValueKind.Number
                        ? prV.GetDecimal() : (decimal?)null;
                    var row0 = rows.First();
                    await _cambios.LogPausadaConStockAsync(meliItemId, row0.MeliAccountId, row0.Sku,
                        row0.Title, precio, sumStock, "push", saveChanges: true, ct);
                    return (PushOutcome.Skipped, $"Paused con stock {sumStock}: NO se despierta, avisado para revisar");
                }
                return (PushOutcome.Skipped, "Paused sin stock: no se toca");
            }

            var payload = new Dictionary<string, object> { ["variations"] = varEntries };
            // NUNCA agregar "status" al payload → el push no cambia el status en ningún modo
            return await DoPut(http, meliItemId, payload, ct);
        }
        else
        {
            // Sin variations.
            int stock;
            var compsForItem = compsThisItem.Where(c => string.IsNullOrEmpty(c.MeliVariationId)).ToList();
            if (compsForItem.Count == 0) compsForItem = compsThisItem;
            if (compsForItem.Count > 0)
            {
                stock = CalcStockMinComponentes(compsForItem, productos, stock9Abril);
            }
            else
            {
                stock = CalcStockLegacy(rows.First(), productos, stock9Abril);
            }

            // POLITICA 2026-05-23: pushear stock real (0 incluido) menos reserva interna.
            var stockMeliSingle = Math.Max(0, stock - reserva);

            if (conservativeMode)
            {
                // Skip total si esta paused
                if (string.Equals(statusActual, "paused", StringComparison.OrdinalIgnoreCase))
                    return (PushOutcome.Skipped, "ConservativeMode: paused no se toca");
                // Skip si daria 0 o subiria/igualaria
                int meliQtyActual = rows.First().AvailableQuantity;
                if (stockMeliSingle <= 0)
                    return (PushOutcome.Skipped, "ConservativeMode: daria 0, no pausar");
                if (stockMeliSingle >= meliQtyActual)
                    return (PushOutcome.Skipped, "ConservativeMode: subiria o igualaria, no permitido");
                // Solo cuando baja real
                var payloadC = new Dictionary<string, object> { ["available_quantity"] = stockMeliSingle };
                return await DoPut(http, meliItemId, payloadC, ct);
            }

            if (safeBulkMode)
            {
                // Skip si paused (no reactivamos)
                if (string.Equals(statusActual, "paused", StringComparison.OrdinalIgnoreCase))
                    return (PushOutcome.Skipped, "SafeBulk: paused no se toca");
                // Skip si daria 0 (no pausamos)
                if (stockMeliSingle <= 0)
                    return (PushOutcome.Skipped, "SafeBulk: daria 0, no pausar");
                // Permite tanto subir como bajar — pero NUNCA agrega status: active
                var payloadSb = new Dictionary<string, object> { ["available_quantity"] = stockMeliSingle };
                return await DoPut(http, meliItemId, payloadSb, ct);
            }

            // POLÍTICA 2026-07-16: NUNCA despertar pausadas (ver comentario en la rama con variations).
            if (string.Equals(statusActual, "paused", StringComparison.OrdinalIgnoreCase))
            {
                if (stockMeliSingle > 0)
                {
                    var precio = doc.TryGetProperty("price", out var prS) && prS.ValueKind == JsonValueKind.Number
                        ? prS.GetDecimal() : (decimal?)null;
                    var row0 = rows.First();
                    await _cambios.LogPausadaConStockAsync(meliItemId, row0.MeliAccountId, row0.Sku,
                        row0.Title, precio, stockMeliSingle, "push", saveChanges: true, ct);
                    return (PushOutcome.Skipped, $"Paused con stock {stockMeliSingle}: NO se despierta, avisado para revisar");
                }
                return (PushOutcome.Skipped, "Paused sin stock: no se toca");
            }

            var payload = new Dictionary<string, object> { ["available_quantity"] = stockMeliSingle };
            return await DoPut(http, meliItemId, payload, ct);
        }
    }

    /// <summary>Actualiza el stock del depósito propio (selling_address) de una publicación
    /// Full (logistic_type=fulfillment) sin tocar el stock del Full (meli_facility).
    ///
    /// Endpoint descubierto el 2026-05-25 analizando addin Integraly:
    ///   PUT /user-products/{upg_id}/stock/type/selling_address
    ///   Header: x-version (fresco, obtenido con GET previo)
    ///   Body: { "quantity": N }
    ///
    /// Maneja 409 (version mismatch) reintentando con el x-version nuevo.</summary>
    private async Task<(PushOutcome, string?)> PushSellingAddressForFullAsync(
        HttpClient http, string userProductId, int quantity, CancellationToken ct)
    {
        // 1) GET para obtener el x-version actual.
        var getUrl = $"https://api.mercadolibre.com/user-products/{userProductId}/stock";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string? xVersion;
            try
            {
                using var getResp = await http.GetAsync(getUrl, ct);
                if (!getResp.IsSuccessStatusCode)
                    return (PushOutcome.Error, $"FULL GET user-product stock {(int)getResp.StatusCode}");
                xVersion = getResp.Headers.TryGetValues("x-version", out var xvVals)
                    ? xvVals.FirstOrDefault() : null;
            }
            catch (Exception ex)
            {
                return (PushOutcome.Error, "FULL GET ex: " + ex.Message);
            }
            if (string.IsNullOrEmpty(xVersion))
                return (PushOutcome.Error, "FULL sin x-version header en GET");

            // 2) PUT a /stock/type/selling_address con x-version.
            var putUrl = $"https://api.mercadolibre.com/user-products/{userProductId}/stock/type/selling_address";
            var jsonBody = JsonSerializer.Serialize(new { quantity });
            using var req = new HttpRequestMessage(HttpMethod.Put, putUrl)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("x-version", xVersion);
            HttpResponseMessage resp;
            try { resp = await http.SendAsync(req, ct); }
            catch (Exception ex) { return (PushOutcome.Error, "FULL PUT ex: " + ex.Message); }

            // 204 No Content = OK
            if (resp.IsSuccessStatusCode)
                return (PushOutcome.Ok, $"FULL selling_address pusheado (qty={quantity})");

            // 409 = version mismatch, reintentar con x-version nuevo (max 1 retry)
            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict && attempt == 0)
                continue;

            var err = await resp.Content.ReadAsStringAsync(ct);
            return (PushOutcome.Error, $"FULL PUT {(int)resp.StatusCode}: {Trim(err)}");
        }
        return (PushOutcome.Error, "FULL: agotados los reintentos por version mismatch");
    }

    private async Task<(PushOutcome, string?)> DoPut(HttpClient http, string meliItemId,
        Dictionary<string, object> payload, CancellationToken ct)
    {
        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = await http.PutAsync($"https://api.mercadolibre.com/items/{meliItemId}", body, ct);
        if (resp.IsSuccessStatusCode) return (PushOutcome.Ok, $"stock pusheado");
        var err = await resp.Content.ReadAsStringAsync();
        // 1 reintento simple en errores 5xx / 429 (rate limit).
        if ((int)resp.StatusCode >= 500 || resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            await Task.Delay(1500, ct);
            var body2 = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var resp2 = await http.PutAsync($"https://api.mercadolibre.com/items/{meliItemId}", body2, ct);
            if (resp2.IsSuccessStatusCode) return (PushOutcome.Ok, "stock pusheado (retry)");
            var err2 = await resp2.Content.ReadAsStringAsync();
            return (PushOutcome.Error, $"PUT {(int)resp2.StatusCode}: {Trim(err2)}");
        }
        return (PushOutcome.Error, $"PUT {(int)resp.StatusCode}: {Trim(err)}");
    }

    private static string Trim(string s) => s.Length > 200 ? s.Substring(0, 200) : s;

    /// <summary>Calcula stock disponible para un mapping de N componentes. Devuelve el minimo
    /// (la publicacion solo se puede armar la cantidad de veces que el componente mas escaso permita).
    /// 2026-05-29: usa SOLO el stock del depósito 9 de Abril (stock9Abril). El depósito Full MeLi
    /// está desenlazado del sistema por configuración — su stock no se ofrece en cross_docking.</summary>
    private static int CalcStockMinComponentes(List<MeliItemComponente> comps,
        Dictionary<int, CafeProducto> productos,
        Dictionary<int, (int StockUnidades, decimal StockGramos)> stock9Abril)
    {
        // IMPORTANTE: la reserva (StockMinimoMeLi) se aplica AL PRODUCTO BASE ANTES de dividir
        // por la cantidad del componente. Si la aplicas después (al stock final del pack)
        // la división se hace sobre el stock total y la reserva no se "magnifica" — bug detectado
        // 2026-05-27 con HE410X6: si HE410=22 y reserva=1, lo correcto es floor((22-1)/6)=3,
        // NO floor(22/6)-1=2.
        int? min = null;
        foreach (var c in comps)
        {
            if (!productos.TryGetValue(c.CafeProductoId, out var prod)) continue;
            var s9 = stock9Abril.GetValueOrDefault(c.CafeProductoId, (0, 0m));
            int disp;
            if (string.Equals(prod.Categoria, "CAFE", StringComparison.OrdinalIgnoreCase))
            {
                var gramos = (c.Formato ?? "1KG").ToUpperInvariant() switch
                {
                    "1KG" => 1000m, "MEDIO" => 500m, "CUARTO" => 250m, _ => 1000m
                };
                var gramosNecesariosPorUnidad = gramos * c.Cantidad;
                // Reserva en gramos: convertir StockMinimoMeLi (que está en gramos para café) — usamos como gramos directos
                var reservaGramos = (prod.StockMinimoMeLi ?? 0);
                var stockGramosDisponibles = Math.Max(0m, s9.Item2 - reservaGramos);
                disp = gramosNecesariosPorUnidad > 0
                    ? (int)Math.Floor(stockGramosDisponibles / gramosNecesariosPorUnidad)
                    : 0;
            }
            else
            {
                var unidadesPorBulto = string.Equals(c.Formato, "BULTO", StringComparison.OrdinalIgnoreCase)
                    && prod.UxB.HasValue && prod.UxB.Value > 0 ? prod.UxB.Value : 1;
                var unidadesNecesariasPorUnidad = c.Cantidad * unidadesPorBulto;
                // Aplicar reserva (StockMinimoMeLi) al producto BASE antes de dividir.
                var reservaUnits = prod.StockMinimoMeLi ?? 0;
                var stockUnidadesDisponibles = Math.Max(0, s9.Item1 - reservaUnits);
                disp = unidadesNecesariasPorUnidad > 0
                    ? (int)Math.Floor(stockUnidadesDisponibles / unidadesNecesariasPorUnidad)
                    : 0;
            }
            if (disp < 0) disp = 0;
            if (min is null || disp < min) min = disp;
        }
        return min ?? 0;
    }

    /// <summary>Legacy: MeliItem.CafeProductoId + CafeFormato (sin pasar por MeliItemComponentes).
    /// 2026-05-29: usa SOLO el stock del depósito 9 de Abril (stock9Abril). Ver comentario en CalcStockMinComponentes.</summary>
    private static int CalcStockLegacy(MeliItem mi, Dictionary<int, CafeProducto> productos,
        Dictionary<int, (int StockUnidades, decimal StockGramos)> stock9Abril)
    {
        if (!mi.CafeProductoId.HasValue) return 0;
        if (!productos.TryGetValue(mi.CafeProductoId.Value, out var prod)) return 0;
        var s9 = stock9Abril.GetValueOrDefault(mi.CafeProductoId.Value, (0, 0m));
        // Aplicar reserva (StockMinimoMeLi) al stock base — coherente con CalcStockMinComponentes
        // (la reserva siempre se descuenta del producto base, NUNCA del stock ya calculado del pack)
        var reservaUnits = prod.StockMinimoMeLi ?? 0;
        if (string.Equals(prod.Categoria, "CAFE", StringComparison.OrdinalIgnoreCase))
        {
            var gramos = (mi.CafeFormato ?? "1KG").ToUpperInvariant() switch
            {
                "1KG" => 1000m, "MEDIO" => 500m, "CUARTO" => 250m, _ => 1000m
            };
            var reservaGramos = reservaUnits;  // StockMinimoMeLi en café es directamente gramos
            var stockGramosDisp = Math.Max(0m, s9.Item2 - reservaGramos);
            var disp = gramos > 0 ? (int)Math.Floor(stockGramosDisp / gramos) : 0;
            return disp < 0 ? 0 : disp;
        }
        else
        {
            var disp = Math.Max(0, s9.Item1 - reservaUnits);
            return disp < 0 ? 0 : disp;
        }
    }

    /// <summary>
    /// Identifica productos con StockChangedAt > LastPushedToMeli (o LastPushedToMeli null) y los pushea.
    /// Es la "red de seguridad" — si el push event-driven fallo por alguna razon, este job lo recupera.
    /// </summary>
    public async Task<PushStockResult> PushPendingAsync(int maxProductos = 200, CancellationToken ct = default)
    {
        var pendientes = await _db.CafeProductos
            .Where(p => p.IsActive
                && p.StockChangedAt != null
                && (p.LastPushedToMeli == null || p.StockChangedAt > p.LastPushedToMeli))
            .OrderBy(p => p.LastPushedToMeli) // primero los que mas tiempo llevan sin pushear (nulls first)
            .Take(maxProductos)
            .Select(p => p.Id)
            .ToListAsync(ct);

        if (pendientes.Count == 0)
            return new PushStockResult(0, 0, 0, 0, new());

        // Resolver todos los MeliItemIds afectados por estos productos (legacy + componentes).
        var meliFromLegacy = await _db.MeliItems
            .Where(mi => mi.CafeProductoId != null && pendientes.Contains(mi.CafeProductoId!.Value))
            .Select(mi => mi.MeliItemId)
            .Distinct().ToListAsync(ct);
        var meliFromComps = await _db.MeliItemComponentes
            .Where(c => pendientes.Contains(c.CafeProductoId))
            .Select(c => c.MeliItemId)
            .Distinct().ToListAsync(ct);
        var allMeliIds = meliFromLegacy.Concat(meliFromComps).Distinct().ToList();

        return await PushStockForMeliItemsAsync(allMeliIds, ct);
    }
}
