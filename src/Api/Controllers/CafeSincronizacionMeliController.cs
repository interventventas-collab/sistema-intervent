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

    public record SyncConfigDto(bool SyncStock, bool SyncPrecio, decimal AjustePct, decimal AjusteFijo, DateTime? LastSyncAt);

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
        SyncConfigDto Config);

    /// <summary>
    /// Lista publicaciones linkeadas + todos los calculos para que la UI las muestre con margenes.
    /// </summary>
    [HttpGet("publicaciones")]
    public async Task<IActionResult> ListPublicaciones([FromQuery] string? q = null,
        [FromQuery] string? categoria = null, [FromQuery] string? sortBy = "diferencia")
    {
        // 1. MeLi items linkeados a productos
        var qItems = _db.MeliItems
            .Where(mi => mi.Status == "active" && mi.CafeProductoId != null)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            qItems = qItems.Where(mi => mi.Title.Contains(t) || (mi.Sku ?? "").Contains(t) || mi.MeliItemId.Contains(t));
        }
        var items = await qItems.ToListAsync();
        if (items.Count == 0) return Ok(new List<PublicacionExtendidaDto>());

        // 2. Productos referenciados
        var prodIds = items.Select(mi => mi.CafeProductoId!.Value).Distinct().ToList();
        var productos = await _db.CafeProductos
            .Include(p => p.MarcaNav)
            .Where(p => prodIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);
        if (!string.IsNullOrWhiteSpace(categoria))
        {
            var c = categoria.Trim().ToUpperInvariant();
            items = items.Where(mi => productos.TryGetValue(mi.CafeProductoId!.Value, out var p) && p.Categoria == c).ToList();
        }

        // 3. Configs
        var meliIds = items.Select(mi => mi.MeliItemId).ToList();
        var configs = await _db.MeliItemSyncConfigs
            .Where(c => meliIds.Contains(c.MeliItemId))
            .ToDictionaryAsync(c => c.MeliItemId);

        // 4. Comisiones cacheadas
        var ratesAll = await _db.MeliCommissionRates.ToListAsync();
        var ratesByKey = ratesAll.ToDictionary(r => $"{r.CategoryId}|{r.ListingTypeId}");

        // Default fallback: 16% + $1.250 (gold_special MLA47752 — mate/cafe/te)
        var defaultPct = 16.00m;
        var defaultFijo = 1250.00m;

        var result = new List<PublicacionExtendidaDto>();
        foreach (var mi in items)
        {
            if (!productos.TryGetValue(mi.CafeProductoId!.Value, out var p)) continue;

            var ivaPct = p.IvaPct;
            var precioBarConIva = p.PrecioBar.HasValue ? Math.Round(p.PrecioBar.Value * (1 + ivaPct/100m), 2) : (decimal?)null;
            var precioOtroConIva = p.PrecioOtro.HasValue ? Math.Round(p.PrecioOtro.Value * (1 + ivaPct/100m), 2) : (decimal?)null;
            // Margen sin comisiones MeLi = PrecioOtro - Costo (en sin IVA)
            var margenSinComisDollar = (p.PrecioOtro ?? 0) - p.Costo;
            var margenSinComisPct = p.Costo > 0 ? Math.Round(margenSinComisDollar / p.Costo * 100m, 0) : 0m;

            // Comision MeLi efectiva
            var key = $"{mi.CategoryId ?? "?"}|{mi.ListingTypeId ?? "?"}";
            var rate = ratesByKey.GetValueOrDefault(key);
            var comisPct = rate?.PercentageFee ?? defaultPct;
            var comisFija = rate?.FixedFee ?? defaultFijo;
            var precioMeli = mi.Price;
            var comisionTotal = Math.Round(precioMeli * comisPct / 100m + comisFija, 2);
            var netoConIva = Math.Round(precioMeli - comisionTotal, 2);
            var netoSinIva = ivaPct > 0 ? Math.Round(netoConIva / (1 + ivaPct/100m), 2) : netoConIva;
            var margenRealConMeli = Math.Round(netoSinIva - p.Costo, 2);
            var margenRealConMeliPct = p.Costo > 0 ? Math.Round(margenRealConMeli / p.Costo * 100m, 0) : 0m;

            // Diferencia precio sistema (PrecioOtroConIva) vs MeLi
            var diferenciaPrecio = precioMeli - (precioOtroConIva ?? 0);
            var diferenciaPrecioPct = (precioOtroConIva ?? 0) > 0 ? Math.Round(diferenciaPrecio / precioOtroConIva!.Value * 100m, 1) : 0m;

            // Config
            var cfg = configs.GetValueOrDefault(mi.MeliItemId);
            var cfgDto = new SyncConfigDto(
                cfg?.SyncStock ?? true,
                cfg?.SyncPrecio ?? false,
                cfg?.AjustePct ?? 0m,
                cfg?.AjusteFijo ?? 0m,
                cfg?.LastSyncAt);

            // Precio MeLi calculado con la formula del ajuste (lo que el sistema pushearia hoy)
            decimal? precioMeliCalc = null;
            if (precioOtroConIva.HasValue)
                precioMeliCalc = Math.Round(precioOtroConIva.Value * (1 + cfgDto.AjustePct/100m) + cfgDto.AjusteFijo, 2);

            result.Add(new PublicacionExtendidaDto(
                mi.MeliItemId, mi.Title, mi.Sku ?? "", mi.Thumbnail, mi.Status, mi.LogisticType,
                mi.CategoryId ?? "?", mi.ListingTypeId ?? "?",
                p.Id, p.Nombre, p.MarcaNav?.Nombre,
                p.Costo, ivaPct,
                p.PrecioBar, p.PrecioOtro,
                precioBarConIva, precioOtroConIva,
                margenSinComisDollar, margenSinComisPct,
                p.StockUnidades, mi.AvailableQuantity,
                precioMeli, precioMeliCalc,
                comisPct, comisFija, comisionTotal,
                netoConIva, netoSinIva,
                margenRealConMeli, margenRealConMeliPct,
                diferenciaPrecio, diferenciaPrecioPct,
                cfgDto));
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

    public record UpdateSyncConfigRequest(bool SyncStock, bool SyncPrecio, decimal AjustePct, decimal AjusteFijo);

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
        cfg.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new SyncConfigDto(cfg.SyncStock, cfg.SyncPrecio, cfg.AjustePct, cfg.AjusteFijo, cfg.LastSyncAt));
    }

    public record UpdatePrecioRequest(decimal Precio);
    public record UpdatePrecioResultDto(decimal NuevoPrecio, decimal ComisionTotal, decimal NetoConIva, decimal NetoSinIva);

    /// <summary>Push manual de precio a MeLi. Llama PUT /items/{id} con price.</summary>
    [HttpPut("{meliItemId}/precio")]
    public async Task<IActionResult> UpdatePrecio(string meliItemId, [FromBody] UpdatePrecioRequest req)
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
        if (cfg is not null) { cfg.LastSyncAt = DateTime.UtcNow; cfg.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();

        // Calcular comision + neto para devolver al frontend
        var rate = await _db.MeliCommissionRates.FirstOrDefaultAsync(r => r.CategoryId == (mi.CategoryId ?? "?") && r.ListingTypeId == (mi.ListingTypeId ?? "?"));
        var comisPct = rate?.PercentageFee ?? 16.00m;
        var comisFija = rate?.FixedFee ?? 1250.00m;
        var comisionTotal = Math.Round(req.Precio * comisPct / 100m + comisFija, 2);
        var netoConIva = Math.Round(req.Precio - comisionTotal, 2);
        var prod = await _db.CafeProductos.FindAsync(mi.CafeProductoId);
        var ivaPct = prod?.IvaPct ?? 21m;
        var netoSinIva = ivaPct > 0 ? Math.Round(netoConIva / (1 + ivaPct/100m), 2) : netoConIva;

        return Ok(new UpdatePrecioResultDto(req.Precio, comisionTotal, netoConIva, netoSinIva));
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
