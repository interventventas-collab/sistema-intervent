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
    private readonly ILogger<MeliStockPushService> _logger;

    public MeliStockPushService(AppDbContext db, IHttpClientFactory httpFactory,
        MeliAccountService accountService, ILogger<MeliStockPushService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _accountService = accountService;
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
    public async Task<PushStockResult> PushStockForMeliItemsAsync(List<string> meliItemIds, CancellationToken ct = default, bool conservativeMode = false)
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

        int ok = 0, skipped = 0, err = 0;
        var mensajes = new List<string>();

        // Agrupar por (cuenta MeLi, MeliItemId) para emitir 1 PUT por publicacion.
        // (las multiples filas con variation se procesan adentro del mismo PUT).
        var grupos = meliItems
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
                    productos, token, ct, conservativeMode);

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
        string token, CancellationToken ct,
        bool conservativeMode = false)
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

        // FULL (dual stock): MeLi NO permite modificar `available_quantity` via PUT /items/{id},
        // pero SÍ se puede modificar el stock del depósito propio (selling_address) via
        // PUT /user-products/{upg_id}/stock/type/selling_address (endpoint descubierto el
        // 2026-05-25 analizando addin Integraly). Requiere header `x-version` fresco.
        // El stock del Full (meli_facility) NO se toca — eso lo maneja MeLi.
        if (isFulfillment)
        {
            // Necesitamos el user_product_id que viene en el GET del item.
            if (!doc.TryGetProperty("user_product_id", out var upgProp) || upgProp.ValueKind != JsonValueKind.String)
                return (PushOutcome.Skipped, "FULL: sin user_product_id, no se puede pushear");
            var upgId = upgProp.GetString();
            if (string.IsNullOrEmpty(upgId))
                return (PushOutcome.Skipped, "FULL: user_product_id vacío");

            // Para Full no hay variations (típicamente, según el código Integraly). Si las hay,
            // por ahora calculamos un único stock total y lo metemos en selling_address.
            // Cargar componentes/legacy stock.
            var compsForFull = compsThisItem;
            int stockFull;
            if (compsForFull.Count > 0)
                stockFull = CalcStockMinComponentes(compsForFull, productos);
            else
                stockFull = CalcStockLegacy(rows.First(), productos);
            // Reserva por producto (regla 2026-05-25): MAX de los StockMinimoMeLi seteados.
            // Si ninguno tiene valor → reserva 0 (MeLi recibe el stock real).
            int reservaFull = 0;
            var prodIdsFull = compsForFull.Select(c => c.CafeProductoId)
                .Concat(rows.Where(r => r.CafeProductoId.HasValue).Select(r => r.CafeProductoId!.Value))
                .Distinct();
            foreach (var pid in prodIdsFull)
            {
                if (productos.TryGetValue(pid, out var p) && p.StockMinimoMeLi.HasValue && p.StockMinimoMeLi.Value > reservaFull)
                    reservaFull = p.StockMinimoMeLi.Value;
            }
            var qtySellingAddress = Math.Max(0, stockFull - reservaFull);

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

        // RESERVA INTERNA — 2026-05-25 regla nueva (Opción A pedida por Osmar):
        // Cada producto tiene su StockMinimoMeLi explícito. Si la pub mapea a varios productos,
        // usamos el MAX (el más conservador). Si todos los productos lo tienen vacío (null o 0),
        // NO hay reserva — MeLi recibe el stock real.
        // El AppSetting global "meli.stock_push.reserva_interna" ya NO se usa para items normales.
        int reserva = 0;
        var todosProdIds = compsThisItem.Select(c => c.CafeProductoId)
            .Concat(rows.Where(r => r.CafeProductoId.HasValue).Select(r => r.CafeProductoId!.Value))
            .Distinct().ToList();
        foreach (var pid in todosProdIds)
        {
            if (productos.TryGetValue(pid, out var p) && p.StockMinimoMeLi.HasValue && p.StockMinimoMeLi.Value > reserva)
                reserva = p.StockMinimoMeLi.Value;
        }

        // Calcular stock disponible.
        if (meliVariationIds.Count > 0)
        {
            // ── MODO CONSERVADOR (no pausa, no activa, solo baja) ──
            // Skip total si la publicacion esta paused: NO la despertamos.
            if (conservativeMode && string.Equals(statusActual, "paused", StringComparison.OrdinalIgnoreCase))
                return (PushOutcome.Skipped, "ConservativeMode: paused no se toca");

            // Multi-variante: una entry por cada variation en el PUT.
            var varEntries = new List<object>();
            int sumStock = 0;
            bool algunoBaja = false; // en conservative: solo pushear si AL MENOS UNA variante baja stock real
            foreach (var vid in meliVariationIds)
            {
                var vidStr = vid.ToString();
                var compsForVar = compsThisItem
                    .Where(c => c.MeliVariationId == vidStr || string.IsNullOrEmpty(c.MeliVariationId))
                    .ToList();

                int stock;
                if (compsForVar.Count > 0)
                {
                    stock = CalcStockMinComponentes(compsForVar, productos);
                }
                else
                {
                    var legacyRow = rows.FirstOrDefault(r => r.VariationId == vidStr) ?? rows.First();
                    stock = CalcStockLegacy(legacyRow, productos);
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
                varEntries.Add(new { id = vid, available_quantity = stockFinal });
                sumStock += stockFinal;
            }

            // Si conservative y NINGUNA variante baja: skip total (no hay cambios reales)
            if (conservativeMode && !algunoBaja)
                return (PushOutcome.Skipped, "ConservativeMode: ninguna variante baja stock");

            var payload = new Dictionary<string, object> { ["variations"] = varEntries };
            if (!conservativeMode)
            {
                // Modo normal: si stock>0 y estaba paused, reactivar
                if (sumStock > 0 && string.Equals(statusActual, "paused", StringComparison.OrdinalIgnoreCase))
                    payload["status"] = "active";
            }
            // Conservative: NUNCA agregar "status" al payload → NO cambia status
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
                stock = CalcStockMinComponentes(compsForItem, productos);
            }
            else
            {
                stock = CalcStockLegacy(rows.First(), productos);
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

            var payload = new Dictionary<string, object> { ["available_quantity"] = stockMeliSingle };
            if (stockMeliSingle > 0 && string.Equals(statusActual, "paused", StringComparison.OrdinalIgnoreCase))
                payload["status"] = "active";
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
    /// (la publicacion solo se puede armar la cantidad de veces que el componente mas escaso permita).</summary>
    private static int CalcStockMinComponentes(List<MeliItemComponente> comps,
        Dictionary<int, CafeProducto> productos)
    {
        int? min = null;
        foreach (var c in comps)
        {
            if (!productos.TryGetValue(c.CafeProductoId, out var prod)) continue;
            int disp;
            if (string.Equals(prod.Categoria, "CAFE", StringComparison.OrdinalIgnoreCase))
            {
                var gramos = (c.Formato ?? "1KG").ToUpperInvariant() switch
                {
                    "1KG" => 1000m, "MEDIO" => 500m, "CUARTO" => 250m, _ => 1000m
                };
                var gramosNecesariosPorUnidad = gramos * c.Cantidad;
                disp = gramosNecesariosPorUnidad > 0
                    ? (int)Math.Floor(prod.StockGramos / gramosNecesariosPorUnidad)
                    : 0;
            }
            else
            {
                var unidadesPorBulto = string.Equals(c.Formato, "BULTO", StringComparison.OrdinalIgnoreCase)
                    && prod.UxB.HasValue && prod.UxB.Value > 0 ? prod.UxB.Value : 1;
                var unidadesNecesariasPorUnidad = c.Cantidad * unidadesPorBulto;
                disp = unidadesNecesariasPorUnidad > 0
                    ? (int)Math.Floor(prod.StockUnidades / unidadesNecesariasPorUnidad)
                    : 0;
            }
            if (disp < 0) disp = 0;
            if (min is null || disp < min) min = disp;
        }
        return min ?? 0;
    }

    /// <summary>Legacy: MeliItem.CafeProductoId + CafeFormato (sin pasar por MeliItemComponentes).</summary>
    private static int CalcStockLegacy(MeliItem mi, Dictionary<int, CafeProducto> productos)
    {
        if (!mi.CafeProductoId.HasValue) return 0;
        if (!productos.TryGetValue(mi.CafeProductoId.Value, out var prod)) return 0;
        if (string.Equals(prod.Categoria, "CAFE", StringComparison.OrdinalIgnoreCase))
        {
            var gramos = (mi.CafeFormato ?? "1KG").ToUpperInvariant() switch
            {
                "1KG" => 1000m, "MEDIO" => 500m, "CUARTO" => 250m, _ => 1000m
            };
            var disp = gramos > 0 ? (int)Math.Floor(prod.StockGramos / gramos) : 0;
            return disp < 0 ? 0 : disp;
        }
        else
        {
            var disp = prod.StockUnidades;
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
