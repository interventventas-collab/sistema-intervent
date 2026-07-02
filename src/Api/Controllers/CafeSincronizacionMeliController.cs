using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Api.Controllers;

/// <summary>
/// Control de publicaciones MeLi linkeadas a productos del sistema: precio sistema vs precio MeLi,
/// margen sin comision, comision MeLi, neto en el bolsillo, configuracion por publicacion.
/// Inspiracion: pantalla de "Sincronizacion con MercadoLibre" de Contabilium.
/// </summary>
[ApiController]
[Route("api/cafe/sincronizacion-meli")]
[Authorize]
public class CafeSincronizacionMeliController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<CafeSincronizacionMeliController> _logger;

    public CafeSincronizacionMeliController(AppDbContext db, IHttpClientFactory httpFactory,
        ILogger<CafeSincronizacionMeliController> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public record SyncConfigDto(bool SyncStock, bool SyncPrecio, decimal AjustePct, decimal AjusteFijo, string? AjusteRedondeo, DateTime? LastSyncAt,
        // 2026-06-11: precio independiente
        bool PrecioIndependiente = false, decimal? PrecioFactor = null, decimal? PrecioBaseRef = null,
        string? ListingType = null, string? InstallmentConfig = null, bool? FreeShipping = null);

    public record PublicacionExtendidaDto(
        string MeliItemId, string Title, string Sku, string? Thumbnail, string Status, string? LogisticType,
        string CategoryId, string ListingTypeId,
        // Sistema
        int CafeProductoId, string CafeProductoNombre, string? CafeProductoMarca,
        decimal Costo, decimal IvaPct,
        decimal? PrecioBar, decimal? PrecioOtro,
        decimal? PrecioBarConIva, decimal? PrecioOtroConIva,
        decimal MargenSinComisDollar, decimal MargenSinComisPct,
        // Stock
        int StockSistema, int StockMeli,
        // Precios MeLi
        decimal PrecioMeli, decimal? PrecioMeliCalculado,
        // Comision
        decimal ComisionPct, decimal ComisionFija, decimal ComisionTotal,
        decimal NetoDeMeliConIva, decimal NetoDeMeliSinIva,
        decimal MargenRealConMeli, decimal MargenRealConMeliPct,
        // Diferencia precio sistema vs MeLi
        decimal DiferenciaPrecio, decimal DiferenciaPrecioPct,
        // Config
        SyncConfigDto Config,
        // Variaciones: si la publicacion tiene varias variantes, esta fila representa UNA de ellas
        string? VariationId = null,
        string? VariationAttributes = null);

    /// <summary>
    /// Lista publicaciones linkeadas + todos los calculos para que la UI las muestre con margenes.
    /// </summary>
    [HttpGet("publicaciones")]
    public async Task<IActionResult> ListPublicaciones([FromQuery] string? q = null,
        [FromQuery] string? categoria = null, [FromQuery] string? sortBy = "diferencia")
    {
        // 2026-05-29: ahora también incluye publicaciones linkeadas via MeliItemComponentes
        // (combos del sistema nuevo). Para esos casos, costo/precio = sumatoria de componentes × cantidad.

        // 1. MeLi items activos. Filtramos linkeo después (legacy o componentes).
        var qItems = _db.MeliItems
            .Where(mi => mi.Status == "active")
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            qItems = qItems.Where(mi => mi.Title.Contains(t) || (mi.Sku ?? "").Contains(t) || mi.MeliItemId.Contains(t));
        }
        var allItems = await qItems.ToListAsync();
        if (allItems.Count == 0) return Ok(new List<PublicacionExtendidaDto>());

        // 1b. Cargar componentes para identificar combos
        var allMeliIds = allItems.Select(mi => mi.MeliItemId).Distinct().ToList();
        var componentesRaw = await _db.MeliItemComponentes
            .Where(c => allMeliIds.Contains(c.MeliItemId))
            .ToListAsync();
        var componentesByMeliId = componentesRaw
            .GroupBy(c => c.MeliItemId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 2. Filtrar: aceptar items con CafeProductoId directo O con al menos un componente.
        var items = allItems
            .Where(mi => mi.CafeProductoId != null || componentesByMeliId.ContainsKey(mi.MeliItemId))
            .ToList();
        if (items.Count == 0) return Ok(new List<PublicacionExtendidaDto>());

        // 3. Cargar TODOS los CafeProductos referenciados (legacy + componentes).
        var prodIds = items.Where(mi => mi.CafeProductoId != null).Select(mi => mi.CafeProductoId!.Value)
            .Concat(componentesRaw.Select(c => c.CafeProductoId))
            .Distinct().ToList();
        var productos = await _db.CafeProductos
            .Include(p => p.MarcaNav)
            .Where(p => prodIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        // Filtro categoria: aplica si TODOS los productos del item son de esa categoria.
        if (!string.IsNullOrWhiteSpace(categoria))
        {
            var c = categoria.Trim().ToUpperInvariant();
            items = items.Where(mi =>
            {
                if (mi.CafeProductoId.HasValue && productos.TryGetValue(mi.CafeProductoId.Value, out var pLeg))
                    return pLeg.Categoria == c;
                // Combos: aceptar si al menos uno de los componentes coincide
                if (componentesByMeliId.TryGetValue(mi.MeliItemId, out var comps))
                    return comps.Any(co => productos.TryGetValue(co.CafeProductoId, out var pc) && pc.Categoria == c);
                return false;
            }).ToList();
        }

        // 4. Configs
        var meliIds = items.Select(mi => mi.MeliItemId).ToList();
        var configs = await _db.MeliItemSyncConfigs
            .Where(c => meliIds.Contains(c.MeliItemId))
            .ToDictionaryAsync(c => c.MeliItemId);

        // 5. Comisiones cacheadas
        var ratesAll = await _db.MeliCommissionRates.ToListAsync();
        var ratesByKey = ratesAll.ToDictionary(r => $"{r.CategoryId}|{r.ListingTypeId}");
        var defaultPct = 16.00m;
        var defaultFijo = 1250.00m;

        var result = new List<PublicacionExtendidaDto>();
        foreach (var mi in items)
        {
            // Calcular costo, precio, stock — distinto si es legacy 1:1 o combo de componentes.
            decimal costoBase = 0m, precioBarBase = 0m, precioOtroBase = 0m;
            decimal ivaPct = 21m;
            int stockSistema = 0;
            int productoIdRef = 0;
            string productoNombre = mi.Title ?? "";
            string? marcaNombre = null;
            bool tieneDatos = false;

            if (mi.CafeProductoId.HasValue && productos.TryGetValue(mi.CafeProductoId.Value, out var pLegacy))
            {
                // CASO LEGACY: 1 producto del sistema linkeado directo a la publicación.
                ivaPct = pLegacy.IvaPct;
                costoBase = pLegacy.Costo;
                precioBarBase = pLegacy.PrecioBar ?? 0;
                precioOtroBase = pLegacy.PrecioOtro ?? 0;
                stockSistema = pLegacy.StockUnidades;
                productoIdRef = pLegacy.Id;
                productoNombre = pLegacy.Nombre;
                marcaNombre = pLegacy.MarcaNav?.Nombre;
                tieneDatos = true;
            }
            else if (componentesByMeliId.TryGetValue(mi.MeliItemId, out var compsAll))
            {
                // CASO COMBO: sumar costos/precios de los componentes (variation_id si aplica).
                var compsForItem = compsAll.Where(c =>
                {
                    // Si la publicación tiene VariationId, aceptar componentes con ese VariationId o sin (defecto).
                    if (!string.IsNullOrEmpty(mi.VariationId))
                        return c.MeliVariationId == mi.VariationId || string.IsNullOrEmpty(c.MeliVariationId);
                    // Si no tiene VariationId, aceptar solo componentes sin VariationId.
                    return string.IsNullOrEmpty(c.MeliVariationId);
                }).ToList();
                if (compsForItem.Count == 0) compsForItem = compsAll;

                int? minStock = null;
                foreach (var co in compsForItem)
                {
                    if (!productos.TryGetValue(co.CafeProductoId, out var pc)) continue;
                    var cant = co.Cantidad <= 0 ? 1 : co.Cantidad;
                    costoBase += pc.Costo * cant;
                    precioBarBase += (pc.PrecioBar ?? 0) * cant;
                    precioOtroBase += (pc.PrecioOtro ?? 0) * cant;
                    // Tomar IVA del primer componente con IVA seteado
                    if (!tieneDatos && pc.IvaPct > 0) { ivaPct = pc.IvaPct; }
                    // Stock posible del combo = mínimo entre componentes / cantidad
                    var dispComp = cant > 0 ? (int)(pc.StockUnidades / cant) : 0;
                    if (minStock is null || dispComp < minStock) minStock = dispComp;
                    if (!tieneDatos) { marcaNombre = pc.MarcaNav?.Nombre; }
                    tieneDatos = true;
                }
                stockSistema = minStock ?? 0;
                productoNombre = $"🎁 Combo: {string.Join(", ", compsForItem.Take(3).Select(c => productos.TryGetValue(c.CafeProductoId, out var p) ? $"{p.Sku} ×{(int)c.Cantidad}" : "?"))}"
                    + (compsForItem.Count > 3 ? $" +{compsForItem.Count - 3} más" : "");
                productoIdRef = -1; // -1 = combo, no hay un único ProductoId
            }
            if (!tieneDatos) continue; // Skip items sin nada cargable

            costoBase = Math.Round(costoBase, 2);
            precioBarBase = Math.Round(precioBarBase, 2);
            precioOtroBase = Math.Round(precioOtroBase, 2);

            var precioBarConIva = precioBarBase > 0 ? Math.Round(precioBarBase * (1 + ivaPct/100m), 2) : (decimal?)null;
            var precioOtroConIva = precioOtroBase > 0 ? Math.Round(precioOtroBase * (1 + ivaPct/100m), 2) : (decimal?)null;
            var margenSinComisDollar = precioOtroBase - costoBase;
            var margenSinComisPct = costoBase > 0 ? Math.Round(margenSinComisDollar / costoBase * 100m, 0) : 0m;

            var key = $"{mi.CategoryId ?? "?"}|{mi.ListingTypeId ?? "?"}";
            var rate = ratesByKey.GetValueOrDefault(key);
            var comisPct = rate?.PercentageFee ?? defaultPct;
            var comisFija = rate?.FixedFee ?? defaultFijo;
            var precioMeli = mi.Price;
            var comisionTotal = Math.Round(precioMeli * comisPct / 100m + comisFija, 2);
            var netoConIva = Math.Round(precioMeli - comisionTotal, 2);
            var netoSinIva = ivaPct > 0 ? Math.Round(netoConIva / (1 + ivaPct/100m), 2) : netoConIva;
            var margenRealConMeli = Math.Round(netoSinIva - costoBase, 2);
            var margenRealConMeliPct = costoBase > 0 ? Math.Round(margenRealConMeli / costoBase * 100m, 0) : 0m;

            var diferenciaPrecio = precioMeli - (precioOtroConIva ?? 0);
            var diferenciaPrecioPct = (precioOtroConIva ?? 0) > 0 ? Math.Round(diferenciaPrecio / precioOtroConIva!.Value * 100m, 1) : 0m;

            var cfg = configs.GetValueOrDefault(mi.MeliItemId);
            var cfgDto = new SyncConfigDto(
                cfg?.SyncStock ?? true,
                cfg?.SyncPrecio ?? false,
                cfg?.AjustePct ?? 0m,
                cfg?.AjusteFijo ?? 0m,
                cfg?.AjusteRedondeo,
                cfg?.LastSyncAt,
                cfg?.PrecioIndependiente ?? false,
                cfg?.PrecioFactor,
                cfg?.PrecioBaseRef,
                cfg?.ListingType,
                cfg?.InstallmentConfig,
                cfg?.FreeShipping);

            decimal? precioMeliCalc = null;
            if (precioOtroConIva.HasValue)
            {
                var conAjuste = Math.Round(precioOtroConIva.Value * (1 + cfgDto.AjustePct/100m) + cfgDto.AjusteFijo, 2);
                precioMeliCalc = AplicarRedondeoHaciaArriba(conAjuste, cfgDto.AjusteRedondeo);
            }

            result.Add(new PublicacionExtendidaDto(
                mi.MeliItemId, mi.Title, mi.Sku ?? "", mi.Thumbnail, mi.Status, mi.LogisticType,
                mi.CategoryId ?? "?", mi.ListingTypeId ?? "?",
                productoIdRef, productoNombre, marcaNombre,
                costoBase, ivaPct,
                precioBarBase > 0 ? precioBarBase : (decimal?)null, precioOtroBase > 0 ? precioOtroBase : (decimal?)null,
                precioBarConIva, precioOtroConIva,
                margenSinComisDollar, margenSinComisPct,
                stockSistema, mi.AvailableQuantity,
                precioMeli, precioMeliCalc,
                comisPct, comisFija, comisionTotal,
                netoConIva, netoSinIva,
                margenRealConMeli, margenRealConMeliPct,
                diferenciaPrecio, diferenciaPrecioPct,
                cfgDto,
                mi.VariationId, mi.VariationAttributes));
        }

        // Ordenar
        result = sortBy switch
        {
            "margen" => result.OrderByDescending(x => x.MargenRealConMeliPct).ToList(),
            "diferencia" => result.OrderByDescending(x => Math.Abs(x.DiferenciaPrecio)).ToList(),
            "neto" => result.OrderByDescending(x => x.NetoDeMeliSinIva).ToList(),
            "precio" => result.OrderByDescending(x => x.PrecioMeli).ToList(),
            _ => result.OrderByDescending(x => Math.Abs(x.DiferenciaPrecio)).ToList()
        };

        return Ok(result);
    }

    public record UpdateSyncConfigRequest(bool SyncStock, bool SyncPrecio, decimal AjustePct, decimal AjusteFijo, string? AjusteRedondeo);

    [HttpPut("{meliItemId}/config")]
    public async Task<IActionResult> UpdateConfig(string meliItemId, [FromBody] UpdateSyncConfigRequest req)
    {
        var mi = await _db.MeliItems.FirstOrDefaultAsync(x => x.MeliItemId == meliItemId);
        if (mi is null) return NotFound(new { error = "Item MeLi no encontrado" });

        var cfg = await _db.MeliItemSyncConfigs.FindAsync(meliItemId);
        if (cfg is null)
        {
            cfg = new MeliItemSyncConfig { MeliItemId = meliItemId };
            _db.MeliItemSyncConfigs.Add(cfg);
        }
        cfg.SyncStock = req.SyncStock;
        cfg.SyncPrecio = req.SyncPrecio;
        cfg.AjustePct = req.AjustePct;
        cfg.AjusteFijo = req.AjusteFijo;
        cfg.AjusteRedondeo = string.IsNullOrEmpty(req.AjusteRedondeo) ? null : req.AjusteRedondeo;
        cfg.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new SyncConfigDto(cfg.SyncStock, cfg.SyncPrecio, cfg.AjustePct, cfg.AjusteFijo, cfg.AjusteRedondeo, cfg.LastSyncAt));
    }

    /// <summary>Redondea HACIA ARRIBA al siguiente número que cumpla la terminación.
    /// "" / null = sin redondeo. "99" = termina en 99 (ej: 24684 → 24699).
    /// "999" = termina en 999 (ej: 24684 → 24999). "000" = múltiplo de 1000 (ej: 24684 → 25000).</summary>
    private static decimal AplicarRedondeoHaciaArriba(decimal valor, string? modo)
    {
        if (string.IsNullOrEmpty(modo) || valor <= 0) return valor;
        int term = modo switch { "99" => 99, "999" => 999, "000" => 0, _ => -1 };
        int step = modo switch { "99" => 100, "999" => 1000, "000" => 1000, _ => 1 };
        if (step <= 1) return valor;
        int valorInt = (int)Math.Ceiling(valor);
        int siguiente;
        if (valorInt % step == term && valorInt >= valor)
            siguiente = valorInt;
        else
        {
            siguiente = ((valorInt - term + step - 1) / step) * step + term;
            if (siguiente < valor) siguiente += step;
        }
        return siguiente;
    }

    public record UpdatePrecioRequest(decimal Precio, decimal? GananciaObjetivoPct = null);
    public record UpdatePrecioResultDto(
        decimal NuevoPrecio, decimal ComisionTotal, decimal NetoConIva, decimal NetoSinIva,
        // 2026-07-02: verificacion del objetivo cumplido
        decimal? ShippingCost = null,
        decimal? Costo = null,
        decimal? GananciaReal = null,
        decimal? MargenRealPct = null,
        decimal? ObjetivoPct = null,
        bool? DentroDelUmbral = null,
        decimal? DesviacionPt = null);

    /// <summary>Push manual de precio a MeLi. Llama PUT /items/{id} con price.
    /// 2026-07-02: si viene GananciaObjetivoPct, se guarda el objetivo Y despues del push se refrescan
    /// comisiones+envio con el precio final para verificar si sigue dando esa ganancia (+-2 pt).</summary>
    [HttpPut("{meliItemId}/precio")]
    public async Task<IActionResult> UpdatePrecio(string meliItemId, [FromBody] UpdatePrecioRequest req,
        [FromServices] Api.Services.MeliItemService meliSvc)
    {
        if (req.Precio <= 0) return BadRequest(new { error = "Precio debe ser mayor a 0" });

        var mi = await _db.MeliItems.Include(m => m.MeliAccount).FirstOrDefaultAsync(x => x.MeliItemId == meliItemId);
        if (mi is null) return NotFound(new { error = "Item MeLi no encontrado" });
        if (mi.MeliAccount is null) return BadRequest(new { error = "Cuenta MeLi no disponible" });

        var token = mi.MeliAccount.AccessToken;
        if (string.IsNullOrWhiteSpace(token)) return BadRequest(new { error = "Token MeLi vacío" });

        using var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var body = new StringContent(JsonSerializer.Serialize(new { price = req.Precio }), System.Text.Encoding.UTF8, "application/json");
        var resp = await http.PutAsync($"https://api.mercadolibre.com/items/{meliItemId}", body);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("MeLi PUT price fallo para {item}: {code} {body}", meliItemId, (int)resp.StatusCode, text);
            return BadRequest(new { error = $"MeLi rechazo el cambio ({(int)resp.StatusCode}): {text}" });
        }

        // Actualizar snapshot local
        mi.Price = req.Precio;
        mi.UpdatedAt = DateTime.UtcNow;
        var cfg = await _db.MeliItemSyncConfigs.FindAsync(meliItemId);
        if (cfg is null)
        {
            cfg = new MeliItemSyncConfig { MeliItemId = meliItemId };
            _db.MeliItemSyncConfigs.Add(cfg);
        }
        cfg.LastSyncAt = DateTime.UtcNow;
        cfg.UpdatedAt = DateTime.UtcNow;

        // 2026-07-02: si vino objetivo, guardarlo
        if (req.GananciaObjetivoPct.HasValue && req.GananciaObjetivoPct.Value > 0)
        {
            cfg.GananciaObjetivoPct = req.GananciaObjetivoPct.Value;
            cfg.GananciaObjetivoAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        // 2026-07-02: refrescar costos REALES de MeLi con el precio nuevo para verificar objetivo.
        // Si algo falla, devolvemos igual el resultado basico sin la verificacion.
        decimal comisionTotal;
        decimal shippingCost = 0m;
        decimal netoConIva;
        try
        {
            var costs = await meliSvc.GetListingCostsAsync(meliItemId);
            comisionTotal = costs.SaleFeeAmount;
            shippingCost = costs.ShippingCost;
            netoConIva = Math.Round(req.Precio - comisionTotal - shippingCost, 2);
        }
        catch
        {
            var rate = await _db.MeliCommissionRates.FirstOrDefaultAsync(r => r.CategoryId == (mi.CategoryId ?? "?") && r.ListingTypeId == (mi.ListingTypeId ?? "?"));
            var comisPct = rate?.PercentageFee ?? 16.00m;
            var comisFija = rate?.FixedFee ?? 1250.00m;
            comisionTotal = Math.Round(req.Precio * comisPct / 100m + comisFija, 2);
            netoConIva = Math.Round(req.Precio - comisionTotal, 2);
        }

        var prod = await _db.CafeProductos.FindAsync(mi.CafeProductoId);
        var ivaPct = prod?.IvaPct ?? 21m;
        var netoSinIva = ivaPct > 0 ? Math.Round(netoConIva / (1 + ivaPct/100m), 2) : netoConIva;

        // 2026-07-02: calcular costo del producto (mismas reglas que GetProductCost) para poder verificar objetivo
        decimal? costoProducto = await CalcularCostoProductoAsync(mi);
        decimal? gananciaReal = null;
        decimal? margenRealPct = null;
        bool? dentroDelUmbral = null;
        decimal? desviacionPt = null;
        if (costoProducto.HasValue && costoProducto.Value > 0)
        {
            gananciaReal = Math.Round(netoSinIva - costoProducto.Value, 2);
            margenRealPct = Math.Round(gananciaReal.Value / costoProducto.Value * 100m, 2);
            if (cfg?.GananciaObjetivoPct.HasValue == true)
            {
                var obj = cfg.GananciaObjetivoPct.Value;
                desviacionPt = Math.Round(margenRealPct.Value - obj, 2);
                dentroDelUmbral = Math.Abs(desviacionPt.Value) <= 2m;
            }
        }

        return Ok(new UpdatePrecioResultDto(
            req.Precio, comisionTotal, netoConIva, netoSinIva,
            shippingCost, costoProducto, gananciaReal, margenRealPct,
            cfg?.GananciaObjetivoPct, dentroDelUmbral, desviacionPt));
    }

    /// <summary>2026-07-02: costo del producto/combo asociado a la MLA. Copia de GetProductCost
    /// para reutilizar en UpdatePrecio sin duplicar el endpoint.</summary>
    private async Task<decimal?> CalcularCostoProductoAsync(MeliItem mi)
    {
        var mecs = await (
            from c in _db.MeliItemComponentes
            join p in _db.CafeProductos on c.CafeProductoId equals p.Id
            where c.MeliItemId == mi.MeliItemId
            select new { p.Sku, p.Costo, c.Cantidad }
        ).ToListAsync();
        if (mecs.Count > 0)
        {
            // Dedup por SKU si son todas del mismo producto
            var deduped = mecs.GroupBy(x => x.Sku).Select(g => g.First()).ToList();
            return deduped.Sum(x => x.Costo * x.Cantidad);
        }
        if (mi.CafeComboId.HasValue)
        {
            var items = await (
                from ci in _db.CafeComboItems
                join p in _db.CafeProductos on ci.ProductoId equals p.Id
                where ci.ComboId == mi.CafeComboId.Value
                select p.Costo * ci.Cantidad
            ).ToListAsync();
            return items.Sum();
        }
        if (mi.CafeProductoId.HasValue)
        {
            var p = await _db.CafeProductos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mi.CafeProductoId.Value);
            if (p == null) return null;
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

    /// <summary>Refresca la cache de comisiones consultando /sites/MLA/listing_prices para una categoria+listing.
    /// Util cuando hay una categoria nueva o cuando MeLi actualiza sus tarifas.</summary>
    [HttpPost("comisiones/refresh")]
    public async Task<IActionResult> RefreshComisiones([FromQuery] string categoryId, [FromQuery] string listingTypeId, [FromQuery] decimal samplePrice = 5000)
    {
        var meliAcc = await _db.MeliAccounts.FirstOrDefaultAsync();
        if (meliAcc is null || string.IsNullOrWhiteSpace(meliAcc.AccessToken))
            return BadRequest(new { error = "Cuenta MeLi sin token" });

        using var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", meliAcc.AccessToken);
        var url = $"https://api.mercadolibre.com/sites/MLA/listing_prices?price={samplePrice}&listing_type_id={listingTypeId}&category_id={categoryId}";
        var resp = await http.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
            return BadRequest(new { error = $"MeLi no devolvio datos ({(int)resp.StatusCode})" });
        var text = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var pct = root.GetProperty("sale_fee_details").GetProperty("percentage_fee").GetDecimal();
        var fix = root.GetProperty("sale_fee_details").GetProperty("fixed_fee").GetDecimal();

        var existing = await _db.MeliCommissionRates
            .FirstOrDefaultAsync(r => r.CategoryId == categoryId && r.ListingTypeId == listingTypeId);
        if (existing is null)
        {
            _db.MeliCommissionRates.Add(new MeliCommissionRate
            {
                CategoryId = categoryId,
                ListingTypeId = listingTypeId,
                PercentageFee = pct,
                FixedFee = fix,
                CapturedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.PercentageFee = pct;
            existing.FixedFee = fix;
            existing.CapturedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new { categoryId, listingTypeId, percentageFee = pct, fixedFee = fix });
    }
}
