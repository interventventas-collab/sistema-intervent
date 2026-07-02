using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class MeliItemService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly AuditLogService _auditLog;
    private readonly AiService _aiService;
    private readonly SyncProgressService _syncProgress;
    private readonly MeliCambioDetectadoService _detector;

    public MeliItemService(AppDbContext db, IHttpClientFactory httpFactory, MeliAccountService accountService, AuditLogService auditLog, AiService aiService, SyncProgressService syncProgress, MeliCambioDetectadoService detector)
    {
        _db = db;
        _httpFactory = httpFactory;
        _accountService = accountService;
        _auditLog = auditLog;
        _aiService = aiService;
        _syncProgress = syncProgress;
        _detector = detector;
    }

    public async Task<MeliItemsResponse> GetItemsAsync(int? meliAccountId = null, string? status = null)
    {
        var query = _db.MeliItems
            .Include(i => i.MeliAccount)
            .Include(i => i.Product)
            .Include(i => i.Combo)
            .AsQueryable();

        if (meliAccountId.HasValue)
            query = query.Where(i => i.MeliAccountId == meliAccountId.Value);

        if (!string.IsNullOrEmpty(status))
        {
            // 2026-06-02: status especial "all" = traer todo (incluyendo closed). Para ese caso, no filtramos.
            if (!string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
                query = query.Where(i => i.Status == status);
        }
        else
        {
            // 2026-06-02: Por default excluir 'closed' (son ~3.193 publicaciones cerradas inútiles que solo
            // hacen lento el listado). Si querés traerlas, pasar status="all" explicitamente.
            query = query.Where(i => i.Status != "closed");
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderBy(i => i.Title)
            .Select(i => new MeliItemDto(
                i.Id, i.MeliItemId, i.MeliAccountId,
                i.MeliAccount != null ? i.MeliAccount.Nickname : "Desconocida",
                i.Title, i.CategoryId, i.CategoryPath, i.Price, i.OriginalPrice, i.CurrencyId,
                i.AvailableQuantity, i.SoldQuantity, i.Status,
                i.Condition, i.ListingTypeId, i.InstallmentTag, i.FreeShipping, i.Thumbnail, i.Permalink,
                i.Sku, i.UserProductId, i.FamilyId, i.FamilyName,
                i.DateCreated, i.LastUpdated,
                i.ProductId, i.Product != null ? i.Product.Title : null, i.Product != null ? (int?)i.Product.CriticalStock : null,
                i.ComboId, i.Combo != null ? i.Combo.Sku : null, i.Combo != null ? i.Combo.Name : null,
                i.CafeProductoId, i.CafeFormato,
                i.CafeProducto != null ? i.CafeProducto.Sku : null,
                i.CafeProducto != null ? i.CafeProducto.Nombre : null,
                _db.MeliItemComponentes.Count(mc => mc.MeliItemId == i.MeliItemId),
                null, // ComponentMappingsSummary se completa abajo en memoria
                i.LogisticType,
                i.VariationId,
                i.VariationAttributes,
                null,  // LastStockPushedToMeli — se completa abajo en memoria
                true, false, 0m, 0m, null,  // SyncStock/SyncPrecio/AjustePct/AjusteFijo/AjusteRedondeo
                false, null, null,  // 2026-06-11: PrecioIndependiente, PrecioFactor, PrecioBaseRef
                null,  // PrecioOtroConIvaCalc — se completa abajo en memoria
                null,  // 2026-06-01: ProductCost — se completa abajo en memoria
                i.CatalogListing,  // 2026-06-12: publicacion de catalogo
                i.SaleFeeAmount, i.SaleFeePriceSnapshot, i.SaleFeeCapturedAt,  // 2026-06-19: comision real cacheada
                i.SaleFeePercentageFee, i.SaleFeeFixedFee, i.SaleFeeFinancingFee  // 2026-07-02: desglose
                ))
            .ToListAsync();

        // Cargar resumen de componentes en una sola query y mergear en memoria (más eficiente que sub-query por fila).
        // IMPORTANTE: filtrar por VariationId del MeliItem para no mezclar variantes hermanas (color/talle/etc).
        // Solo aceptar componentes con MeliVariationId == variationId del item, o componentes sin variation_id.
        var meliIds = items.Where(it => it.ComponentMappingsCount > 0).Select(it => it.MeliItemId).ToHashSet();
        if (meliIds.Count > 0)
        {
            var summariesRaw = await (from mc in _db.MeliItemComponentes
                                      join p in _db.CafeProductos on mc.CafeProductoId equals p.Id
                                      where meliIds.Contains(mc.MeliItemId)
                                      select new { mc.MeliItemId, p.Sku, mc.Cantidad, mc.MeliVariationId })
                .ToListAsync();
            // Indexar por (MeliItemId, VariationId-o-null)
            // Reemplazar el DTO con la versión que tiene Summary (records son inmutables → with).
            for (int k = 0; k < items.Count; k++)
            {
                var it = items[k];
                // Componentes que aplican a ESTA fila (su variante o sin variation)
                var aplicables = summariesRaw.Where(s => s.MeliItemId == it.MeliItemId &&
                    (
                        // Si el item tiene VariationId, aceptar componentes con ese variation_id o sin variation
                        (it.VariationId != null && (s.MeliVariationId == it.VariationId || string.IsNullOrEmpty(s.MeliVariationId)))
                        ||
                        // Si el item NO tiene VariationId, aceptar solo componentes sin variation_id
                        (it.VariationId == null && string.IsNullOrEmpty(s.MeliVariationId))
                    )).ToList();
                if (aplicables.Count > 0)
                {
                    var summary = string.Join(", ", aplicables.Select(x => $"{x.Sku} ×{(x.Cantidad == Math.Floor(x.Cantidad) ? x.Cantidad.ToString("0") : x.Cantidad.ToString("0.##"))}"));
                    items[k] = it with { ComponentMappingsSummary = summary, ComponentMappingsCount = aplicables.Count };
                }
                else
                {
                    // No tiene componentes propios → resetear a 0 para no confundir
                    items[k] = it with { ComponentMappingsCount = 0 };
                }
            }
        }

        // ── Calcular LastStockPushedToMeli por cada item ──
        // Es el MAX(LastPushedToMeli) entre: el CafeProductoId directo (legacy) + todos los productos linkeados via MeliItemComponentes.
        // Si no hay ningún producto linkeado pusheado, queda null = nunca pusheado.
        var allItemIds = items.Select(it => it.MeliItemId).ToHashSet();
        if (allItemIds.Count > 0)
        {
            // Via CafeProductoId directo (legacy)
            var legacyPushes = await (from mi in _db.MeliItems
                                      join p in _db.CafeProductos on mi.CafeProductoId equals p.Id
                                      where allItemIds.Contains(mi.MeliItemId) && p.LastPushedToMeli != null
                                      select new { mi.MeliItemId, p.LastPushedToMeli })
                .ToListAsync();
            // Via componentes
            var compPushes = await (from mc in _db.MeliItemComponentes
                                    join p in _db.CafeProductos on mc.CafeProductoId equals p.Id
                                    where allItemIds.Contains(mc.MeliItemId) && p.LastPushedToMeli != null
                                    select new { mc.MeliItemId, p.LastPushedToMeli })
                .ToListAsync();
            var lastPushByItem = new Dictionary<string, DateTime>();
            foreach (var lp in legacyPushes)
            {
                if (lp.LastPushedToMeli.HasValue)
                {
                    if (!lastPushByItem.TryGetValue(lp.MeliItemId, out var cur) || lp.LastPushedToMeli.Value > cur)
                        lastPushByItem[lp.MeliItemId] = lp.LastPushedToMeli.Value;
                }
            }
            foreach (var cp in compPushes)
            {
                if (cp.LastPushedToMeli.HasValue)
                {
                    if (!lastPushByItem.TryGetValue(cp.MeliItemId, out var cur) || cp.LastPushedToMeli.Value > cur)
                        lastPushByItem[cp.MeliItemId] = cp.LastPushedToMeli.Value;
                }
            }
            for (int k = 0; k < items.Count; k++)
            {
                if (lastPushByItem.TryGetValue(items[k].MeliItemId, out var lp))
                    items[k] = items[k] with { LastStockPushedToMeli = lp };
            }
        }

        // 2026-05-29: cargar configuraciones de Sync por publicación (SyncStock/SyncPrecio/Ajustes).
        // Esto reemplaza la lectura desde las 3 columnas viejas de MeliItems (AjustePctOverride/etc).
        var configsByItemId = await _db.MeliItemSyncConfigs
            .Where(c => allItemIds.Contains(c.MeliItemId))
            .ToDictionaryAsync(c => c.MeliItemId);
        for (int k = 0; k < items.Count; k++)
        {
            if (configsByItemId.TryGetValue(items[k].MeliItemId, out var cfg))
            {
                items[k] = items[k] with
                {
                    SyncStock = cfg.SyncStock,
                    SyncPrecio = cfg.SyncPrecio,
                    AjustePct = cfg.AjustePct,
                    AjusteFijo = cfg.AjusteFijo,
                    AjusteRedondeo = cfg.AjusteRedondeo,
                    PrecioIndependiente = cfg.PrecioIndependiente,
                    PrecioFactor = cfg.PrecioFactor,
                    PrecioBaseRef = cfg.PrecioBaseRef
                };
            }
        }

        // 2026-05-29: calcular PrecioOtroConIvaCalc por cada item (precio del SISTEMA con IVA).
        // 2026-05-30: extendido — si el producto tiene OemId y el OEM tiene PvpConIva, el precio
        // viene del OEM (OEM.PvpConIva × MultiplicadorOem) en lugar de PrecioOtro × IVA.
        // Permite que el OEM sea fuente única de verdad y propague a todos los productos hijos.
        var componentesForPrecio = await _db.MeliItemComponentes
            .Where(c => allItemIds.Contains(c.MeliItemId))
            .ToListAsync();
        var prodIdsParaPrecio = items.Where(it => it.CafeProductoId.HasValue).Select(it => it.CafeProductoId!.Value).Distinct()
            .Concat(componentesForPrecio.Select(c => c.CafeProductoId)).Distinct().ToList();
        var prodsPrecio = await _db.CafeProductos
            .Where(p => prodIdsParaPrecio.Contains(p.Id))
            .Select(p => new { p.Id, p.PrecioOtro, p.IvaPct, p.OemId, p.MultiplicadorOem, p.Costo })
            .ToDictionaryAsync(p => p.Id);

        // 2026-06-01: dict de costo (sistema). Soporta combos legacy via CafeComboId.
        var comboIdsLegacy = items.Where(it => it.ComboId.HasValue).Select(it => it.ComboId!.Value).Distinct().ToList();
        List<(int ComboId, int ProductoId, decimal Cantidad)> comboItemsLegacy;
        if (comboIdsLegacy.Count == 0)
        {
            comboItemsLegacy = new();
        }
        else
        {
            var rawComboItems = await _db.CafeComboItems
                .Where(ci => comboIdsLegacy.Contains(ci.ComboId))
                .Select(ci => new { ci.ComboId, ci.ProductoId, ci.Cantidad })
                .ToListAsync();
            comboItemsLegacy = rawComboItems.Select(x => (ComboId: x.ComboId, ProductoId: x.ProductoId, Cantidad: (decimal)x.Cantidad)).ToList();
        }
        var prodIdsCombos = comboItemsLegacy.Select(c => c.ProductoId).Distinct().ToList();
        var prodCostosCombo = prodIdsCombos.Count == 0
            ? new Dictionary<int, decimal>()
            : await _db.CafeProductos
                .Where(p => prodIdsCombos.Contains(p.Id) && !prodIdsParaPrecio.Contains(p.Id))
                .Select(p => new { p.Id, p.Costo })
                .ToDictionaryAsync(p => p.Id, p => p.Costo);
        // OEMs referenciados por los productos
        var oemIdsRef = prodsPrecio.Values.Where(p => p.OemId.HasValue).Select(p => p.OemId!.Value).Distinct().ToList();
        var oemsPvp = oemIdsRef.Count == 0
            ? new Dictionary<int, decimal>()
            : await _db.CafeOems
                .Where(o => oemIdsRef.Contains(o.Id) && o.PvpConIva != null && o.PvpConIva > 0)
                .Select(o => new { o.Id, Pvp = o.PvpConIva!.Value })
                .ToDictionaryAsync(o => o.Id, o => o.Pvp);
        var componentesByItemId = componentesForPrecio.GroupBy(c => c.MeliItemId).ToDictionary(g => g.Key, g => g.ToList());

        // Helper local: precio c/IVA de un producto, respetando OEM si aplica.
        // Si tiene OEM y el OEM tiene PvpConIva → OEM × Multiplicador (default 1).
        // Si no → PrecioOtro × (1 + IvaPct/100).
        decimal? PrecioCIvaProducto(int prodId)
        {
            if (!prodsPrecio.TryGetValue(prodId, out var pp)) return null;
            if (pp.OemId.HasValue && oemsPvp.TryGetValue(pp.OemId.Value, out var oemPvp) && oemPvp > 0)
            {
                var mult = pp.MultiplicadorOem ?? 1m;
                if (mult <= 0) mult = 1m;
                return Math.Round(oemPvp * mult, 2);
            }
            if (pp.PrecioOtro.HasValue && pp.PrecioOtro.Value > 0)
                return Math.Round(pp.PrecioOtro.Value * (1 + pp.IvaPct / 100m), 2);
            return null;
        }

        for (int k = 0; k < items.Count; k++)
        {
            var it = items[k];
            decimal? precioBase = null;
            if (it.CafeProductoId.HasValue)
            {
                precioBase = PrecioCIvaProducto(it.CafeProductoId.Value);
                // 2026-06-19: cafe fraccionado — aplicar factor proporcional al precio sistema tambien.
                if (precioBase.HasValue && !string.IsNullOrEmpty(it.Sku))
                {
                    if (it.Sku.EndsWith(".4")) precioBase = Math.Round(precioBase.Value * 0.25m, 2);
                    else if (it.Sku.EndsWith(".2")) precioBase = Math.Round(precioBase.Value * 0.5m, 2);
                }
            }
            else if (componentesByItemId.TryGetValue(it.MeliItemId, out var comps))
            {
                // Filtrar componentes por VariationId si aplica
                var compsForItem = comps.Where(c =>
                {
                    if (!string.IsNullOrEmpty(it.VariationId))
                        return c.MeliVariationId == it.VariationId || string.IsNullOrEmpty(c.MeliVariationId);
                    return string.IsNullOrEmpty(c.MeliVariationId);
                }).ToList();
                if (compsForItem.Count == 0) compsForItem = comps;
                decimal sum = 0m;
                bool hasData = false;
                foreach (var c in compsForItem)
                {
                    var pCIva = PrecioCIvaProducto(c.CafeProductoId);
                    if (pCIva == null) continue;
                    sum += pCIva.Value * c.Cantidad;
                    hasData = true;
                }
                if (hasData) precioBase = Math.Round(sum, 2);
            }
            // 2026-06-01: calcular ProductCost (costo del producto/combo desde sistema)
            decimal? costoBase = null;
            // 1) Modelo nuevo: componentes
            if (componentesByItemId.TryGetValue(it.MeliItemId, out var compsCost))
            {
                var compsForItem = compsCost.Where(c =>
                {
                    if (!string.IsNullOrEmpty(it.VariationId))
                        return c.MeliVariationId == it.VariationId || string.IsNullOrEmpty(c.MeliVariationId);
                    return string.IsNullOrEmpty(c.MeliVariationId);
                }).ToList();
                if (compsForItem.Count == 0) compsForItem = compsCost;

                // 2026-06-19 FIX C: detectar linkeos rotos de VARIANTES.
                // Caso 1: N filas con MeliVariationId NULL apuntando al MISMO CafeProductoId
                //   (auto-link creo una fila por variante MeLi, todas al mismo producto del sistema).
                //   -> DEDUPLICAR.
                // Caso 2: N filas con MeliVariationId NULL apuntando a productos DISTINTOS
                //   (variantes color/talle mal linkeadas: Beige, Rojo, Blanco, etc.)
                //   -> PROMEDIO en vez de suma.
                var todosSinVariation = compsForItem.All(c => string.IsNullOrEmpty(c.MeliVariationId));
                if (todosSinVariation && !string.IsNullOrEmpty(it.VariationId) && compsForItem.Count > 1)
                {
                    compsForItem = compsForItem
                        .GroupBy(c => c.CafeProductoId)
                        .Select(g => g.First())
                        .ToList();
                }
                var distinctProds = compsForItem.Select(c => c.CafeProductoId).Distinct().Count();
                bool tratarComoVariantes = todosSinVariation && distinctProds > 1 && !string.IsNullOrEmpty(it.VariationId);

                decimal sumC = 0m; bool anyCost = false; int countWithCost = 0;
                foreach (var c in compsForItem)
                {
                    if (prodsPrecio.TryGetValue(c.CafeProductoId, out var pp))
                    { sumC += pp.Costo * c.Cantidad; anyCost = true; countWithCost++; }
                }
                if (anyCost)
                {
                    costoBase = tratarComoVariantes
                        ? Math.Round(sumC / Math.Max(countWithCost, 1), 2)  // promedio: linkeo de variantes roto
                        : Math.Round(sumC, 2);                              // suma: combo real
                }
            }
            // 2) Legacy: combo directo
            if (!costoBase.HasValue && it.ComboId.HasValue)
            {
                var citems = comboItemsLegacy.Where(x => x.ComboId == it.ComboId!.Value).ToList();
                if (citems.Count > 0)
                {
                    decimal sumC = 0m;
                    foreach (var ci in citems)
                    {
                        decimal cost = 0m;
                        if (prodsPrecio.TryGetValue(ci.ProductoId, out var pp)) cost = pp.Costo;
                        else if (prodCostosCombo.TryGetValue(ci.ProductoId, out var c2)) cost = c2;
                        sumC += cost * ci.Cantidad;
                    }
                    costoBase = Math.Round(sumC, 2);
                }
            }
            // 3) Legacy: producto directo
            if (!costoBase.HasValue && it.CafeProductoId.HasValue
                && prodsPrecio.TryGetValue(it.CafeProductoId.Value, out var ppDir))
            {
                // 2026-06-19: si la MLA es cafe fraccionado (sku F*.4 = cuarto, F*.2 = medio) y el
                // producto destino es el kilo entero, aplicar factor proporcional. Mismo criterio que
                // el UPDATE masivo en MeliItemComponentes. Sin esto, los MLAs legacy (sin componentes)
                // mostraban costo del kilo entero ($17.500 en vez de $4.375 para F3.4).
                decimal factor = 1m;
                if (!string.IsNullOrEmpty(it.Sku))
                {
                    if (it.Sku.EndsWith(".4")) factor = 0.25m;
                    else if (it.Sku.EndsWith(".2")) factor = 0.5m;
                }
                costoBase = Math.Round(ppDir.Costo * factor, 2);
            }
            if (precioBase.HasValue || costoBase.HasValue)
                items[k] = it with { PrecioOtroConIvaCalc = precioBase ?? it.PrecioOtroConIvaCalc, ProductCost = costoBase };
        }

        return new MeliItemsResponse(items, total);
    }

    public async Task<MeliItemDto> UpdateItemAsync(string meliItemId, UpdateMeliItemRequest request)
    {
        var item = await _db.MeliItems
            .Include(i => i.MeliAccount)
            .Include(i => i.Product)
            .FirstOrDefaultAsync(i => i.MeliItemId == meliItemId);

        if (item is null)
            throw new Exception($"Item {meliItemId} no encontrado");

        if (item.MeliAccount is null)
            throw new Exception($"Cuenta asociada no encontrada para {meliItemId}");

        var token = await _accountService.GetValidTokenAsync(item.MeliAccount);
        if (token is null)
            throw new Exception("Token expirado. Reconecta la cuenta de MercadoLibre.");

        // Capture old values for audit
        var oldTitle = item.Title;
        var oldPrice = item.Price;
        var oldOriginalPrice = item.OriginalPrice;
        var oldStock = item.AvailableQuantity;
        var oldStatus = item.Status;

        // Build payload with only changed fields
        var payload = new Dictionary<string, object?>();
        if (request.Title is not null) payload["title"] = request.Title;
        if (request.Price.HasValue) payload["price"] = request.Price.Value;
        if (request.AvailableQuantity.HasValue) payload["available_quantity"] = request.AvailableQuantity.Value;
        if (request.Status is not null) payload["status"] = request.Status;
        // Precio de lista (tachado): 0 = quitarlo (MeLi espera null)
        if (request.OriginalPrice.HasValue)
            payload["original_price"] = request.OriginalPrice.Value > 0 ? request.OriginalPrice.Value : null;

        if (payload.Count > 0)
        {
            var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await http.PutAsync($"https://api.mercadolibre.com/items/{meliItemId}", content);

            // Si MeLi devuelve 401/403, intentar refrescar el token y reintentar una vez
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var newToken = await _accountService.GetValidTokenAsync(item.MeliAccount, forceRefresh: true);
                if (newToken is not null)
                {
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
                    content = new StringContent(json, Encoding.UTF8, "application/json");
                    response = await http.PutAsync($"https://api.mercadolibre.com/items/{meliItemId}", content);
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new Exception($"Error de MercadoLibre ({response.StatusCode}): {errorBody}");
            }

            // Update local DB only after MeLi API success
            if (request.Title is not null) item.Title = request.Title;
            if (request.Price.HasValue) item.Price = request.Price.Value;
            if (request.OriginalPrice.HasValue) item.OriginalPrice = request.OriginalPrice.Value > 0 ? request.OriginalPrice.Value : null;
            if (request.AvailableQuantity.HasValue) item.AvailableQuantity = request.AvailableQuantity.Value;
            if (request.Status is not null) item.Status = request.Status;
            item.UpdatedAt = DateTime.UtcNow;
            item.LastUpdated = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            // Audit log
            var changes = new Dictionary<string, object>();
            if (request.Title is not null && request.Title != oldTitle)
                changes["Titulo"] = new { old = oldTitle, @new = request.Title };
            if (request.Price.HasValue && request.Price.Value != oldPrice)
                changes["Precio"] = new { old = oldPrice, @new = request.Price.Value };
            if (request.OriginalPrice.HasValue && request.OriginalPrice.Value != (oldOriginalPrice ?? 0))
                changes["PrecioLista"] = new { old = oldOriginalPrice, @new = request.OriginalPrice.Value };
            if (request.AvailableQuantity.HasValue && request.AvailableQuantity.Value != oldStock)
                changes["Stock"] = new { old = oldStock, @new = request.AvailableQuantity.Value };
            if (request.Status is not null && request.Status != oldStatus)
                changes["Estado"] = new { old = oldStatus, @new = request.Status };

            if (changes.Count > 0)
            {
                var changesJson = JsonSerializer.Serialize(changes);
                await _auditLog.LogAsync("MeliItem", meliItemId, "UPDATE", changesJson);
            }
        }

        var nickname = item.MeliAccount?.Nickname ?? "Desconocida";
        return ToDto(item, nickname);
    }

    public async Task<MeliItemDto> LinkToProductAsync(string meliItemId, int productId)
    {
        var item = await _db.MeliItems
            .Include(i => i.MeliAccount)
            .Include(i => i.Product)
            .Include(i => i.Combo)
            .FirstOrDefaultAsync(i => i.MeliItemId == meliItemId);
        if (item is null) throw new Exception("Item no encontrado");

        var product = await _db.Products.FindAsync(productId);
        if (product is null) throw new Exception("Producto no encontrado");

        item.ProductId = productId;
        item.Product = product;
        // Mutuamente excluyente: vincular a producto desvincula del combo si lo hubiera.
        item.ComboId = null;
        item.Combo = null;
        item.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var nickname = item.MeliAccount?.Nickname ?? "Desconocida";
        return ToDto(item, nickname);
    }

    public async Task<MeliItemDto> LinkToComboAsync(string meliItemId, int comboId)
    {
        var item = await _db.MeliItems
            .Include(i => i.MeliAccount)
            .Include(i => i.Product)
            .Include(i => i.Combo)
            .FirstOrDefaultAsync(i => i.MeliItemId == meliItemId);
        if (item is null) throw new Exception("Item no encontrado");

        var combo = await _db.Combos.FindAsync(comboId);
        if (combo is null) throw new Exception("Combo no encontrado");

        item.ComboId = comboId;
        item.Combo = combo;
        // Mutuamente excluyente: vincular a combo desvincula del producto.
        item.ProductId = null;
        item.Product = null;
        item.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var nickname = item.MeliAccount?.Nickname ?? "Desconocida";
        return ToDto(item, nickname);
    }

    public async Task<MeliItemDto> UnlinkProductAsync(string meliItemId)
    {
        var item = await _db.MeliItems
            .Include(i => i.MeliAccount)
            .FirstOrDefaultAsync(i => i.MeliItemId == meliItemId);
        if (item is null) throw new Exception("Item no encontrado");

        item.ProductId = null;
        item.Product = null;
        item.ComboId = null;
        item.Combo = null;
        item.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var nickname = item.MeliAccount?.Nickname ?? "Desconocida";
        return ToDto(item, nickname);
    }

    private static MeliItemDto ToDto(MeliItem item, string nickname) => new MeliItemDto(
        item.Id, item.MeliItemId, item.MeliAccountId, nickname,
        item.Title, item.CategoryId, item.CategoryPath, item.Price, item.OriginalPrice, item.CurrencyId,
        item.AvailableQuantity, item.SoldQuantity, item.Status,
        item.Condition, item.ListingTypeId, item.InstallmentTag, item.FreeShipping, item.Thumbnail, item.Permalink,
        item.Sku, item.UserProductId, item.FamilyId, item.FamilyName,
        item.DateCreated, item.LastUpdated,
        item.ProductId, item.Product?.Title, item.Product != null ? (int?)item.Product.CriticalStock : null,
        item.ComboId, item.Combo?.Sku, item.Combo?.Name,
        item.CafeProductoId, item.CafeFormato,
        item.CafeProducto?.Sku, item.CafeProducto?.Nombre,
        0, null, item.LogisticType,
        item.VariationId, item.VariationAttributes,
        null, // LastStockPushedToMeli
        true, false, 0m, 0m, null, // SyncConfig (queda en default)
        false, null, null, // 2026-06-11: PrecioIndependiente, PrecioFactor, PrecioBaseRef
        null, // PrecioOtroConIvaCalc
        null, // ProductCost
        item.CatalogListing, // 2026-06-12
        item.SaleFeeAmount, item.SaleFeePriceSnapshot, item.SaleFeeCapturedAt, // 2026-06-19
        item.SaleFeePercentageFee, item.SaleFeeFixedFee, item.SaleFeeFinancingFee); // 2026-07-02

    public async Task<int> DeleteItemsAsync(List<int> ids)
    {
        var items = await _db.MeliItems.Where(i => ids.Contains(i.Id)).ToListAsync();
        if (!items.Any()) return 0;

        var count = items.Count;
        var meliIds = items.Select(i => i.MeliItemId).ToList();
        _db.MeliItems.RemoveRange(items);
        await _db.SaveChangesAsync();

        var auditData = new
        {
            resumen = $"Eliminadas {count} publicaciones de la base de datos",
            cantidad = count,
            meliItemIds = meliIds
        };
        var json = System.Text.Json.JsonSerializer.Serialize(auditData);
        await _auditLog.LogAsync("MeliItem", string.Join(",", meliIds), "BULK_DELETE", json);

        return count;
    }

    public async Task<BulkCreateProductResult> BulkCreateProductsAsync(List<int> ids)
    {
        var result = new BulkCreateProductResult();

        var items = await _db.MeliItems
            .Include(i => i.MeliAccount)
            .Where(i => ids.Contains(i.Id))
            .ToListAsync();

        // Skip items that already have a product linked
        var alreadyLinked = items.Where(i => i.ProductId.HasValue).ToList();
        if (alreadyLinked.Any())
        {
            result.Skipped += alreadyLinked.Count;
            result.SkippedMessages.Add($"{alreadyLinked.Count} publicaciones ya tenian producto vinculado");
        }

        var toProcess = items.Where(i => !i.ProductId.HasValue).ToList();

        // Get all existing SKUs for duplicate detection AND parent auto-detection.
        var existingSkus = await _db.Products
            .Where(p => p.Sku != null && p.Sku != "")
            .Select(p => new { p.Sku, p.Title, p.Id, p.CostPrice, p.RetailPrice, p.VatRate })
            .ToListAsync();
        var skuMap = existingSkus
            .GroupBy(p => p.Sku!.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var item in toProcess)
        {
            try
            {
                // Check SKU duplicate
                if (!string.IsNullOrWhiteSpace(item.Sku))
                {
                    var skuLower = item.Sku.ToLowerInvariant();
                    if (skuMap.TryGetValue(skuLower, out var existing))
                    {
                        // Link to existing product instead of creating
                        item.ProductId = existing.Id;
                        item.UpdatedAt = DateTime.UtcNow;
                        await _db.SaveChangesAsync();
                        result.Skipped++;
                        result.SkippedMessages.Add($"SKU {item.Sku} ya existe (producto: {existing.Title}) - se vinculo automaticamente");
                        continue;
                    }
                }

                // === Heuristica: detectar padre candidato ===
                // Extraer la subcadena NUMERICA mas larga del SKU. Si esa subcadena coincide
                // con el SKU de un producto existente, asumimos que es el padre.
                // Ej: C818BL -> "818" -> matches SKU "818" -> padre.
                // Ej: 8718X4 -> "8718" -> matches SKU "8718" -> padre.
                int? parentId = null;
                decimal? parentCost = null, parentRetail = null, parentVat = null;
                if (!string.IsNullOrWhiteSpace(item.Sku))
                {
                    var numericCore = ExtractLongestNumericRun(item.Sku);
                    if (!string.IsNullOrEmpty(numericCore) && numericCore != item.Sku)
                    {
                        var parentLower = numericCore.ToLowerInvariant();
                        if (skuMap.TryGetValue(parentLower, out var parent))
                        {
                            parentId = parent.Id;
                            parentCost = parent.CostPrice;
                            parentRetail = parent.RetailPrice;
                            parentVat = parent.VatRate;
                        }
                    }
                }

                // Create new product. Si detectamos padre, lo creamos como HIJO heredando
                // cost/retail/IVA del padre. Sino, queda como independiente con datos del MeLi.
                var product = new Product
                {
                    Title = item.Title,
                    Sku = item.Sku,
                    RetailPrice = parentRetail ?? item.Price,
                    CostPrice = parentCost ?? 0,
                    VatRate = parentVat,
                    Stock = item.AvailableQuantity,
                    Photo1 = item.Thumbnail,
                    IsActive = true,
                    BaseProductId = parentId,
                    CreatedAt = DateTime.UtcNow
                };

                _db.Products.Add(product);
                await _db.SaveChangesAsync();

                // Link item to product
                item.ProductId = product.Id;
                item.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();

                // Add to skuMap so next items with same SKU get detected
                if (!string.IsNullOrWhiteSpace(item.Sku))
                {
                    var skuLower = item.Sku.ToLowerInvariant();
                    if (!skuMap.ContainsKey(skuLower))
                        skuMap[skuLower] = new { Sku = item.Sku, Title = product.Title, Id = product.Id, CostPrice = product.CostPrice, RetailPrice = product.RetailPrice, VatRate = product.VatRate };
                }

                result.Created++;
                if (parentId.HasValue)
                    result.SkippedMessages.Add($"✓ {item.Sku} creado como hijo del padre detectado automaticamente");
            }
            catch (Exception ex)
            {
                // Detach failed entities to avoid corrupting the context
                foreach (var entry in _db.ChangeTracker.Entries().Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Added))
                    entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;

                result.Skipped++;
                result.SkippedMessages.Add($"Error en '{item.Title}': {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        // Audit log
        var auditData = new
        {
            resumen = $"Creacion masiva: {result.Created} productos creados, {result.Skipped} omitidos",
            created = result.Created,
            skipped = result.Skipped,
            skippedMessages = result.SkippedMessages
        };
        var json = System.Text.Json.JsonSerializer.Serialize(auditData);
        await _auditLog.LogAsync("Product", string.Join(",", ids), "BULK_CREATE", json);

        return result;
    }

            public async Task<List<ItemPromotionDto>> GetItemPromotionsAsync(string meliItemId)
    {
        var item = await _db.MeliItems
            .Include(i => i.MeliAccount)
            .FirstOrDefaultAsync(i => i.MeliItemId == meliItemId);

        if (item is null)
            throw new Exception($"Item {meliItemId} no encontrado");
        if (item.MeliAccount is null)
            throw new Exception($"Cuenta asociada no encontrada para {meliItemId}");

        var token = await _accountService.GetValidTokenAsync(item.MeliAccount);
        if (token is null)
            throw new Exception("Token expirado. Reconecta la cuenta de MercadoLibre.");

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var userId = item.MeliAccount.MeliUserId.ToString();

        // Try URL patterns - the one without /marketplace/ prefix works
        var urlsToTry = new[]
        {
            $"https://api.mercadolibre.com/seller-promotions/items/{meliItemId}?app_version=v2",
            $"https://api.mercadolibre.com/marketplace/seller-promotions/items/{meliItemId}?user_id={userId}&app_version=v2",
        };

        string? promoJson = null;

        foreach (var promoUrl in urlsToTry)
        {
            using var promoRequest = new HttpRequestMessage(HttpMethod.Get, promoUrl);
            promoRequest.Headers.Add("version", "v2");
            promoRequest.Headers.Add("x-caller-id", userId);
            var promoResponse = await http.SendAsync(promoRequest);

            if (promoResponse.IsSuccessStatusCode)
            {
                promoJson = await promoResponse.Content.ReadAsStringAsync();
                break;
            }

            // On 401/403 refresh token once and retry same URL
            if (promoResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                promoResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var newToken = await _accountService.GetValidTokenAsync(item.MeliAccount, forceRefresh: true);
                if (newToken is not null)
                {
                    token = newToken;
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var retryRequest = new HttpRequestMessage(HttpMethod.Get, promoUrl);
                    retryRequest.Headers.Add("version", "v2");
                    retryRequest.Headers.Add("x-caller-id", userId);
                    var retryResponse = await http.SendAsync(retryRequest);

                    if (retryResponse.IsSuccessStatusCode)
                    {
                        promoJson = await retryResponse.Content.ReadAsStringAsync();
                        break;
                    }
                }
            }
        }

        if (promoJson is null)
            return new List<ItemPromotionDto>();

        var promoDoc = JsonDocument.Parse(promoJson).RootElement;

        var result = new List<ItemPromotionDto>();

        if (promoDoc.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var promo in promoDoc.EnumerateArray())
        {
            var promoId = promo.TryGetProperty("id", out var pid) ? pid.GetString() ?? "" : "";
            var promoType = promo.TryGetProperty("type", out var pt) ? pt.GetString() ?? "" : "";
            var promoStatus = promo.TryGetProperty("status", out var ps) ? ps.GetString() ?? "" : "";
            var promoName = promo.TryGetProperty("name", out var pn) ? pn.GetString() ?? "" : "";

            DateTime? startDate = promo.TryGetProperty("start_date", out var sd) && sd.ValueKind != JsonValueKind.Null
                ? sd.GetDateTime() : null;
            DateTime? finishDate = promo.TryGetProperty("finish_date", out var fd) && fd.ValueKind != JsonValueKind.Null
                ? fd.GetDateTime() : null;

            // Parse percentage breakdown and prices directly from the first response
            decimal? meliPct = promo.TryGetProperty("meli_percentage", out var mp) && mp.ValueKind == JsonValueKind.Number
                ? mp.GetDecimal() : null;
            decimal? sellerPct = promo.TryGetProperty("seller_percentage", out var sp) && sp.ValueKind == JsonValueKind.Number
                ? sp.GetDecimal() : null;
            decimal? promoPrice = promo.TryGetProperty("price", out var pp) && pp.ValueKind == JsonValueKind.Number
                ? pp.GetDecimal() : null;
            decimal? originalPrice = promo.TryGetProperty("original_price", out var opp) && opp.ValueKind == JsonValueKind.Number
                ? opp.GetDecimal() : null;

            var dto = new ItemPromotionDto
            {
                PromotionId = promoId,
                Type = promoType,
                Status = promoStatus,
                Name = promoName,
                StartDate = startDate,
                FinishDate = finishDate,
                MeliPercentage = meliPct,
                SellerPercentage = sellerPct,
                PromotionPrice = promoPrice,
                OriginalPrice = originalPrice
            };

            result.Add(dto);
        }

        return result;
    }

    public async Task<MeliItemSyncByIdBatchResult> SyncItemsByIdAsync(string rawIds)
    {
        var ids = (rawIds ?? "")
            .Split(new[] { ',', ';', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0)
            throw new Exception("Hay que indicar al menos un ID de publicacion.");

        var results = new List<MeliItemSyncByIdResultItem>();
        int synced = 0, errors = 0;

        foreach (var id in ids)
        {
            try
            {
                var single = await SyncSingleItemAsync(id);
                results.Add(new MeliItemSyncByIdResultItem(single.Item.MeliItemId, single.Action, single.AccountNickname, null));
                synced++;
            }
            catch (Exception ex)
            {
                results.Add(new MeliItemSyncByIdResultItem(id, null, null, ex.Message));
                errors++;
            }
        }

        return new MeliItemSyncByIdBatchResult(ids.Count, synced, errors, results);
    }

    public async Task<MeliItemSyncSingleResult> SyncSingleItemAsync(string meliItemId)
    {
        meliItemId = (meliItemId ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(meliItemId))
            throw new Exception("Hay que indicar un ID de publicacion.");

        // Aceptar IDs sin prefijo (solo numeros): asumir MLA (Argentina) por default.
        if (meliItemId.All(char.IsDigit))
            meliItemId = "MLA" + meliItemId;

        var accounts = await _accountService.GetAllAccountEntitiesAsync();
        if (accounts.Count == 0)
            throw new Exception("No hay cuentas de MercadoLibre conectadas.");

        // Use the first account's token to fetch the item (the /items/{id} endpoint
        // works for any item, but we still need a token to avoid throttling).
        var firstAccount = accounts[0];
        var token = await _accountService.GetValidTokenAsync(firstAccount);
        if (token is null)
            throw new Exception($"Token expirado para {firstAccount.Nickname}. Reconecta la cuenta.");

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // include_attributes=all expone atributos como SELLER_SKU que el endpoint default oculta.
        var response = await http.GetAsync($"https://api.mercadolibre.com/items/{meliItemId}?include_attributes=all");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new Exception($"La publicacion {meliItemId} no existe en MercadoLibre.");
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync();
            throw new Exception($"Error de MercadoLibre ({response.StatusCode}): {errBody}");
        }

        var json = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(json).RootElement;

        // Identify owner account by seller_id
        long sellerId = 0;
        if (body.TryGetProperty("seller_id", out var sid) && sid.ValueKind == JsonValueKind.Number)
            sellerId = sid.GetInt64();

        var ownerAccount = accounts.FirstOrDefault(a => a.MeliUserId == sellerId);
        if (ownerAccount is null)
            throw new Exception($"La publicacion {meliItemId} pertenece a una cuenta de MercadoLibre que no esta conectada (seller_id={sellerId}).");

        // Detect if it already existed locally to report create vs update
        var existed = await _db.MeliItems.AnyAsync(i => i.MeliItemId == meliItemId);

        await UpsertItemAsync(ownerAccount.Id, body);
        await _db.SaveChangesAsync();

        var saved = await _db.MeliItems
            .Include(i => i.MeliAccount)
            .Include(i => i.Product)
            .Include(i => i.Combo)
            .FirstAsync(i => i.MeliItemId == meliItemId);

        var dto = ToDto(saved, saved.MeliAccount != null ? saved.MeliAccount.Nickname : "Desconocida");

        var action = existed ? "updated" : "created";
        await _auditLog.LogAsync("Sync", "items", "SYNC_BY_ID",
            System.Text.Json.JsonSerializer.Serialize(new { meliItemId, action, cuenta = ownerAccount.Nickname }));

        return new MeliItemSyncSingleResult(action, ownerAccount.Nickname, dto);
    }

    /// <summary>Audita: para cada cuenta conectada, pide a MeLi la lista completa de MLAs (solo IDs, sin descargar detalles)
    /// y compara contra los MeliItemId que tenemos en DB. Devuelve 3 conjuntos: match, en MeLi falta en sistema, en sistema falta en MeLi.
    /// No modifica nada — solo informa. La importación de los faltantes se hace después con SyncItemsByIdAsync.</summary>
    public async Task<MeliAuditResult> AuditAccountsAsync(int? accountId = null)
    {
        var startedAt = DateTime.UtcNow;
        var result = new MeliAuditResult();
        var accounts = await _accountService.GetAllAccountEntitiesAsync();
        if (accountId.HasValue)
            accounts = accounts.Where(a => a.Id == accountId.Value).ToList();

        foreach (var account in accounts)
        {
            var accResult = new MeliAuditAccountResult
            {
                AccountId = account.Id,
                Nickname = account.Nickname ?? $"Cuenta {account.Id}"
            };

            try
            {
                var token = await _accountService.GetValidTokenAsync(account);
                if (token is null)
                {
                    accResult.Error = "Sin token válido — la cuenta debe reconectarse";
                    result.Accounts.Add(accResult);
                    continue;
                }

                // 1) Bajar TODOS los MLAs de MeLi via scroll_id (sin filtro de estado → trae activos + pausados + en revision + cerrados que MeLi todavia listee)
                var meliIds = await GetAllItemIdsFromMeliAsync(account, token);

                // 2) Traer los MeliItemId del sistema para esa cuenta (distinct, sin importar status)
                var systemIds = await _db.MeliItems
                    .Where(x => x.MeliAccountId == account.Id)
                    .Select(x => x.MeliItemId)
                    .Distinct()
                    .ToListAsync();

                var meliSet = new HashSet<string>(meliIds);
                var systemSet = new HashSet<string>(systemIds);

                var meliOnly = meliSet.Except(systemSet).ToList();
                var systemOnly = systemSet.Except(meliSet).ToList();
                var both = meliSet.Intersect(systemSet).Count();

                accResult.MeliCount = meliSet.Count;
                accResult.SystemCount = systemSet.Count;
                accResult.BothCount = both;
                // Limitar listados a primeros 500 para no saturar el JSON de respuesta
                accResult.MeliOnly = meliOnly.Take(500).ToList();
                accResult.SystemOnly = systemOnly.Take(500).ToList();
            }
            catch (Exception ex)
            {
                accResult.Error = ex.Message;
            }

            result.Accounts.Add(accResult);
        }

        result.TotalMeli = result.Accounts.Sum(a => a.MeliCount);
        result.TotalSystem = result.Accounts.Sum(a => a.SystemCount);
        result.TotalBoth = result.Accounts.Sum(a => a.BothCount);
        result.TotalMeliOnly = result.Accounts.Sum(a => a.MeliOnly.Count);
        result.TotalSystemOnly = result.Accounts.Sum(a => a.SystemOnly.Count);
        result.FinishedAt = DateTime.UtcNow;
        result.DurationSeconds = (int)(DateTime.UtcNow - startedAt).TotalSeconds;
        return result;
    }

    /// <summary>Trae todos los MeliItemId de una cuenta paginando con scroll_id. Sin filtro de estado.
    /// Equivalente al primer paso de SyncItemsForAccountAsync pero sin descargar los detalles.</summary>
    private async Task<List<string>> GetAllItemIdsFromMeliAsync(MeliAccount account, string token)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var ids = new List<string>();
        int limit = 100;
        string? scrollId = null;
        bool isFirstRequest = true;

        while (true)
        {
            var url = $"https://api.mercadolibre.com/users/{account.MeliUserId}/items/search" +
                $"?search_type=scan&limit={limit}";
            if (!isFirstRequest && !string.IsNullOrEmpty(scrollId))
                url += $"&scroll_id={scrollId}";

            var response = await http.GetAsync(url);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var newToken = await _accountService.GetValidTokenAsync(account, forceRefresh: true);
                if (newToken is not null)
                {
                    token = newToken;
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    response = await http.GetAsync(url);
                }
            }
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync();
                throw new Exception($"MeLi API error ({response.StatusCode}): {errBody}");
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json).RootElement;
            var results = doc.GetProperty("results");
            int count = 0;
            foreach (var idEl in results.EnumerateArray())
            {
                var itemId = idEl.GetString();
                if (!string.IsNullOrEmpty(itemId))
                {
                    ids.Add(itemId);
                    count++;
                }
            }

            scrollId = doc.TryGetProperty("scroll_id", out var sid) && sid.ValueKind != JsonValueKind.Null
                ? sid.GetString() : null;
            isFirstRequest = false;
            if (count == 0 || string.IsNullOrEmpty(scrollId)) break;
        }

        return ids;
    }

    public async Task<MeliItemSyncResult> SyncItemsAsync(string? statusFilter = null, int? accountId = null, string? progressId = null)
    {
        var accounts = await _accountService.GetAllAccountEntitiesAsync();

        // Filter by account if specified
        if (accountId.HasValue)
            accounts = accounts.Where(a => a.Id == accountId.Value).ToList();

        // Start progress tracking
        progressId ??= _syncProgress.StartSync("Sincronizando publicaciones");
        _syncProgress.Update(progressId, p =>
        {
            p.TotalAccounts = accounts.Count;
            p.CurrentStep = "Iniciando sincronizacion...";
        });

        int totalSynced = 0;
        int totalErrors = 0;
        var errors = new List<string>();

        for (int i = 0; i < accounts.Count; i++)
        {
            var account = accounts[i];
            _syncProgress.Update(progressId, p =>
            {
                p.AccountIndex = i + 1;
                p.CurrentAccount = account.Nickname;
                p.CurrentStep = $"Obteniendo publicaciones de {account.Nickname}...";
            });

            try
            {
                var token = await _accountService.GetValidTokenAsync(account);
                if (token is null)
                {
                    errors.Add($"Token expirado para {account.Nickname}");
                    totalErrors++;
                    _syncProgress.Update(progressId, p => p.TotalErrors = totalErrors);
                    continue;
                }

                var synced = await SyncItemsForAccountAsync(account, token, statusFilter, progressId);
                totalSynced += synced;
            }
            catch (Exception ex)
            {
                errors.Add($"Error en {account.Nickname}: {ex.Message}");
                totalErrors++;
                _syncProgress.Update(progressId, p => p.TotalErrors = totalErrors);
            }
        }

        // Audit log for sync - include full error details as JSON
        var filterDesc = statusFilter is not null ? $" (filtro: {statusFilter})" : "";
        var auditData = new
        {
            resumen = $"Sincronizados {totalSynced} items, {totalErrors} errores{filterDesc}",
            totalSincronizados = totalSynced,
            totalErrores = totalErrors,
            filtro = statusFilter,
            errores = errors
        };
        var syncJson = System.Text.Json.JsonSerializer.Serialize(auditData);
        await _auditLog.LogAsync("Sync", "items", "SYNC", syncJson);

        // Complete progress
        _syncProgress.Complete(progressId, $"Sincronizados {totalSynced} items, {totalErrors} errores");
        _syncProgress.Cleanup();

        return new MeliItemSyncResult(totalSynced, totalErrors, errors);
    }

    private async Task<int> SyncItemsForAccountAsync(MeliAccount account, string token, string? statusFilter = null, string? progressId = null)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Step 1: Get all item IDs via search using scan mode (supports >1000 items)
        var allItemIds = new List<string>();
        int limit = 100;
        string? scrollId = null;
        bool isFirstRequest = true;

        while (true)
        {
            // Use search_type=scan for pagination (MeLi requirement for >1000 results)
            var url = $"https://api.mercadolibre.com/users/{account.MeliUserId}/items/search" +
                $"?search_type=scan&limit={limit}";

            // Add scroll_id for subsequent pages
            if (!isFirstRequest && !string.IsNullOrEmpty(scrollId))
                url += $"&scroll_id={scrollId}";

            // Add status filter if specified (active, paused, closed)
            if (!string.IsNullOrEmpty(statusFilter))
                url += $"&status={statusFilter}";

            var response = await http.GetAsync(url);

            // Si devuelve 401/403, refrescar token y reintentar
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var newToken = await _accountService.GetValidTokenAsync(account, forceRefresh: true);
                if (newToken is not null)
                {
                    token = newToken;
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    response = await http.GetAsync(url);
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new Exception($"MeLi API error ({response.StatusCode}): {errorBody}");
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json).RootElement;

            var results = doc.GetProperty("results");
            int count = 0;
            foreach (var id in results.EnumerateArray())
            {
                var itemId = id.GetString();
                if (!string.IsNullOrEmpty(itemId))
                    allItemIds.Add(itemId);
                count++;
            }

            // Get scroll_id for next page - null or missing means we're done
            scrollId = doc.TryGetProperty("scroll_id", out var sid) && sid.ValueKind != JsonValueKind.Null
                ? sid.GetString() : null;

            isFirstRequest = false;

            // Stop if no results returned or no scroll_id for next page
            if (count == 0 || string.IsNullOrEmpty(scrollId))
                break;
        }

        // Report found items
        if (progressId is not null)
        {
            _syncProgress.Update(progressId, p =>
            {
                p.TotalItemsFound += allItemIds.Count;
                p.CurrentStep = $"{account.Nickname}: {allItemIds.Count} publicaciones encontradas, descargando detalles...";
            });
        }

        // Step 2: Batch fetch item details (20 at a time)
        int synced = 0;
        for (int i = 0; i < allItemIds.Count; i += 20)
        {
            var batch = allItemIds.Skip(i).Take(20).ToList();
            var idsParam = string.Join(",", batch);

            // include_attributes=all expone SELLER_SKU (a nivel item y variantes).
            var response = await http.GetAsync($"https://api.mercadolibre.com/items?ids={idsParam}&include_attributes=all");
            if (!response.IsSuccessStatusCode)
                continue;

            var json = await response.Content.ReadAsStringAsync();
            var items = JsonDocument.Parse(json).RootElement;

            foreach (var itemResult in items.EnumerateArray())
            {
                var code = itemResult.GetProperty("code").GetInt32();
                if (code != 200) continue;

                var body = itemResult.GetProperty("body");
                synced += await UpsertItemAsync(account.Id, body);
            }

            await _db.SaveChangesAsync();

            // Update progress
            if (progressId is not null)
            {
                var currentSynced = Math.Min(i + 20, allItemIds.Count);
                _syncProgress.Update(progressId, p =>
                {
                    p.ItemsSynced += batch.Count;
                    p.CurrentStep = $"{account.Nickname}: {currentSynced}/{allItemIds.Count} items procesados";
                    if (p.TotalItemsFound > 0)
                        p.Percentage = (int)((double)p.ItemsSynced / p.TotalItemsFound * 80); // 80% for items, 20% for promos/categories
                });
            }
        }

        // Step 3: Marcar como "deleted" las publicaciones que ya no existen en MeLi
        // Solo cuando se sincroniza sin filtro de estado (sync completa)
        if (string.IsNullOrEmpty(statusFilter))
        {
            var localItemIds = await _db.MeliItems
                .Where(i => i.MeliAccountId == account.Id && i.Status != "deleted")
                .Select(i => i.MeliItemId)
                .ToListAsync();

            var meliIdSet = new HashSet<string>(allItemIds);
            var deletedIds = localItemIds.Where(id => !meliIdSet.Contains(id)).ToList();

            if (deletedIds.Any())
            {
                var itemsToMark = await _db.MeliItems
                    .Where(i => i.MeliAccountId == account.Id && deletedIds.Contains(i.MeliItemId))
                    .ToListAsync();

                foreach (var item in itemsToMark)
                    item.Status = "deleted";

                await _db.SaveChangesAsync();
            }
        }

        // Step 4: Sync promotional prices for active items
        if (progressId is not null)
        {
            _syncProgress.Update(progressId, p =>
            {
                p.CurrentStep = $"{account.Nickname}: Sincronizando precios promocionales...";
                p.Percentage = Math.Max(p.Percentage, 80);
            });
        }

        // The items API does not include promotional discounts, so we use /items/{id}/sale_price
        var activeItems = await _db.MeliItems
            .Where(i => i.MeliAccountId == account.Id && i.Status == "active")
            .Select(i => new { i.Id, i.MeliItemId })
            .ToListAsync();

        foreach (var ai in activeItems)
        {
            try
            {
                var spResponse = await http.GetAsync(
                    $"https://api.mercadolibre.com/items/{ai.MeliItemId}/sale_price?app_version=v2");

                if (!spResponse.IsSuccessStatusCode)
                {
                    // No sale_price means no active promotion - clear OriginalPrice if set
                    var itemToClean = await _db.MeliItems.FindAsync(ai.Id);
                    if (itemToClean is not null && itemToClean.OriginalPrice.HasValue)
                    {
                        itemToClean.OriginalPrice = null;
                    }
                    continue;
                }

                var spJson = await spResponse.Content.ReadAsStringAsync();
                var spDoc = JsonDocument.Parse(spJson).RootElement;

                var promoAmount = spDoc.TryGetProperty("amount", out var amt) && amt.ValueKind == JsonValueKind.Number
                    ? amt.GetDecimal() : (decimal?)null;
                var regularAmount = spDoc.TryGetProperty("regular_amount", out var reg) && reg.ValueKind == JsonValueKind.Number
                    ? reg.GetDecimal() : (decimal?)null;

                if (promoAmount.HasValue && promoAmount.Value > 0 && regularAmount.HasValue && regularAmount.Value > promoAmount.Value)
                {
                    var itemToUpdate = await _db.MeliItems.FindAsync(ai.Id);
                    if (itemToUpdate is not null)
                    {
                        itemToUpdate.Price = promoAmount.Value;
                        itemToUpdate.OriginalPrice = regularAmount.Value;
                    }
                }
            }
            catch
            {
                // Skip individual item errors
            }
        }

        await _db.SaveChangesAsync();

        // Step 5: Sync category paths for items missing CategoryPath
        if (progressId is not null)
        {
            _syncProgress.Update(progressId, p =>
            {
                p.CurrentStep = $"{account.Nickname}: Sincronizando categorias...";
                p.Percentage = Math.Max(p.Percentage, 90);
            });
        }

        var itemsMissingCatPath = await _db.MeliItems
            .Where(i => i.MeliAccountId == account.Id && i.CategoryId != null && i.CategoryPath == null)
            .Select(i => new { i.Id, i.CategoryId })
            .ToListAsync();

        var categoryCache = new Dictionary<string, string>();
        foreach (var ci in itemsMissingCatPath)
        {
            if (string.IsNullOrEmpty(ci.CategoryId)) continue;
            try
            {
                if (!categoryCache.TryGetValue(ci.CategoryId, out var path))
                {
                    var catResponse = await http.GetAsync(
                        $"https://api.mercadolibre.com/categories/{ci.CategoryId}");
                    if (catResponse.IsSuccessStatusCode)
                    {
                        var catJson = await catResponse.Content.ReadAsStringAsync();
                        var catDoc = JsonDocument.Parse(catJson).RootElement;
                        if (catDoc.TryGetProperty("path_from_root", out var pathArr) && pathArr.ValueKind == JsonValueKind.Array)
                        {
                            var names = new List<string>();
                            foreach (var node in pathArr.EnumerateArray())
                            {
                                if (node.TryGetProperty("name", out var n) && n.ValueKind != JsonValueKind.Null)
                                    names.Add(n.GetString() ?? "");
                            }
                            path = string.Join(" > ", names);
                        }
                        else
                        {
                            path = ci.CategoryId;
                        }
                    }
                    else
                    {
                        path = ci.CategoryId;
                    }
                    categoryCache[ci.CategoryId] = path;
                }

                var itemToUpdate = await _db.MeliItems.FindAsync(ci.Id);
                if (itemToUpdate is not null)
                    itemToUpdate.CategoryPath = path;
            }
            catch { }
        }

        await _db.SaveChangesAsync();

        return synced;
    }

    // SKU: prioriza atributo SELLER_SKU, cae a seller_custom_field si no esta.
    private static string? ExtractSku(JsonElement element)
    {
        if (element.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
        {
            foreach (var attr in attrs.EnumerateArray())
            {
                var attrId = attr.TryGetProperty("id", out var aid) ? aid.GetString() : null;
                if (attrId == "SELLER_SKU")
                {
                    var v = attr.TryGetProperty("value_name", out var vn) && vn.ValueKind != JsonValueKind.Null
                        ? vn.GetString() : null;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
        }
        if (element.TryGetProperty("seller_custom_field", out var scf) && scf.ValueKind != JsonValueKind.Null)
        {
            var v = scf.GetString();
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return null;
    }

    // "Negro / Talle XL" a partir de attribute_combinations o values.
    private static string? ExtractVariationAttributes(JsonElement variation)
    {
        if (!variation.TryGetProperty("attribute_combinations", out var combos) || combos.ValueKind != JsonValueKind.Array)
            return null;
        var parts = new List<string>();
        foreach (var c in combos.EnumerateArray())
        {
            var name = c.TryGetProperty("value_name", out var vn) && vn.ValueKind != JsonValueKind.Null
                ? vn.GetString() : null;
            if (!string.IsNullOrEmpty(name)) parts.Add(name);
        }
        return parts.Count > 0 ? string.Join(" / ", parts) : null;
    }

    private async Task<int> UpsertItemAsync(int accountId, JsonElement item)
    {
        var meliItemId = item.GetProperty("id").GetString() ?? "";
        var title = item.GetProperty("title").GetString() ?? "Sin titulo";
        var price = item.TryGetProperty("price", out var pr) && pr.ValueKind != JsonValueKind.Null
            ? pr.GetDecimal() : 0m;
        var originalPrice = item.TryGetProperty("original_price", out var op) && op.ValueKind != JsonValueKind.Null
            ? op.GetDecimal() : (decimal?)null;
        var currencyId = item.TryGetProperty("currency_id", out var cur) && cur.ValueKind != JsonValueKind.Null
            ? cur.GetString() ?? "ARS" : "ARS";
        var availableQty = item.TryGetProperty("available_quantity", out var aq) && aq.ValueKind != JsonValueKind.Null
            ? aq.GetInt32() : 0;
        var soldQty = item.TryGetProperty("sold_quantity", out var sq) && sq.ValueKind != JsonValueKind.Null
            ? sq.GetInt32() : 0;
        var status = item.GetProperty("status").GetString() ?? "unknown";
        var condition = item.TryGetProperty("condition", out var cond) && cond.ValueKind != JsonValueKind.Null
            ? cond.GetString() : null;
        var listingTypeId = item.TryGetProperty("listing_type_id", out var lt) && lt.ValueKind != JsonValueKind.Null
            ? lt.GetString() : null;
        var categoryId = item.TryGetProperty("category_id", out var cat) && cat.ValueKind != JsonValueKind.Null
            ? cat.GetString() : null;
        var permalink = item.TryGetProperty("permalink", out var pl) && pl.ValueKind != JsonValueKind.Null
            ? pl.GetString() : null;
        var parentSku = ExtractSku(item);
        var dateCreated = item.TryGetProperty("date_created", out var dc) && dc.ValueKind != JsonValueKind.Null
            ? dc.GetDateTime() : (DateTime?)null;
        var lastUpdated = item.TryGetProperty("last_updated", out var lu) && lu.ValueKind != JsonValueKind.Null
            ? lu.GetDateTime() : (DateTime?)null;

        // Thumbnail - convert http to https
        var thumbnail = item.TryGetProperty("thumbnail", out var th) && th.ValueKind != JsonValueKind.Null
            ? th.GetString() : null;
        if (thumbnail != null && thumbnail.StartsWith("http://"))
            thumbnail = "https://" + thumbnail[7..];

        // User product grouping
        var userProductId = item.TryGetProperty("user_product_id", out var upid) && upid.ValueKind != JsonValueKind.Null
            ? upid.GetString() : null;

        // 2026-05-25: capturar logistic_type del shipping para distinguir Full vs no-Full.
        // Lo necesitamos para el sync de stock Full y para descontar bien las órdenes
        // (MeliFullStockSyncService + MeliStockSyncService).
        string? logisticType = null;
        if (item.TryGetProperty("shipping", out var shippingProp)
            && shippingProp.ValueKind == JsonValueKind.Object
            && shippingProp.TryGetProperty("logistic_type", out var ltProp)
            && ltProp.ValueKind == JsonValueKind.String)
        {
            logisticType = ltProp.GetString();
        }

        // Family grouping - try multiple locations
        string? familyId = null;
        string? familyName = null;

        if (item.TryGetProperty("family", out var family) && family.ValueKind != JsonValueKind.Null)
        {
            if (family.TryGetProperty("id", out var fid) && fid.ValueKind != JsonValueKind.Null)
                familyId = fid.GetString();
            if (family.TryGetProperty("name", out var fname) && fname.ValueKind != JsonValueKind.Null)
                familyName = fname.GetString();
        }

        // Fallback: check family_id at root level
        if (familyId is null && item.TryGetProperty("family_id", out var fid2) && fid2.ValueKind != JsonValueKind.Null)
        {
            familyId = fid2.ValueKind == JsonValueKind.Number
                ? fid2.GetInt64().ToString()
                : fid2.GetString();
        }
        if (familyName is null && item.TryGetProperty("family_name", out var fname2) && fname2.ValueKind != JsonValueKind.Null)
            familyName = fname2.GetString();

        // Installment tag from item tags
        string? installmentTag = null;
        if (item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            var installmentTags = new[] { "12x_campaign", "9x_campaign", "3x_campaign", "pcj-co-funded" };
            foreach (var tag in tags.EnumerateArray())
            {
                var tagStr = tag.GetString();
                if (tagStr is not null && installmentTags.Contains(tagStr))
                {
                    installmentTag = tagStr;
                    break;
                }
            }
        }

        // Free shipping
        var freeShipping = false;
        if (item.TryGetProperty("shipping", out var shipping) && shipping.ValueKind == JsonValueKind.Object)
        {
            freeShipping = shipping.TryGetProperty("free_shipping", out var fs) && fs.ValueKind == JsonValueKind.True;
        }

        // 2026-06-12: catalogo — MeLi marca catalog_listing=true cuando la publicacion compite
        // en el catalogo (el precio puede cambiar solo). Lo mostramos en /publicaciones.
        var catalogListing = item.TryGetProperty("catalog_listing", out var cl) && cl.ValueKind == JsonValueKind.True;

        // Detectar variantes. Si tiene variantes, grabamos una fila por variante (con su
        // VariationId y su SKU). Si no, grabamos una fila simple (VariationId NULL).
        var hasVariations = item.TryGetProperty("variations", out var variations)
            && variations.ValueKind == JsonValueKind.Array
            && variations.GetArrayLength() > 0;

        // Pictures por variante (variation.picture_ids -> resolver thumbnail).
        // Mantenemos un map id->secure_url usando item.pictures cuando esta disponible.
        Dictionary<string, string>? pictureMap = null;
        if (hasVariations && item.TryGetProperty("pictures", out var pics) && pics.ValueKind == JsonValueKind.Array)
        {
            pictureMap = new Dictionary<string, string>();
            foreach (var p in pics.EnumerateArray())
            {
                var pid = p.TryGetProperty("id", out var pidEl) && pidEl.ValueKind != JsonValueKind.Null
                    ? pidEl.GetString() : null;
                var secure = p.TryGetProperty("secure_url", out var su) && su.ValueKind != JsonValueKind.Null
                    ? su.GetString() : null;
                if (!string.IsNullOrEmpty(pid) && !string.IsNullOrEmpty(secure))
                    pictureMap[pid!] = secure!;
            }
        }

        if (!hasVariations)
        {
            await UpsertSingleRowAsync(
                accountId, meliItemId, variationId: null, variationAttributes: null,
                title, categoryId, price, originalPrice, currencyId,
                availableQty, soldQty, status, condition, listingTypeId,
                thumbnail, permalink, parentSku, userProductId, familyId, familyName,
                installmentTag, freeShipping, dateCreated, lastUpdated, catalogListing: catalogListing);
            return 1;
        }

        // Set de variation ids encontrados en MeLi para limpiar filas viejas.
        var seenVariationIds = new HashSet<string>();
        int rowsUpserted = 0;
        foreach (var v in variations.EnumerateArray())
        {
            string? vId = null;
            if (v.TryGetProperty("id", out var vidEl) && vidEl.ValueKind != JsonValueKind.Null)
            {
                vId = vidEl.ValueKind == JsonValueKind.Number
                    ? vidEl.GetInt64().ToString()
                    : vidEl.GetString();
            }
            if (string.IsNullOrEmpty(vId)) continue;
            seenVariationIds.Add(vId!);

            var vSku = ExtractSku(v) ?? parentSku;
            var vPrice = v.TryGetProperty("price", out var vpr) && vpr.ValueKind != JsonValueKind.Null
                ? vpr.GetDecimal() : price;
            var vOriginal = v.TryGetProperty("original_price", out var vop) && vop.ValueKind != JsonValueKind.Null
                ? vop.GetDecimal() : originalPrice;
            var vQty = v.TryGetProperty("available_quantity", out var vaq) && vaq.ValueKind != JsonValueKind.Null
                ? vaq.GetInt32() : 0;
            var vSold = v.TryGetProperty("sold_quantity", out var vsq) && vsq.ValueKind != JsonValueKind.Null
                ? vsq.GetInt32() : 0;
            var vAttrs = ExtractVariationAttributes(v);

            // Thumbnail por variante: tomar primer picture_id y resolverlo.
            var vThumb = thumbnail;
            if (pictureMap is not null && v.TryGetProperty("picture_ids", out var pidArr) && pidArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var pidEl in pidArr.EnumerateArray())
                {
                    var pidStr = pidEl.GetString();
                    if (!string.IsNullOrEmpty(pidStr) && pictureMap.TryGetValue(pidStr!, out var url))
                    {
                        vThumb = url.StartsWith("http://") ? "https://" + url[7..] : url;
                        break;
                    }
                }
            }

            await UpsertSingleRowAsync(
                accountId, meliItemId, vId, vAttrs,
                title, categoryId, vPrice, vOriginal, currencyId,
                vQty, vSold, status, condition, listingTypeId,
                vThumb, permalink, vSku, userProductId, familyId, familyName,
                installmentTag, freeShipping, dateCreated, lastUpdated, logisticType, catalogListing);
            rowsUpserted++;
        }

        // Limpieza: si la publicacion paso de simple -> con variantes, borrar la fila vieja sin variante.
        var stale = await _db.MeliItems
            .Where(i => i.MeliItemId == meliItemId && i.VariationId == null)
            .ToListAsync();
        if (stale.Count > 0) _db.MeliItems.RemoveRange(stale);

        // Limpieza: variantes que dejaron de existir en MeLi.
        var existingVariants = await _db.MeliItems
            .Where(i => i.MeliItemId == meliItemId && i.VariationId != null)
            .ToListAsync();
        var toRemove = existingVariants.Where(i => !seenVariationIds.Contains(i.VariationId!)).ToList();
        if (toRemove.Count > 0) _db.MeliItems.RemoveRange(toRemove);

        return rowsUpserted;
    }

    private async Task UpsertSingleRowAsync(
        int accountId, string meliItemId, string? variationId, string? variationAttributes,
        string title, string? categoryId, decimal price, decimal? originalPrice, string currencyId,
        int availableQty, int soldQty, string status, string? condition, string? listingTypeId,
        string? thumbnail, string? permalink, string? sku, string? userProductId,
        string? familyId, string? familyName, string? installmentTag, bool freeShipping,
        DateTime? dateCreated, DateTime? lastUpdated, string? logisticType = null,
        bool catalogListing = false)
    {
        var existing = await _db.MeliItems
            .FirstOrDefaultAsync(i => i.MeliItemId == meliItemId && i.VariationId == variationId);

        // 2026-06-08: AUTO-LINKEO por SKU.
        // Cuando MeLi trae una publicación con un SKU que YA existe en Cafe_Combos/Cafe_Productos,
        // la asociamos automáticamente. Sin esto, las publicaciones nuevas (creadas en MeLi
        // después del primer linkeo masivo) quedan "sin vincular" y necesitan linkeo manual.
        // Solo busca cuando: hay SKU, y el item no tiene linkeo (CafeComboId+CafeProductoId nulos).
        int? autoComboId = null;
        int? autoProductoId = null;
        var needsAutoLink = !string.IsNullOrWhiteSpace(sku)
            && (existing is null || (existing.CafeComboId is null && existing.CafeProductoId is null));
        if (needsAutoLink)
        {
            // Prioridad: combo > producto (el combo es más específico).
            var combo = await _db.Set<CafeCombo>().AsNoTracking()
                .Where(c => c.Sku == sku).Select(c => (int?)c.Id).FirstOrDefaultAsync();
            if (combo.HasValue) autoComboId = combo;
            else
            {
                var prod = await _db.CafeProductos.AsNoTracking()
                    .Where(p => p.Sku == sku).Select(p => (int?)p.Id).FirstOrDefaultAsync();
                if (prod.HasValue) autoProductoId = prod;
            }

            // 2026-06-08: además del CafeComboId/CafeProductoId, poblar MeliItemComponentes
            // (lo que mira /publicaciones para el chip "Linkeado" y MeliStockPushService para el push de stock).
            // Sin esto, el item aparece "sin vincular" aunque CafeComboId esté seteado.
            var yaTieneComp = await _db.Set<MeliItemComponente>().AsNoTracking()
                .AnyAsync(c => c.MeliItemId == meliItemId);
            if (!yaTieneComp)
            {
                if (autoComboId.HasValue)
                {
                    var comps = await _db.Set<CafeComboItem>().AsNoTracking()
                        .Where(ci => ci.ComboId == autoComboId.Value)
                        .Select(ci => new { ci.ProductoId, ci.Cantidad })
                        .ToListAsync();
                    foreach (var ci in comps)
                    {
                        _db.Set<MeliItemComponente>().Add(new MeliItemComponente
                        {
                            MeliItemId = meliItemId,
                            CafeProductoId = ci.ProductoId,
                            Cantidad = ci.Cantidad,
                            Source = "auto-link-sync",
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }
                else if (autoProductoId.HasValue)
                {
                    _db.Set<MeliItemComponente>().Add(new MeliItemComponente
                    {
                        MeliItemId = meliItemId,
                        CafeProductoId = autoProductoId.Value,
                        Cantidad = 1m,
                        Source = "auto-link-sync",
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }
        }

        if (existing is not null)
        {
            // DETECTOR DE CAMBIOS 2026-05-23: capturar valores anteriores ANTES de actualizar
            // para detectar cambios de precio y status. Solo si esto NO es el primer registro
            // (es decir, ya teníamos un valor anterior real).
            var oldPrice = existing.Price;
            var oldStatus = existing.Status;
            await _detector.LogPriceChangeAsync(meliItemId, accountId, sku, title, oldPrice, price, "sync");
            await _detector.LogStatusChangeAsync(meliItemId, accountId, sku, title, oldStatus, status, "sync");

            existing.Title = title;
            existing.CategoryId = categoryId;
            existing.Price = price;
            existing.OriginalPrice = originalPrice;
            existing.CurrencyId = currencyId;
            existing.AvailableQuantity = availableQty;
            existing.SoldQuantity = soldQty;
            existing.Status = status;
            existing.Condition = condition;
            existing.ListingTypeId = listingTypeId;
            existing.Thumbnail = thumbnail;
            existing.Permalink = permalink;
            existing.Sku = sku;
            existing.UserProductId = userProductId;
            existing.FamilyId = familyId;
            existing.FamilyName = familyName;
            existing.InstallmentTag = installmentTag;
            existing.FreeShipping = freeShipping;
            existing.CatalogListing = catalogListing;
            existing.LastUpdated = lastUpdated;
            existing.VariationAttributes = variationAttributes;
            // Solo overrideamos LogisticType si vino del API. Si vino null (no estaba en la response),
            // mantenemos el valor cacheado para no perder info.
            if (logisticType != null) existing.LogisticType = logisticType;
            // 2026-06-08: aplicar auto-linkeo si se encontró match por SKU
            if (autoComboId.HasValue) existing.CafeComboId = autoComboId;
            else if (autoProductoId.HasValue) existing.CafeProductoId = autoProductoId;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _db.MeliItems.Add(new MeliItem
            {
                MeliItemId = meliItemId,
                MeliAccountId = accountId,
                VariationId = variationId,
                VariationAttributes = variationAttributes,
                Title = title,
                CategoryId = categoryId,
                Price = price,
                OriginalPrice = originalPrice,
                CurrencyId = currencyId,
                AvailableQuantity = availableQty,
                SoldQuantity = soldQty,
                Status = status,
                Condition = condition,
                ListingTypeId = listingTypeId,
                Thumbnail = thumbnail,
                Permalink = permalink,
                Sku = sku,
                UserProductId = userProductId,
                FamilyId = familyId,
                FamilyName = familyName,
                InstallmentTag = installmentTag,
                FreeShipping = freeShipping,
                CatalogListing = catalogListing,
                DateCreated = dateCreated,
                LastUpdated = lastUpdated,
                LogisticType = logisticType,
                // 2026-06-08: auto-linkeo al crear si el SKU ya existe en el sistema
                CafeComboId = autoComboId,
                CafeProductoId = autoProductoId
            });
        }
    }

    public async Task<ListingCostDto> GetListingCostsAsync(string meliItemId)
    {
        var item = await _db.MeliItems
            .Include(i => i.MeliAccount)
            .FirstOrDefaultAsync(i => i.MeliItemId == meliItemId);

        if (item is null)
            throw new Exception("Item no encontrado en la base de datos");

        if (item.MeliAccount is null)
            throw new Exception("Cuenta de MeLi no encontrada");

        var token = await _accountService.GetValidTokenAsync(item.MeliAccount);
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Use item.Price (precio final / lo que paga el comprador) para calcular costos
        // Si hay promocion, Price ya es el precio con descuento
        var price = item.Price;

        // Build query params
        var queryParams = $"price={price.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrEmpty(item.CategoryId))
            queryParams += $"&category_id={item.CategoryId}";
        if (!string.IsNullOrEmpty(item.ListingTypeId))
            queryParams += $"&listing_type_id={item.ListingTypeId}";
        // 2026-07-01: FIX crítico — sin installment_tag MeLi devolvía el cargo por cuotas del default
        // (6 cuotas). Publis con 3, 6, 9, 12 cuotas mostraban todas el mismo costo por cuotas y por
        // ende el mismo margen/ganancia. Ahora cada modalidad trae su costo real.
        if (!string.IsNullOrEmpty(item.InstallmentTag))
            queryParams += $"&installment_tag={item.InstallmentTag}";

        var url = $"https://api.mercadolibre.com/sites/MLA/listing_prices?{queryParams}";
        var resp = await http.GetAsync(url);

        var result = new ListingCostDto
        {
            Price = price,
            CurrencyId = item.CurrencyId ?? "ARS",
            ListingTypeId = item.ListingTypeId
        };

        if (resp.IsSuccessStatusCode)
        {
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            // The response may be an array (one entry per listing type) or a single object
            JsonElement root;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                // Find the matching listing type
                root = doc.RootElement.EnumerateArray()
                    .FirstOrDefault(e => e.TryGetProperty("listing_type_id", out var lt)
                        && lt.GetString() == item.ListingTypeId);
                if (root.ValueKind == JsonValueKind.Undefined)
                    root = doc.RootElement[0]; // fallback to first
            }
            else
            {
                root = doc.RootElement;
            }

            result.SaleFeeAmount = root.TryGetProperty("sale_fee_amount", out var sfa) && sfa.ValueKind == JsonValueKind.Number
                ? sfa.GetDecimal() : 0;
            result.ListingFeeAmount = root.TryGetProperty("listing_fee_amount", out var lfa) && lfa.ValueKind == JsonValueKind.Number
                ? lfa.GetDecimal() : 0;
            result.ListingTypeName = root.TryGetProperty("listing_type_name", out var ltn)
                ? ltn.GetString() : null;

            if (root.TryGetProperty("sale_fee_details", out var sfd))
            {
                result.FixedFee = sfd.TryGetProperty("fixed_fee", out var ff) && ff.ValueKind == JsonValueKind.Number
                    ? ff.GetDecimal() : 0;
                result.PercentageFee = sfd.TryGetProperty("percentage_fee", out var pf) && pf.ValueKind == JsonValueKind.Number
                    ? pf.GetDecimal() : 0;
                result.FinancingFee = sfd.TryGetProperty("financing_add_on_fee", out var faf) && faf.ValueKind == JsonValueKind.Number
                    ? faf.GetDecimal() : 0;
            }

            // 2026-07-02 FIX CRÍTICO: la API listing_prices para gold_pro devuelve SIEMPRE el
            // financing_add_on_fee del default (6 cuotas = 12,3%) sin importar el installment_tag.
            // Verificado con Integraly y con datos reales: MeLi cobra distinto financing por modalidad.
            // Aplicamos tabla real (los valores son fijos por modalidad, no varían por categoría/precio).
            var meliPctFee = sfd.TryGetProperty("meli_percentage_fee", out var mpf) && mpf.ValueKind == JsonValueKind.Number
                ? mpf.GetDecimal() : 14.30m;
            var realFinancing = GetFinancingRealPct(item.ListingTypeId, item.InstallmentTag);
            if (realFinancing.HasValue)
            {
                result.FinancingFee = realFinancing.Value;
                result.PercentageFee = meliPctFee + realFinancing.Value;
                // Fixed fee = $3050 estándar; excepto pcj-co-funded (interés bajo) = $0.
                result.FixedFee = item.InstallmentTag == "pcj-co-funded" ? 0m : 3050m;
                result.SaleFeeAmount = Math.Round(price * result.PercentageFee / 100m + result.FixedFee, 2);
            }
        }

        // Step 2: Get shipping costs (only if item offers free shipping - seller pays)
        if (item.FreeShipping)
        {
            try
            {
                var userId = item.MeliAccount.MeliUserId;
                var shippingUrl = $"https://api.mercadolibre.com/users/{userId}/shipping_options/free?item_id={meliItemId}&item_price={price.ToString(System.Globalization.CultureInfo.InvariantCulture)}&free_shipping=true&listing_type_id={item.ListingTypeId}";
                Console.WriteLine($"[shipping-cost] {meliItemId} → GET {shippingUrl}");
                var shippingResp = await http.GetAsync(shippingUrl);
                Console.WriteLine($"[shipping-cost] {meliItemId} ← HTTP {(int)shippingResp.StatusCode}");
                if (shippingResp.IsSuccessStatusCode)
                {
                    var shippingJson = await shippingResp.Content.ReadAsStringAsync();
                    Console.WriteLine($"[shipping-cost] {meliItemId} body: {shippingJson.Substring(0, Math.Min(200, shippingJson.Length))}");
                    using var shippingDoc = JsonDocument.Parse(shippingJson);
                    if (shippingDoc.RootElement.TryGetProperty("coverage", out var coverage)
                        && coverage.TryGetProperty("all_country", out var allCountry)
                        && allCountry.TryGetProperty("list_cost", out var listCost)
                        && listCost.ValueKind == JsonValueKind.Number)
                    {
                        result.ShippingCost = listCost.GetDecimal();
                        Console.WriteLine($"[shipping-cost] {meliItemId} ✓ set ShippingCost = {result.ShippingCost}");
                    }
                    else
                    {
                        Console.WriteLine($"[shipping-cost] {meliItemId} ⚠ no encontró coverage.all_country.list_cost en la respuesta");
                    }
                }
                else
                {
                    var err = await shippingResp.Content.ReadAsStringAsync();
                    Console.WriteLine($"[shipping-cost] {meliItemId} ❌ error {(int)shippingResp.StatusCode}: {err.Substring(0, Math.Min(300, err.Length))}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[shipping-cost] {meliItemId} ❌ EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"[shipping-cost] {meliItemId} skip (FreeShipping=false)");
        }


        // Net amount = price - sale_fee - listing_fee - taxes
        // NetAmount without taxes (taxes calculated in frontend with user percentage)
        result.NetAmount = Math.Round(price - result.SaleFeeAmount - result.ListingFeeAmount - result.ShippingCost, 2);

        // 2026-06-19: cachear el sale_fee en MeliItems para que el listado /publicaciones
        // pueda calcular el neto real sin tener que llamar a la API por item. Se sobreescribe
        // siempre que se llama este metodo.
        item.SaleFeeAmount = result.SaleFeeAmount;
        item.SaleFeeFixedFee = result.FixedFee;
        item.SaleFeePercentageFee = result.PercentageFee;
        item.SaleFeeFinancingFee = result.FinancingFee;
        item.SaleFeeListingFee = result.ListingFeeAmount;
        item.SaleFeePriceSnapshot = price;
        item.SaleFeeCapturedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return result;
    }

    /// <summary>2026-06-19: refresca el sale_fee de un item llamando GetListingCostsAsync
    /// (que ya guarda en DB el resultado). Wrapper simple para usar desde controllers y jobs.</summary>
    public async Task<bool> RefreshSaleFeeAsync(string meliItemId)
    {
        try
        {
            var dto = await GetListingCostsAsync(meliItemId);
            return dto.SaleFeeAmount > 0;
        }
        catch { return false; }
    }

    public async Task<MeliItemDetailsDto?> GetItemDetailsAsync(string meliItemId)
    {
        var item = await _db.MeliItems
            .Include(i => i.MeliAccount)
            .FirstOrDefaultAsync(i => i.MeliItemId == meliItemId);
        if (item?.MeliAccount is null) return null;

        var token = await _accountService.GetValidTokenAsync(item.MeliAccount);
        if (token is null) return null;

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var pictures = new List<string>();
        string? description = null;

        try
        {
            // Fetch item details (includes pictures array)
            var itemResp = await http.GetAsync($"https://api.mercadolibre.com/items/{meliItemId}");
            if (itemResp.IsSuccessStatusCode)
            {
                var json = await itemResp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("pictures", out var pics) && pics.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pic in pics.EnumerateArray())
                    {
                        if (pictures.Count >= 3) break;
                        var url = pic.TryGetProperty("secure_url", out var su) ? su.GetString()
                                : pic.TryGetProperty("url", out var u) ? u.GetString() : null;
                        if (!string.IsNullOrEmpty(url))
                            pictures.Add(url);
                    }
                }
            }
        }
        catch { /* pictures fetch failed, continue */ }

        try
        {
            // Fetch description
            var descResp = await http.GetAsync($"https://api.mercadolibre.com/items/{meliItemId}/description");
            if (descResp.IsSuccessStatusCode)
            {
                var json = await descResp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("plain_text", out var pt))
                    description = pt.GetString();
                if (string.IsNullOrWhiteSpace(description) && doc.RootElement.TryGetProperty("text", out var t))
                    description = t.GetString();
            }
        }
        catch { /* description fetch failed, continue */ }

        return new MeliItemDetailsDto(pictures, description);
    }

    public async Task<List<CategoryPredictionDto>> PredictCategoryAsync(string title, int meliAccountId)
    {
        var account = await _db.MeliAccounts.FindAsync(meliAccountId);
        if (account is null) throw new Exception("Cuenta no encontrada");
        var token = await _accountService.GetValidTokenAsync(account);
        if (token is null) throw new Exception("Token expirado. Reconecta la cuenta.");
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var encodedTitle = Uri.EscapeDataString(title);
        var response = await http.GetAsync($"https://api.mercadolibre.com/sites/MLA/domain_discovery/search?q={encodedTitle}");
        if (!response.IsSuccessStatusCode)
            return new List<CategoryPredictionDto>();
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var results = new List<CategoryPredictionDto>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (results.Count >= 3) break;
            var categoryId = item.TryGetProperty("category_id", out var cid) ? cid.GetString() ?? "" : "";
            var categoryName = item.TryGetProperty("category_name", out var cn) ? cn.GetString() ?? "" : "";
            var categoryPath = categoryName;
            try
            {
                var catResp = await http.GetAsync($"https://api.mercadolibre.com/categories/{categoryId}");
                if (catResp.IsSuccessStatusCode)
                {
                    var catJson = await catResp.Content.ReadAsStringAsync();
                    using var catDoc = JsonDocument.Parse(catJson);
                    if (catDoc.RootElement.TryGetProperty("path_from_root", out var pathArr) && pathArr.ValueKind == JsonValueKind.Array)
                    {
                        var names = new List<string>();
                        foreach (var node in pathArr.EnumerateArray())
                        {
                            if (node.TryGetProperty("name", out var n) && n.ValueKind != JsonValueKind.Null)
                                names.Add(n.GetString() ?? "");
                        }
                        categoryPath = string.Join(" > ", names);
                    }
                }
            }
            catch { }
            results.Add(new CategoryPredictionDto { CategoryId = categoryId, CategoryName = categoryName, CategoryPath = categoryPath, Probability = 0 });
        }
        return results;
    }

    public async Task<List<CategoryAttributeDto>> GetCategoryAttributesAsync(string categoryId)
    {
        var http = _httpFactory.CreateClient();
        var response = await http.GetAsync($"https://api.mercadolibre.com/categories/{categoryId}/attributes");
        if (!response.IsSuccessStatusCode)
            return new List<CategoryAttributeDto>();
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var results = new List<CategoryAttributeDto>();
        foreach (var attr in doc.RootElement.EnumerateArray())
        {
            var id = attr.TryGetProperty("id", out var aid) ? aid.GetString() ?? "" : "";
            var name = attr.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
            var valueType = attr.TryGetProperty("value_type", out var vt) ? vt.GetString() ?? "string" : "string";
            var required = false;
            if (attr.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            {
                required = (tags.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.True)
                    || (tags.TryGetProperty("catalog_required", out var creq) && creq.ValueKind == JsonValueKind.True);
            }
            if (attr.TryGetProperty("tags", out var tags3) && tags3.ValueKind == JsonValueKind.Object)
            {
                if (tags3.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) continue;
                if (tags3.TryGetProperty("read_only", out var ro) && ro.ValueKind == JsonValueKind.True) continue;
            }
            var values = new List<AttributeValueOption>();
            if (attr.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
            {
                foreach (var val in vals.EnumerateArray())
                {
                    var valId = val.TryGetProperty("id", out var vid) ? vid.GetString() ?? "" : "";
                    var valName = val.TryGetProperty("name", out var vn) ? vn.GetString() ?? "" : "";
                    values.Add(new AttributeValueOption { Id = valId, Name = valName });
                }
            }
            string? defaultValue = null;
            if (attr.TryGetProperty("default_value", out var dv) && dv.ValueKind != JsonValueKind.Null)
                defaultValue = dv.GetString();
            results.Add(new CategoryAttributeDto { Id = id, Name = name, ValueType = valueType, Required = required, Values = values, DefaultValue = defaultValue });
        }
        return results.OrderByDescending(a => a.Required).ThenBy(a => a.Name).ToList();
    }

    public async Task<PublishItemResponse> PublishItemAsync(PublishItemRequest request)
    {
        var account = await _db.MeliAccounts.FindAsync(request.MeliAccountId);
        if (account is null) return new PublishItemResponse { Error = "Cuenta no encontrada" };
        var token = await _accountService.GetValidTokenAsync(account);
        if (token is null) return new PublishItemResponse { Error = "Token expirado. Reconecta la cuenta." };
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            // Detectar si la cuenta usa el nuevo modelo user_product_seller
            var isUserProductSeller = await IsUserProductSellerAsync(http, account.MeliUserId);

            var pictures = new List<object>();
            foreach (var picUrl in request.PictureUrls.Where(u => !string.IsNullOrEmpty(u)))
            {
                if (picUrl.StartsWith("data:"))
                {
                    var uploadedUrl = await UploadPictureToMeliAsync(http, picUrl);
                    if (uploadedUrl is not null) pictures.Add(new { source = uploadedUrl });
                }
                else
                {
                    pictures.Add(new { source = picUrl });
                }
            }
            var attributes = request.Attributes
                .Where(a => !string.IsNullOrEmpty(a.ValueId) || !string.IsNullOrEmpty(a.ValueName))
                .Select(a => a.ValueId is not null
                    ? (object)new { id = a.Id, value_id = a.ValueId }
                    : new { id = a.Id, value_name = a.ValueName })
                .ToList();
            var itemBody = new Dictionary<string, object>
            {
                ["category_id"] = request.CategoryId,
                ["price"] = request.Price,
                ["currency_id"] = "ARS",
                ["available_quantity"] = request.AvailableQuantity,
                ["buying_mode"] = "buy_it_now",
                ["condition"] = request.Condition,
                ["listing_type_id"] = request.ListingTypeId,
                ["shipping"] = new { mode = "me2", free_shipping = request.FreeShipping }
            };
            // Cuentas con user_product_seller usan family_name en vez de title
            if (isUserProductSeller)
                itemBody["family_name"] = request.FamilyName ?? request.Title;
            else
                itemBody["title"] = request.Title;
            if (pictures.Any()) itemBody["pictures"] = pictures;
            if (attributes.Any()) itemBody["attributes"] = attributes;
            var product = await _db.Products.FindAsync(request.ProductId);
            if (product?.Sku is not null) itemBody["seller_custom_field"] = product.Sku;
            var jsonBody = JsonSerializer.Serialize(itemBody);
            var bodyContent = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            var meliResponse = await http.PostAsync("https://api.mercadolibre.com/items", bodyContent);
            if (!meliResponse.IsSuccessStatusCode)
            {
                var errorBody = await meliResponse.Content.ReadAsStringAsync();
                var statusCode = (int)meliResponse.StatusCode;

                if (statusCode == 400)
                {
                    // CASO 1: Titulo demasiado largo - truncar y reintentar
                    if (!request.TitleTruncated && TryExtractMaxTitleLength(errorBody, out int maxLen))
                    {
                        request.Title = request.Title.Length > maxLen ? request.Title[..maxLen] : request.Title;
                        if (request.FamilyName != null && request.FamilyName.Length > maxLen)
                            request.FamilyName = request.FamilyName[..maxLen];
                        request.TitleTruncated = true;
                        return await PublishItemAsync(request);
                    }

                    // CASO 2: Atributos faltantes/obligatorios - sugerir con IA y reintentar
                    if (!request.AiRetried && IsAttributeRequiredError(errorBody))
                    {
                        try
                        {
                            var aiAttributes = await SuggestAttributesWithAiAsync(request, http);
                            if (aiAttributes.Any())
                            {
                                var existingIds = request.Attributes.Select(a => a.Id).ToHashSet();
                                foreach (var suggestion in aiAttributes)
                                {
                                    if (!existingIds.Contains(suggestion.Id))
                                        request.Attributes.Add(suggestion);
                                }
                                request.AiRetried = true;
                                return await PublishItemAsync(request);
                            }
                        }
                        catch { }
                    }

                    // CASO 3: Valor de atributo no resuelto - quitar value_id y dejar solo value_name
                    if (!request.ValuesStripped && IsAttributeValueNotResolvedError(errorBody))
                    {
                        foreach (var attr in request.Attributes)
                        {
                            if (attr.ValueId != null)
                            {
                                if (string.IsNullOrEmpty(attr.ValueName))
                                    attr.ValueName = attr.ValueId;
                                attr.ValueId = null;
                            }
                        }
                        request.ValuesStripped = true;
                        return await PublishItemAsync(request);
                    }

                    // CASO 4: GTIN requerido - bypass: modificar BRAND temporalmente, publicar, luego restaurar
                    if (!request.GtinBypassed && IsGtinRequiredError(errorBody))
                    {
                        var brandAttr = request.Attributes.FirstOrDefault(a => a.Id == "BRAND");
                        if (brandAttr != null)
                        {
                            request.OriginalBrand = brandAttr.ValueName ?? brandAttr.ValueId;
                            brandAttr.ValueName = "XXX-" + (brandAttr.ValueName ?? brandAttr.ValueId ?? "SinMarca");
                            brandAttr.ValueId = null; // Forzar value_name para el bypass
                        }
                        else
                        {
                            // Si no tiene BRAND, agregar uno temporal
                            request.OriginalBrand = null;
                            request.Attributes.Add(new PublishAttributeDto { Id = "BRAND", ValueName = "XXX-SinMarca" });
                        }
                        request.GtinBypassed = true;
                        return await PublishItemAsync(request);
                    }
                }

                return new PublishItemResponse { Error = FormatMeliError(errorBody) };
            }
            var responseJson = await meliResponse.Content.ReadAsStringAsync();
            using var responseDoc = JsonDocument.Parse(responseJson);
            var meliItemId = responseDoc.RootElement.GetProperty("id").GetString() ?? "";
            var permalink = responseDoc.RootElement.TryGetProperty("permalink", out var pl) ? pl.GetString() : null;
            if (!string.IsNullOrWhiteSpace(request.Description))
            {
                try
                {
                    var descBody = JsonSerializer.Serialize(new { plain_text = request.Description });
                    var descContent = new StringContent(descBody, Encoding.UTF8, "application/json");
                    await http.PostAsync($"https://api.mercadolibre.com/items/{meliItemId}/description", descContent);
                }
                catch { }
            }
            // Obtener titulo real (MeLi lo genera en cuentas user_product_seller)
            var actualTitle = responseDoc.RootElement.TryGetProperty("title", out var titleProp)
                ? titleProp.GetString() ?? request.Title : request.Title;
            var respUserProductId = responseDoc.RootElement.TryGetProperty("user_product_id", out var upProp) && upProp.ValueKind != JsonValueKind.Null
                ? upProp.GetString() : null;
            var respFamilyName = responseDoc.RootElement.TryGetProperty("family_name", out var fnProp) && fnProp.ValueKind != JsonValueKind.Null
                ? fnProp.GetString() : null;
            var newItem = new MeliItem
            {
                MeliItemId = meliItemId, MeliAccountId = request.MeliAccountId, Title = actualTitle,
                CategoryId = request.CategoryId, Price = request.Price, CurrencyId = "ARS",
                AvailableQuantity = request.AvailableQuantity, Status = "active", Condition = request.Condition,
                ListingTypeId = request.ListingTypeId, FreeShipping = request.FreeShipping,
                Permalink = permalink, Sku = product?.Sku, ProductId = request.ProductId,
                UserProductId = respUserProductId, FamilyName = respFamilyName,
                DateCreated = DateTime.UtcNow, LastUpdated = DateTime.UtcNow
            };
            if (responseDoc.RootElement.TryGetProperty("thumbnail", out var th))
            {
                var thumb = th.GetString();
                if (thumb?.StartsWith("http://") == true) thumb = "https://" + thumb[7..];
                newItem.Thumbnail = thumb;
            }
            _db.MeliItems.Add(newItem);
            await _db.SaveChangesAsync();

            // Si se uso el bypass de GTIN, restaurar la marca original via PUT
            if (request.GtinBypassed)
            {
                try
                {
                    var brandFix = new List<object>();
                    if (request.OriginalBrand != null)
                        brandFix.Add(new { id = "BRAND", value_name = request.OriginalBrand });
                    else
                    {
                        // Buscar la marca original del atributo (sin el prefijo XXX-)
                        var currentBrand = request.Attributes.FirstOrDefault(a => a.Id == "BRAND");
                        var fixedName = currentBrand?.ValueName?.StartsWith("XXX-") == true
                            ? currentBrand.ValueName[4..] : currentBrand?.ValueName;
                        if (!string.IsNullOrEmpty(fixedName))
                            brandFix.Add(new { id = "BRAND", value_name = fixedName });
                    }
                    if (brandFix.Any())
                    {
                        var fixBody = JsonSerializer.Serialize(new { attributes = brandFix });
                        var fixContent = new StringContent(fixBody, Encoding.UTF8, "application/json");
                        await http.PutAsync($"https://api.mercadolibre.com/items/{meliItemId}", fixContent);
                    }
                }
                catch { /* Si falla la restauracion de marca, la publicacion ya fue creada */ }
            }

            await _auditLog.LogAsync("MeliItem", meliItemId, "PUBLISH",
                JsonSerializer.Serialize(new { titulo = request.Title, cuenta = account.Nickname, categoria = request.CategoryId }));
            return new PublishItemResponse { Success = true, MeliItemId = meliItemId, Permalink = permalink };
        }
        catch (Exception ex)
        {
            return new PublishItemResponse { Error = ex.Message };
        }
    }

    private async Task<bool> IsUserProductSellerAsync(HttpClient http, long meliUserId)
    {
        try
        {
            var response = await http.GetAsync($"https://api.mercadolibre.com/users/{meliUserId}");
            if (!response.IsSuccessStatusCode) return false;
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tags.EnumerateArray())
                {
                    if (tag.GetString() == "user_product_seller") return true;
                }
            }
            return false;
        }
        catch { return false; }
    }

    private async Task<string?> UploadPictureToMeliAsync(HttpClient http, string dataUri)
    {
        try
        {
            var commaIdx = dataUri.IndexOf(',');
            if (commaIdx < 0) return null;
            var meta = dataUri[..commaIdx];
            var base64Data = dataUri[(commaIdx + 1)..];
            var bytes = Convert.FromBase64String(base64Data);
            var contentType = "image/jpeg";
            if (meta.Contains("image/png")) contentType = "image/png";
            else if (meta.Contains("image/webp")) contentType = "image/webp";
            using var formContent = new MultipartFormDataContent();
            var imageContent = new ByteArrayContent(bytes);
            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            formContent.Add(imageContent, "file", "image.jpg");
            var response = await http.PostAsync("https://api.mercadolibre.com/pictures/items/upload", formContent);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("variations", out var vars) && vars.ValueKind == JsonValueKind.Array
                ? vars.EnumerateArray().FirstOrDefault().TryGetProperty("secure_url", out var su) ? su.GetString() : null
                : doc.RootElement.TryGetProperty("secure_url", out var su2) ? su2.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Propaga el stock de un producto a todas sus publicaciones activas en MercadoLibre.
    /// Actualiza via API de MeLi y luego en la base de datos local.
    /// </summary>
    /// <summary>
    /// Empuja a MeLi los datos del producto O combo vinculado a la publicacion.
    /// Para combos, el precio se calcula segun el modo (manual/auto/percent) y el
    /// stock es el minimo disponible considerando la cantidad de cada item.
    /// </summary>
    public async Task<MeliPushResult> PushFromProductAsync(int meliItemId, bool pushPrice = true, bool pushStock = true, decimal? overridePrice = null)
    {
        var item = await _db.MeliItems
            .Include(i => i.MeliAccount)
            .FirstOrDefaultAsync(i => i.Id == meliItemId);

        if (item is null) throw new InvalidOperationException("Publicacion no encontrada.");
        if (item.ProductId is null && item.ComboId is null && item.CafeProductoId is null)
            throw new InvalidOperationException("Esta publicacion no tiene producto, combo ni cafe vinculado todavia.");
        if (item.MeliAccount is null) throw new InvalidOperationException("La cuenta de MeLi no esta cargada.");

        // Construir payload segun la fuente vinculada.
        var payloadDict = new Dictionary<string, object>();
        decimal? priceWithVat = null;
        int? stockToPush = null;
        // Referencia al CafeProducto vinculado (si aplica). La capturamos aca afuera para
        // poder marcarle StockChangedAt + LastPushedToMeli despues del push exitoso y asi
        // activarlo en el radar del background sync hacia adelante.
        Models.CafeProducto? linkedCafe = null;

        if (item.CafeProductoId.HasValue)
        {
            var cafe = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == item.CafeProductoId.Value);
            if (cafe is null) throw new InvalidOperationException("El cafe vinculado no existe.");
            linkedCafe = cafe;
            var settings = await _db.CafeSettings.FindAsync(1) ?? new Models.CafeSetting { Id = 1 };
            var formato = string.IsNullOrEmpty(item.CafeFormato) ? "1KG" : item.CafeFormato;
            // 2026-05-29: regla aclarada. Para Full no usamos el endpoint estándar de items
            // (MeLi rechaza available_quantity), pero SÍ empujamos al "selling_address" con
            // stock de 9 de Abril. Se decide más abajo después del live read (liveIsFull).

            if (pushPrice)
            {
                // 2026-05-29: si vino overridePrice (frontend con ajuste Δ/% + redondeo aplicados),
                // usarlo tal cual. Sino, calcular desde PrecioOtro (consumidor final, NUNCA Pvp1 mayorista).
                if (overridePrice.HasValue && overridePrice.Value > 0)
                {
                    priceWithVat = overridePrice.Value;
                }
                else
                {
                    // 2026-06-11: si la MLA tiene PrecioIndependiente con factor, usa Factor x PrecioOtro
                    var cfgPI = await _db.MeliItemSyncConfigs.FindAsync(item.MeliItemId);
                    if (cfgPI is not null && cfgPI.PrecioIndependiente && cfgPI.PrecioFactor.HasValue && cfgPI.PrecioFactor.Value > 0m)
                    {
                        var baseOtro = cafe.PrecioOtro ?? cafe.Pvp2 ?? cafe.PrecioPorKg ?? 0m;
                        priceWithVat = Math.Round(baseOtro * cfgPI.PrecioFactor.Value, 2, MidpointRounding.AwayFromZero);
                    }
                    else
                    {
                        var listaKg = cafe.PrecioOtro ?? cafe.Pvp2 ?? cafe.PrecioPorKg ?? 0m;
                        decimal precioSinIva = formato switch
                        {
                            "1KG" => listaKg,
                            "MEDIO" => Math.Round(listaKg / 2m + settings.CostoFraccionamiento, 2, MidpointRounding.AwayFromZero),
                            "CUARTO" => Math.Round(listaKg / 4m + settings.CostoFraccionamiento, 2, MidpointRounding.AwayFromZero),
                            _ => listaKg
                        };
                        var rate = cafe.IvaPct;
                        priceWithVat = rate > 0m
                            ? Math.Round(precioSinIva * (1m + rate / 100m), 2, MidpointRounding.AwayFromZero)
                            : precioSinIva;
                    }
                }
                payloadDict["price"] = priceWithVat;
            }
            if (pushStock)
            {
                // 2026-05-29: el stock SIEMPRE se publica desde el depósito 9 de Abril (DepositoId=1),
                // sin sumar Full MeLi.
                const int DEPOSITO_9_ABRIL_ID = 1;
                var spd9 = await _db.CafeStockPorDeposito
                    .FirstOrDefaultAsync(s => s.ProductoId == cafe.Id && s.DepositoId == DEPOSITO_9_ABRIL_ID);

                // 2026-05-29 (post-bug ABEAZU): diferenciar CAFE (stock en gramos) vs OTROS (stock en unidades).
                // El bug previo trataba todo como café -> dividía StockGramos/1000 -> para OTROS daba 0 -> MeLi pausaba.
                if (string.Equals(cafe.Categoria, "CAFE", StringComparison.OrdinalIgnoreCase))
                {
                    int gramosPorUnidad = formato switch { "MEDIO" => 500, "CUARTO" => 250, _ => 1000 };
                    var stockGramos9 = spd9?.StockGramos ?? 0m;
                    // Restar reserva (StockMinimoMeLi en café = gramos directos)
                    var reservaGramos = (cafe.StockMinimoMeLi ?? 0);
                    var stockGramosDisp = Math.Max(0m, stockGramos9 - reservaGramos);
                    stockToPush = (int)Math.Floor(stockGramosDisp / gramosPorUnidad);
                }
                else
                {
                    var stockUnits9 = spd9?.StockUnidades ?? 0;
                    var reservaUnits = cafe.StockMinimoMeLi ?? 0;
                    stockToPush = Math.Max(0, stockUnits9 - reservaUnits);
                }
                payloadDict["available_quantity"] = stockToPush.Value;
            }
        }
        else if (item.ProductId.HasValue)
        {
            var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == item.ProductId.Value);
            if (product is null) throw new InvalidOperationException("El producto vinculado no existe.");

            if (pushPrice)
            {
                var rate = product.VatRate ?? 0m;
                priceWithVat = rate > 0m
                    ? Math.Round(product.RetailPrice * (1m + rate / 100m), 2, MidpointRounding.AwayFromZero)
                    : product.RetailPrice;
                payloadDict["price"] = priceWithVat;
            }
            if (pushStock)
            {
                // MeLi maneja enteros. Para productos kg-mode, esto necesitará lógica especial
                // (derivar paquetes desde el padre kg). Por ahora flooreamos a int.
                stockToPush = (int)Math.Floor(product.Stock);
                payloadDict["available_quantity"] = stockToPush.Value;
            }
        }
        else // ComboId.HasValue
        {
            var combo = await _db.Combos
                .Include(c => c.Items).ThenInclude(ci => ci.Product)
                .FirstOrDefaultAsync(c => c.Id == item.ComboId!.Value);
            if (combo is null) throw new InvalidOperationException("El combo vinculado no existe.");

            if (pushPrice)
            {
                // Calcular precio del combo segun el modo
                decimal comboPriceSinIva;
                if (combo.PriceMode == "manual" && combo.ManualPrice.HasValue)
                {
                    comboPriceSinIva = combo.ManualPrice.Value;
                }
                else if (combo.PriceMode == "percent" && combo.PercentAdjustment.HasValue)
                {
                    var sumItems = combo.Items.Sum(ci => (ci.Product?.RetailPrice ?? 0m) * ci.Quantity);
                    comboPriceSinIva = Math.Round(sumItems * (1 + combo.PercentAdjustment.Value / 100m), 2, MidpointRounding.AwayFromZero);
                }
                else // auto
                {
                    comboPriceSinIva = combo.Items.Sum(ci => (ci.Product?.RetailPrice ?? 0m) * ci.Quantity);
                }

                // Aplicar IVA: usar el VatRate del primer item con IVA seteado (o 21 default).
                var rate = combo.Items.Select(ci => ci.Product?.VatRate).FirstOrDefault(v => v.HasValue) ?? 21m;
                priceWithVat = rate > 0m
                    ? Math.Round(comboPriceSinIva * (1m + rate / 100m), 2, MidpointRounding.AwayFromZero)
                    : comboPriceSinIva;
                payloadDict["price"] = priceWithVat;
            }
            if (pushStock)
            {
                // Stock del combo = minimo de (stock_item / cantidad_necesaria) entre todos los items.
                if (combo.Items.Any())
                {
                    stockToPush = (int)combo.Items
                        .Select(ci => ci.Product is null ? 0m : (ci.Quantity > 0 ? Math.Floor(ci.Product.Stock / ci.Quantity) : 0m))
                        .Min();
                }
                else stockToPush = 0;
                payloadDict["available_quantity"] = stockToPush.Value;
            }
        }

        if (payloadDict.Count == 0)
            return new MeliPushResult(false, "No se eligio ningun campo para sincronizar.", null, null);

        var token = await _accountService.GetValidTokenAsync(item.MeliAccount);
        if (token is null)
            return new MeliPushResult(false, "No se pudo obtener un token valido para la cuenta de MeLi.", null, null);

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // ───────────── LIVE READ desde MeLi ─────────────
        // No confiamos en LogisticType cacheado (puede estar NULL o desactualizado).
        // Hacemos GET en MeLi para detectar:
        //   - logistic_type=fulfillment → MeLi NO permite editar stock por API
        //   - variations[] → si tiene, tenemos que pushear a cada variation
        var getResp = await http.GetAsync($"https://api.mercadolibre.com/items/{item.MeliItemId}");
        if (!getResp.IsSuccessStatusCode)
            return new MeliPushResult(false, $"No se pudo leer la publicacion en MeLi ({(int)getResp.StatusCode}).", null, null);
        var getJson = await getResp.Content.ReadAsStringAsync();
        using var liveDoc = JsonDocument.Parse(getJson);
        var liveRoot = liveDoc.RootElement;

        var liveIsFull = false;
        if (liveRoot.TryGetProperty("shipping", out var shipping)
            && shipping.ValueKind == JsonValueKind.Object
            && shipping.TryGetProperty("logistic_type", out var lt)
            && lt.ValueKind == JsonValueKind.String)
        {
            liveIsFull = string.Equals(lt.GetString(), "fulfillment", StringComparison.OrdinalIgnoreCase);
            // Cachear para futuras consultas
            item.LogisticType = lt.GetString();
        }

        var liveVariantIds = new List<long>();
        if (liveRoot.TryGetProperty("variations", out var liveVars) && liveVars.ValueKind == JsonValueKind.Array && liveVars.GetArrayLength() > 0)
        {
            foreach (var v in liveVars.EnumerateArray())
                liveVariantIds.Add(v.GetProperty("id").GetInt64());
        }

        // 2026-05-29: si es Full + pushStock, sacamos available_quantity del payload del PUT estándar
        // (MeLi lo rechaza). En su lugar, abajo hacemos un PUT separado al endpoint selling_address
        // para empujar "nuestra parte" del stock (lo que no es meli_facility).
        string? userProductIdForFull = null;
        bool pushFullSellingAddress = false;
        int qtyForSellingAddress = 0;
        if (liveIsFull && pushStock && stockToPush.HasValue)
        {
            payloadDict.Remove("available_quantity");
            if (liveRoot.TryGetProperty("user_product_id", out var upgProp) && upgProp.ValueKind == JsonValueKind.String)
            {
                userProductIdForFull = upgProp.GetString();
                if (!string.IsNullOrEmpty(userProductIdForFull))
                {
                    pushFullSellingAddress = true;
                    qtyForSellingAddress = Math.Max(0, stockToPush.Value);
                }
            }
        }

        // Si hay variations, reformatear el payload (price y stock van adentro de cada variation)
        if (liveVariantIds.Count > 0)
        {
            decimal? pPrice = payloadDict.TryGetValue("price", out var p) ? (decimal)p : null;
            int? pStock = payloadDict.TryGetValue("available_quantity", out var s) ? (int)s : null;
            var varList = liveVariantIds.Select(vId =>
            {
                var d = new Dictionary<string, object> { ["id"] = vId };
                if (pPrice.HasValue) d["price"] = pPrice.Value;
                if (pStock.HasValue) d["available_quantity"] = pStock.Value;
                return (object)d;
            }).ToList();
            payloadDict = new Dictionary<string, object> { ["variations"] = varList };
        }

        // Si NO hay nada en el payload estándar Y tampoco hay push Full → nada para hacer.
        if (payloadDict.Count == 0 && !pushFullSellingAddress)
            return new MeliPushResult(false, "Nada para pushear.", null, null);

        // PUT estándar al item (solo si hay algo que pushear ahí: precio o stock no-Full)
        if (payloadDict.Count > 0)
        {
            var payload = JsonSerializer.Serialize(payloadDict);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await http.PutAsync($"https://api.mercadolibre.com/items/{item.MeliItemId}", content);

            // Retry con token refrescado si da 401/403
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var newToken = await _accountService.GetValidTokenAsync(item.MeliAccount, forceRefresh: true);
                if (newToken is not null)
                {
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
                    content = new StringContent(payload, Encoding.UTF8, "application/json");
                    response = await http.PutAsync($"https://api.mercadolibre.com/items/{item.MeliItemId}", content);
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                return new MeliPushResult(false, $"Error de MeLi ({(int)response.StatusCode}): {FormatMeliError(body)}", null, null);
            }
        }

        // 2026-05-29: PUT al selling_address de la publicación Full (con stock 9 de Abril − reserva).
        // Esto NO toca el stock de Full (meli_facility) — solo nuestra parte propia.
        if (pushFullSellingAddress && !string.IsNullOrEmpty(userProductIdForFull))
        {
            var getStockUrl = $"https://api.mercadolibre.com/user-products/{userProductIdForFull}/stock";
            string? xVersion = null;
            using (var getRespFull = await http.GetAsync(getStockUrl))
            {
                if (!getRespFull.IsSuccessStatusCode)
                    return new MeliPushResult(false, $"FULL GET user-product stock {(int)getRespFull.StatusCode}", null, null);
                if (getRespFull.Headers.TryGetValues("x-version", out var xvVals))
                    xVersion = xvVals.FirstOrDefault();
            }
            if (string.IsNullOrEmpty(xVersion))
                return new MeliPushResult(false, "FULL sin x-version header en GET", null, null);

            var putUrl = $"https://api.mercadolibre.com/user-products/{userProductIdForFull}/stock/type/selling_address";
            var bodyJson = JsonSerializer.Serialize(new { quantity = qtyForSellingAddress });
            using var req = new HttpRequestMessage(HttpMethod.Put, putUrl)
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("x-version", xVersion);
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                return new MeliPushResult(false, $"FULL selling_address {(int)resp.StatusCode}: {FormatMeliError(err)}", null, null);
            }
        }

        // Actualizar la copia local
        if (pushPrice && priceWithVat.HasValue) item.Price = priceWithVat.Value;
        // 2026-05-29: para Full también guardamos la cantidad que pusheamos al selling_address.
        // El AvailableQuantity cacheado refleja lo que VOS tenés como parte propia en MeLi.
        if (pushStock && stockToPush.HasValue) item.AvailableQuantity = stockToPush.Value;
        item.UpdatedAt = DateTime.UtcNow;

        // ───────────── Activar sync automatica hacia adelante ─────────────
        // El boton "📦 Push Stock" desde /publicaciones espera dos cosas:
        //   1) Empujar el stock actual a MeLi ahora.
        //   2) Que de aca en adelante, futuros cambios de stock se sincronicen solos.
        //
        // El job de respaldo (MeliStockPushService.PushPendingAsync) selecciona
        // CafeProductos con StockChangedAt != null AND StockChangedAt > LastPushedToMeli.
        // Por eso:
        //   - Seteamos LastPushedToMeli = NOW() (acabamos de empujar OK).
        //   - Si StockChangedAt estaba en NULL (caso bug #119: 1081 productos importados
        //     de Contabilium nunca tuvieron movimiento), lo seteamos a NOW() para
        //     "activar" la columna. Ventas y ajustes futuros van a seguir actualizandola,
        //     y el bg job va a poder comparar (hoy con StockChangedAt = NULL nunca dispara).
        //   - Si StockChangedAt ya estaba seteado, no lo tocamos: respetamos cambios
        //     pendientes que pudieran haber entrado entre que leimos el cafe y guardamos.
        // Solo aplica al caso CafeProductoId — Products y Combos legacy no tienen estas columnas.
        // 2026-05-29: ahora también activa el sync para Full (sacamos el `!liveIsFull`).
        if (pushStock && stockToPush.HasValue && linkedCafe is not null)
        {
            var now = DateTime.UtcNow;
            linkedCafe.LastPushedToMeli = now;
            if (linkedCafe.StockChangedAt is null) linkedCafe.StockChangedAt = now;
        }

        await _db.SaveChangesAsync();

        await _auditLog.LogAsync("MeliItem", item.MeliItemId, "PUSH_FROM_LINKED",
            JsonSerializer.Serialize(new { productId = item.ProductId, comboId = item.ComboId, cafeProductoId = item.CafeProductoId, cafeFormato = item.CafeFormato, pushPrice, pushStock, priceWithVat, stock = stockToPush, liveIsFull, activatedSync = linkedCafe is not null && pushStock }));

        var msgExtra = liveIsFull && pushStock ? " (FULL: stock no actualizado, lo administra MeLi)" : "";
        return new MeliPushResult(true, $"Publicacion actualizada en MeLi.{msgExtra}", priceWithVat, (pushStock && !liveIsFull) ? stockToPush : null);
    }

    public record MeliPushResult(bool Success, string Message, decimal? PushedPrice, int? PushedStock);

    public async Task PropagateStockAsync(int productId, int newStock)
    {
        var items = await _db.MeliItems
            .Include(i => i.MeliAccount)
            .Where(i => i.ProductId == productId && i.Status == "active")
            .ToListAsync();

        if (!items.Any()) return;

        foreach (var item in items)
        {
            if (item.MeliAccount is null) continue;
            try
            {
                var token = await _accountService.GetValidTokenAsync(item.MeliAccount);
                if (token is null) continue;

                var http = _httpFactory.CreateClient();
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var payload = JsonSerializer.Serialize(new { available_quantity = newStock });
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var response = await http.PutAsync($"https://api.mercadolibre.com/items/{item.MeliItemId}", content);

                // Retry con token refrescado si da 401/403
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    var newToken = await _accountService.GetValidTokenAsync(item.MeliAccount, forceRefresh: true);
                    if (newToken is not null)
                    {
                        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
                        content = new StringContent(payload, Encoding.UTF8, "application/json");
                        response = await http.PutAsync($"https://api.mercadolibre.com/items/{item.MeliItemId}", content);
                    }
                }

                if (response.IsSuccessStatusCode)
                {
                    var oldStock = item.AvailableQuantity;
                    item.AvailableQuantity = newStock;
                    item.UpdatedAt = DateTime.UtcNow;
                    item.LastUpdated = DateTime.UtcNow;

                    await _auditLog.LogAsync("MeliItem", item.MeliItemId, "STOCK_SYNC",
                        JsonSerializer.Serialize(new { old = oldStock, @new = newStock, source = "product_update" }));
                }
            }
            catch { /* Si falla una publicacion, continuar con las demas */ }
        }

        await _db.SaveChangesAsync();
    }

        public async Task<BulkPublishResponse> BulkPublishAsync(BulkPublishRequest request)
    {
        var response = new BulkPublishResponse { Total = request.ProductIds.Count };

        // Validar cuenta
        var account = await _db.MeliAccounts.FindAsync(request.MeliAccountId);
        if (account is null)
        {
            response.Failed = response.Total;
            response.Results = request.ProductIds.Select(id => new BulkPublishItemResult
            { ProductId = id, Error = "Cuenta no encontrada" }).ToList();
            return response;
        }

        // Cargar productos
        var products = await _db.Products
            .Where(p => request.ProductIds.Contains(p.Id))
            .ToListAsync();

        // Cargar items existentes vinculados a estos productos (en cualquier cuenta)
        var existingItems = await _db.MeliItems
            .Where(i => i.ProductId != null && request.ProductIds.Contains(i.ProductId.Value))
            .ToListAsync();

        // Items que ya estan publicados en la cuenta destino (para skip)
        var alreadyOnTargetAccount = existingItems
            .Where(i => i.MeliAccountId == request.MeliAccountId)
            .Select(i => i.ProductId!.Value)
            .ToHashSet();

        // Items en OTRAS cuentas (para copiar datos)
        var itemsOnOtherAccounts = existingItems
            .Where(i => i.MeliAccountId != request.MeliAccountId)
            .GroupBy(i => i.ProductId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var productId in request.ProductIds)
        {
            var product = products.FirstOrDefault(p => p.Id == productId);
            if (product is null)
            {
                response.Results.Add(new BulkPublishItemResult
                { ProductId = productId, Error = "Producto no encontrado" });
                response.Failed++;
                continue;
            }

            // Skip si ya esta publicado en la cuenta destino
            if (alreadyOnTargetAccount.Contains(productId))
            {
                response.Results.Add(new BulkPublishItemResult
                { ProductId = productId, ProductTitle = product.Title, Skipped = true, SkipReason = "Ya publicado en esta cuenta" });
                response.Skipped++;
                continue;
            }

            // Calcular precio segun modo
            decimal price;
            if (request.PriceMode == "pvp")
                price = product.RetailPrice > 0 ? product.RetailPrice : product.CostPrice;
            else
                price = Math.Round(product.CostPrice * (1 + request.MarkupPercent / 100), 2);
            if (price <= 0) price = 1;

            // Construir request de publicacion
            var publishRequest = new PublishItemRequest
            {
                ProductId = productId,
                MeliAccountId = request.MeliAccountId,
                Title = product.Title,
                Description = product.Description,
                Price = price,
                AvailableQuantity = product.Stock > 0 ? (int)Math.Floor(product.Stock) : 1,
                Condition = request.Condition,
                ListingTypeId = request.ListingTypeId,
                FreeShipping = request.FreeShipping,
                PictureUrls = new List<string>()
            };

            // Si tiene publicacion en otra cuenta, copiar datos completos de MeLi
            if (itemsOnOtherAccounts.TryGetValue(productId, out var existingItem))
            {
                if (!string.IsNullOrEmpty(existingItem.CategoryId))
                    publishRequest.CategoryId = existingItem.CategoryId;

                // Obtener datos completos de la publicacion existente via API de MeLi
                try
                {
                    var sourceAccount = await _db.MeliAccounts.FindAsync(existingItem.MeliAccountId);
                    var sourceToken = sourceAccount != null ? await _accountService.GetValidTokenAsync(sourceAccount) : null;
                    if (sourceToken != null)
                    {
                        var srcHttp = _httpFactory.CreateClient();
                        srcHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sourceToken);

                        // Obtener item completo (atributos + fotos)
                        var itemResp = await srcHttp.GetAsync($"https://api.mercadolibre.com/items/{existingItem.MeliItemId}");
                        if (itemResp.IsSuccessStatusCode)
                        {
                            var itemJson = await itemResp.Content.ReadAsStringAsync();
                            using var itemDoc = JsonDocument.Parse(itemJson);
                            var root = itemDoc.RootElement;

                            // Copiar family_name si existe
                            if (root.TryGetProperty("family_name", out var fn) && fn.ValueKind != JsonValueKind.Null)
                            {
                                var familyName = fn.GetString();
                                if (!string.IsNullOrEmpty(familyName))
                                    publishRequest.FamilyName = familyName;
                            }

                            // Copiar atributos (solo los que tienen valor)
                            if (root.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var attr in attrs.EnumerateArray())
                                {
                                    var attrId = attr.TryGetProperty("id", out var aid) ? aid.GetString() : null;
                                    if (string.IsNullOrEmpty(attrId)) continue;
                                    // Saltar atributos que MeLi maneja automaticamente
                                    var autoAttrs = new[] { "ITEM_CONDITION", "SELLER_SKU", "GTIN" };
                                    if (autoAttrs.Contains(attrId)) continue;

                                    var valueId = attr.TryGetProperty("value_id", out var vid) && vid.ValueKind != JsonValueKind.Null ? vid.GetString() : null;
                                    var valueName = attr.TryGetProperty("value_name", out var vn) && vn.ValueKind != JsonValueKind.Null ? vn.GetString() : null;
                                    if ((valueId != null && valueId != "-1") || (valueName != null && valueName != "-1"))
                                    {
                                        if (valueId == "-1") valueId = null;
                                        publishRequest.Attributes.Add(new PublishAttributeDto { Id = attrId, ValueId = valueId, ValueName = valueName });
                                    }
                                }
                            }

                            // Copiar fotos de la publicacion existente
                            if (root.TryGetProperty("pictures", out var pics) && pics.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var pic in pics.EnumerateArray())
                                {
                                    var picUrl = pic.TryGetProperty("secure_url", out var su) ? su.GetString() : null;
                                    if (string.IsNullOrEmpty(picUrl) && pic.TryGetProperty("url", out var u))
                                        picUrl = u.GetString();
                                    if (!string.IsNullOrEmpty(picUrl))
                                        publishRequest.PictureUrls.Add(picUrl);
                                }
                            }
                        }

                        // Obtener descripcion de la publicacion existente
                        if (string.IsNullOrWhiteSpace(publishRequest.Description))
                        {
                            var descResp = await srcHttp.GetAsync($"https://api.mercadolibre.com/items/{existingItem.MeliItemId}/description");
                            if (descResp.IsSuccessStatusCode)
                            {
                                var descJson = await descResp.Content.ReadAsStringAsync();
                                using var descDoc = JsonDocument.Parse(descJson);
                                if (descDoc.RootElement.TryGetProperty("plain_text", out var pt) && pt.ValueKind != JsonValueKind.Null)
                                {
                                    var descText = pt.GetString();
                                    if (!string.IsNullOrWhiteSpace(descText))
                                        publishRequest.Description = descText;
                                }
                            }
                        }
                    }
                }
                catch { /* Si falla la consulta a MeLi, continuar con los datos que tenemos */ }
            }

            // Agregar fotos del producto local (si no se copiaron fotos de MeLi)
            if (!publishRequest.PictureUrls.Any())
            {
                if (!string.IsNullOrEmpty(product.Photo1)) publishRequest.PictureUrls.Add(product.Photo1);
                if (!string.IsNullOrEmpty(product.Photo2)) publishRequest.PictureUrls.Add(product.Photo2);
                if (!string.IsNullOrEmpty(product.Photo3)) publishRequest.PictureUrls.Add(product.Photo3);
            }

            // Si no tiene categoria (ni de publicacion existente), predecir
            if (string.IsNullOrEmpty(publishRequest.CategoryId))
            {
                try
                {
                    var predictions = await PredictCategoryAsync(product.Title, request.MeliAccountId);
                    if (predictions.Any())
                        publishRequest.CategoryId = predictions.First().CategoryId;
                }
                catch { }
            }

            if (string.IsNullOrEmpty(publishRequest.CategoryId))
            {
                response.Results.Add(new BulkPublishItemResult
                { ProductId = productId, ProductTitle = product.Title, Error = "No se pudo determinar la categoria" });
                response.Failed++;
                continue;
            }

            // Publicar
            var result = await PublishItemAsync(publishRequest);
            response.Results.Add(new BulkPublishItemResult
            {
                ProductId = productId,
                ProductTitle = product.Title,
                Success = result.Success,
                MeliItemId = result.MeliItemId,
                Permalink = result.Permalink,
                Error = result.Error
            });
            if (result.Success) response.Successful++;
            else response.Failed++;
        }

        return response;
    }


    /// <summary>
    /// Extrae el largo maximo del titulo del error de MeLi.
    /// Ej: "Category MLA123 does not support titles greater than 60 characters long"
    /// </summary>
    private bool TryExtractMaxTitleLength(string errorBody, out int maxLength)
    {
        maxLength = 0;
        if (string.IsNullOrEmpty(errorBody)) return false;
        var lower = errorBody.ToLower();
        if (!lower.Contains("title") || !lower.Contains("character")) return false;
        // Buscar patron "greater than N characters" o "than N characters"
        var match = System.Text.RegularExpressions.Regex.Match(errorBody, @"(?:greater\s+than|exceeds?|max(?:imum)?(?:\s+of)?)\s+(\d+)\s+character", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var len))
        {
            maxLength = len;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Detecta si el error de MeLi es por atributos obligatorios faltantes
    /// </summary>
    private bool IsAttributeRequiredError(string errorBody)
    {
        if (string.IsNullOrEmpty(errorBody)) return false;
        var lower = errorBody.ToLower();
        return (lower.Contains("attribute") || lower.Contains("atributo") || lower.Contains("item.attributes")) &&
               (lower.Contains("required") || lower.Contains("missing") ||
                lower.Contains("obligator") || lower.Contains("requerid") || lower.Contains("must have"));
    }

    /// <summary>
    /// Detecta si el error es por valor de atributo no resuelto.
    /// Ej: "Value name of attribute BRAND was not provided and couldn't be resolved"
    /// </summary>
    private bool IsAttributeValueNotResolvedError(string errorBody)
    {
        if (string.IsNullOrEmpty(errorBody)) return false;
        var lower = errorBody.ToLower();
        return lower.Contains("couldn't be resolved") || lower.Contains("could not be resolved") ||
               lower.Contains("was not provided") || lower.Contains("invalid value") ||
               (lower.Contains("attribute") && lower.Contains("not found"));
    }

    /// <summary>
    /// Consulta a OpenAI para sugerir atributos y los devuelve como PublishAttributeDto.
    /// Valida que los value_id sugeridos existan en los valores predefinidos de MeLi.
    /// </summary>
    private async Task<List<PublishAttributeDto>> SuggestAttributesWithAiAsync(PublishItemRequest request, HttpClient http)
    {
        // Obtener atributos de la categoria
        var categoryAttrs = await GetCategoryAttributesAsync(request.CategoryId);
        if (!categoryAttrs.Any()) return new List<PublishAttributeDto>();

        // Obtener nombre de la categoria
        var categoryName = request.CategoryId;
        try
        {
            var catResp = await http.GetAsync($"https://api.mercadolibre.com/categories/{request.CategoryId}");
            if (catResp.IsSuccessStatusCode)
            {
                var catJson = await catResp.Content.ReadAsStringAsync();
                using var catDoc = JsonDocument.Parse(catJson);
                if (catDoc.RootElement.TryGetProperty("name", out var cn))
                    categoryName = cn.GetString() ?? request.CategoryId;
            }
        }
        catch { }

        // Obtener datos del producto
        var product = await _db.Products.FindAsync(request.ProductId);

        // Pedir sugerencias a la IA
        var suggestRequest = new SuggestAttributesRequest(
            Title: request.Title,
            Description: request.Description,
            Brand: product?.Brand,
            Model: product?.Model,
            CategoryId: request.CategoryId,
            CategoryName: categoryName,
            Attributes: categoryAttrs
        );

        var suggestions = await _aiService.SuggestAttributesAsync(suggestRequest);

        // Validar sugerencias contra valores predefinidos de MeLi
        var attrLookup = categoryAttrs.ToDictionary(a => a.Id, a => a);
        var result = new List<PublishAttributeDto>();
        foreach (var s in suggestions)
        {
            if (string.IsNullOrEmpty(s.ValueId) && string.IsNullOrEmpty(s.ValueName))
                continue;

            if (attrLookup.TryGetValue(s.AttributeId, out var catAttr) && catAttr.Values.Any())
            {
                // El atributo tiene valores predefinidos: validar que el value_id exista
                var matchById = catAttr.Values.FirstOrDefault(v => v.Id == s.ValueId);
                if (matchById != null)
                {
                    // value_id valido
                    result.Add(new PublishAttributeDto { Id = s.AttributeId, ValueId = matchById.Id, ValueName = matchById.Name });
                }
                else
                {
                    // value_id no valido: buscar por nombre mas similar
                    var matchByName = catAttr.Values.FirstOrDefault(v =>
                        string.Equals(v.Name, s.ValueName, StringComparison.OrdinalIgnoreCase));
                    if (matchByName != null)
                    {
                        result.Add(new PublishAttributeDto { Id = s.AttributeId, ValueId = matchByName.Id, ValueName = matchByName.Name });
                    }
                    else if (!string.IsNullOrEmpty(s.ValueName))
                    {
                        // No hay match exacto: enviar solo value_name sin value_id
                        result.Add(new PublishAttributeDto { Id = s.AttributeId, ValueId = null, ValueName = s.ValueName });
                    }
                }
            }
            else
            {
                // Atributo de texto libre: enviar solo value_name sin value_id
                result.Add(new PublishAttributeDto { Id = s.AttributeId, ValueId = null, ValueName = s.ValueName });
            }
        }
        return result;
    }

    /// <summary>
    /// Detecta si el error es por GTIN requerido.
    /// Ej: "The attributes [GTIN] are required for category MLA1234"
    /// </summary>
    private bool IsGtinRequiredError(string errorBody)
    {
        if (string.IsNullOrEmpty(errorBody)) return false;
        var lower = errorBody.ToLower();
        return lower.Contains("gtin") && (lower.Contains("required") || lower.Contains("requerid") || lower.Contains("obligator"));
    }

    /// <summary>
    /// Parsea el JSON de error de MeLi y extrae mensajes legibles.
    /// Separa errores de warnings, muestra solo el campo "message".
    /// </summary>
    private string FormatMeliError(string errorBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            var root = doc.RootElement;
            var errors = new List<string>();
            var warnings = new List<string>();

            // Mensaje principal
            if (root.TryGetProperty("message", out var mainMsg))
            {
                var msg = mainMsg.GetString();
                if (!string.IsNullOrEmpty(msg) && msg != "Validation error")
                    errors.Add(msg);
            }

            // Array "cause" con detalles
            if (root.TryGetProperty("cause", out var causes) && causes.ValueKind == JsonValueKind.Array)
            {
                foreach (var cause in causes.EnumerateArray())
                {
                    var message = cause.TryGetProperty("message", out var m) ? m.GetString() : null;
                    if (string.IsNullOrEmpty(message)) continue;
                    var type = cause.TryGetProperty("type", out var t) ? t.GetString() : "error";
                    if (type == "warning")
                        warnings.Add(message);
                    else
                        errors.Add(message);
                }
            }

            if (!errors.Any() && !warnings.Any())
                return errorBody.Length > 200 ? errorBody[..200] + "..." : errorBody;

            var parts = new List<string>();
            foreach (var e in errors)
                parts.Add("[ERROR] " + e);
            foreach (var w in warnings)
                parts.Add("[WARN] " + w);
            return string.Join("|||", parts);
        }
        catch
        {
            return errorBody.Length > 300 ? errorBody[..300] + "..." : errorBody;
        }
    }

    /// <summary>
    /// Extrae la corrida (run) numerica mas larga del string. Util para detectar el SKU
    /// del padre dentro de un SKU de variante (ej. "C818BL" -> "818", "8718X4" -> "8718").
    /// </summary>
    private static string ExtractLongestNumericRun(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        string longest = "";
        int i = 0;
        while (i < s.Length)
        {
            if (char.IsDigit(s[i]))
            {
                int start = i;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                var run = s.Substring(start, i - start);
                if (run.Length > longest.Length) longest = run;
            }
            else i++;
        }
        return longest;
    }

    // ════════════════════════════════════════════════════════════════════════
    // 2026-06-12: MAYORISTA Y LÍMITES
    //  - Precio por cantidad (PxQ): hasta 5 escalones, solo visibles para compradores B2B.
    //    API: GET /items/{id}/prices (los escalones tienen conditions.min_purchase_unit)
    //         POST /items/{id}/prices/standard/quantity (reemplaza la tabla completa)
    //  - Límite de unidades por compra: sale_terms PURCHASE_MIN_QUANTITY / PURCHASE_MAX_QUANTITY.
    //    Para no pisar garantía/facturación, se manda la lista COMPLETA de sale_terms.
    // ════════════════════════════════════════════════════════════════════════

    public record MayoristaTier(int MinQty, decimal Amount);
    public record MayoristaInfo(List<MayoristaTier> Tiers, int? MinPorCompra, int? MaxPorCompra,
        decimal PrecioStandard, string? StandardPriceId, bool EsAlimentos = false);

    // Cache por categoría: ¿la raíz es "Alimentos y Bebidas"? (MeLi administra el límite de compra ahí)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _catAlimentosCache = new();

    private async Task<(HttpClient http, MeliItem item)> GetAuthorizedClientForItemAsync(string meliItemId)
    {
        var mi = await _db.MeliItems.Include(i => i.MeliAccount)
            .FirstOrDefaultAsync(i => i.MeliItemId == meliItemId)
            ?? throw new Exception($"La publicación {meliItemId} no está en el sistema.");
        if (mi.MeliAccount is null) throw new Exception("La publicación no tiene cuenta asociada.");
        var token = await _accountService.GetValidTokenAsync(mi.MeliAccount)
            ?? throw new Exception($"Token expirado para {mi.MeliAccount.Nickname}. Reconectá la cuenta.");
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (http, mi);
    }

    public async Task<MayoristaInfo> GetMayoristaAsync(string meliItemId)
    {
        var (http, _) = await GetAuthorizedClientForItemAsync(meliItemId);

        // 1) Escalones PxQ + precio estándar desde /prices
        var tiers = new List<MayoristaTier>();
        decimal precioStd = 0;
        string? stdId = null;
        var prResp = await http.GetAsync($"https://api.mercadolibre.com/items/{meliItemId}/prices");
        if (prResp.IsSuccessStatusCode)
        {
            var doc = JsonDocument.Parse(await prResp.Content.ReadAsStringAsync()).RootElement;
            if (doc.TryGetProperty("prices", out var prices) && prices.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in prices.EnumerateArray())
                {
                    var amount = p.TryGetProperty("amount", out var am) && am.ValueKind == JsonValueKind.Number ? am.GetDecimal() : 0m;
                    int? minUnit = null;
                    if (p.TryGetProperty("conditions", out var cond) && cond.ValueKind == JsonValueKind.Object
                        && cond.TryGetProperty("min_purchase_unit", out var mpu) && mpu.ValueKind == JsonValueKind.Number)
                        minUnit = mpu.GetInt32();
                    var type = p.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                    if (minUnit.HasValue && minUnit.Value > 1)
                        tiers.Add(new MayoristaTier(minUnit.Value, amount));
                    else if (type == "standard")
                    {
                        precioStd = amount;
                        stdId = p.TryGetProperty("id", out var pid) ? pid.GetString() : null;
                    }
                }
            }
        }

        // 2) Límites por compra desde sale_terms del item (+ categoría para detectar Alimentos)
        int? minQty = null, maxQty = null;
        string? categoryId = null;
        var itResp = await http.GetAsync($"https://api.mercadolibre.com/items/{meliItemId}?attributes=sale_terms,category_id");
        if (itResp.IsSuccessStatusCode)
        {
            var doc = JsonDocument.Parse(await itResp.Content.ReadAsStringAsync()).RootElement;
            if (doc.TryGetProperty("category_id", out var cid) && cid.ValueKind == JsonValueKind.String)
                categoryId = cid.GetString();
            if (doc.TryGetProperty("sale_terms", out var sts) && sts.ValueKind == JsonValueKind.Array)
            {
                foreach (var st in sts.EnumerateArray())
                {
                    var id = st.TryGetProperty("id", out var sid) ? sid.GetString() : null;
                    var valName = st.TryGetProperty("value_name", out var vn) && vn.ValueKind == JsonValueKind.String ? vn.GetString() : null;
                    if (id == "PURCHASE_MIN_QUANTITY" && int.TryParse((valName ?? "").Split(' ')[0], out var mn)) minQty = mn;
                    if (id == "PURCHASE_MAX_QUANTITY" && int.TryParse((valName ?? "").Split(' ')[0], out var mx)) maxQty = mx;
                }
            }
        }

        // 3) ¿Categoría de Alimentos? En esas, MeLi administra el máximo por compra y pisa el valor del vendedor.
        var esAlimentos = false;
        if (!string.IsNullOrEmpty(categoryId))
        {
            if (!_catAlimentosCache.TryGetValue(categoryId, out esAlimentos))
            {
                try
                {
                    var catResp = await http.GetAsync($"https://api.mercadolibre.com/categories/{categoryId}");
                    if (catResp.IsSuccessStatusCode)
                    {
                        var catDoc = JsonDocument.Parse(await catResp.Content.ReadAsStringAsync()).RootElement;
                        if (catDoc.TryGetProperty("path_from_root", out var path) && path.ValueKind == JsonValueKind.Array && path.GetArrayLength() > 0)
                        {
                            var rootName = path[0].TryGetProperty("name", out var rn) ? rn.GetString() : null;
                            esAlimentos = rootName == "Alimentos y Bebidas";
                        }
                        _catAlimentosCache[categoryId] = esAlimentos;
                    }
                }
                catch { /* sin categoría no hay aviso, no es crítico */ }
            }
        }

        return new MayoristaInfo(tiers.OrderBy(t => t.MinQty).ToList(), minQty, maxQty, precioStd, stdId, esAlimentos);
    }

    public async Task<MayoristaInfo> SaveMayoristaAsync(string meliItemId, List<MayoristaTier> tiers, int? minQty, int? maxQty)
    {
        var (http, _) = await GetAuthorizedClientForItemAsync(meliItemId);

        // ── A) Guardar escalones PxQ ──
        // El POST reemplaza la tabla completa: hay que reenviar el precio estándar por ID
        // (solo el id lo conserva) + los escalones nuevos. Sin escalones = se borran todos.
        var actual = await GetMayoristaAsync(meliItemId);
        if (actual.StandardPriceId is not null)
        {
            var pricesPayload = new List<object> { new { id = actual.StandardPriceId } };
            foreach (var t in tiers.Where(t => t.MinQty > 1 && t.Amount > 0).OrderBy(t => t.MinQty).Take(5))
            {
                pricesPayload.Add(new
                {
                    amount = t.Amount,
                    currency_id = "ARS",
                    conditions = new
                    {
                        context_restrictions = new[] { "channel_marketplace", "user_type_business" },
                        min_purchase_unit = t.MinQty
                    }
                });
            }
            var pxqBody = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { prices = pricesPayload }),
                System.Text.Encoding.UTF8, "application/json");
            var pxqResp = await http.PostAsync($"https://api.mercadolibre.com/items/{meliItemId}/prices/standard/quantity", pxqBody);
            if (!pxqResp.IsSuccessStatusCode)
            {
                var err = await pxqResp.Content.ReadAsStringAsync();
                throw new Exception($"MeLi rechazó los precios por cantidad ({(int)pxqResp.StatusCode}): {err}");
            }
        }
        else if (tiers.Count > 0)
        {
            throw new Exception("No se pudo identificar el precio estándar de la publicación — no se pueden cargar escalones.");
        }

        // ── B) Guardar límites por compra (PURCHASE_MIN/MAX_QUANTITY) ──
        // Leemos los sale_terms actuales y reconstruimos la lista completa para no pisar
        // garantía / facturación / etc.
        var itResp = await http.GetAsync($"https://api.mercadolibre.com/items/{meliItemId}?attributes=sale_terms");
        itResp.EnsureSuccessStatusCode();
        var itDoc = JsonDocument.Parse(await itResp.Content.ReadAsStringAsync()).RootElement;
        var saleTerms = new List<object>();
        if (itDoc.TryGetProperty("sale_terms", out var sts) && sts.ValueKind == JsonValueKind.Array)
        {
            foreach (var st in sts.EnumerateArray())
            {
                var id = st.TryGetProperty("id", out var sid) ? sid.GetString() : null;
                if (id is null || id == "PURCHASE_MIN_QUANTITY" || id == "PURCHASE_MAX_QUANTITY") continue;
                var valName = st.TryGetProperty("value_name", out var vn) && vn.ValueKind == JsonValueKind.String ? vn.GetString() : null;
                if (valName is not null) saleTerms.Add(new { id, value_name = valName });
            }
        }
        if (minQty.HasValue && minQty.Value > 0) saleTerms.Add(new { id = "PURCHASE_MIN_QUANTITY", value_name = minQty.Value.ToString() });
        if (maxQty.HasValue && maxQty.Value > 0) saleTerms.Add(new { id = "PURCHASE_MAX_QUANTITY", value_name = maxQty.Value.ToString() });

        var stBody = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new { sale_terms = saleTerms }),
            System.Text.Encoding.UTF8, "application/json");
        var stResp = await http.PutAsync($"https://api.mercadolibre.com/items/{meliItemId}", stBody);
        if (!stResp.IsSuccessStatusCode)
        {
            var err = await stResp.Content.ReadAsStringAsync();
            throw new Exception($"MeLi rechazó los límites de compra ({(int)stResp.StatusCode}): {err}");
        }

        return await GetMayoristaAsync(meliItemId);
    }

    // 2026-07-02: tabla FIJA de financing_add_on_fee por modalidad de cuotas.
    // La API listing_prices no la distingue (devuelve siempre el default 12,3%).
    // Verificado con Integraly y con múltiples publis. Los % son constantes por modalidad,
    // no varían por categoría/precio/vendedor. Aplican tanto a Premium (gold_pro) como
    // a Clásica (gold_special) cuando usan tags específicos.
    private static decimal? GetFinancingRealPct(string? listingTypeId, string? installmentTag)
    {
        if (string.IsNullOrEmpty(installmentTag)) return null;
        return installmentTag switch
        {
            "3x_campaign"    => 8.4m,
            "6x_campaign"    => 12.3m,
            "9x_campaign"    => 15.7m,
            "12x_campaign"   => 19.2m,
            "pcj-co-funded"  => 5.0m,
            _                => null,
        };
    }
}
