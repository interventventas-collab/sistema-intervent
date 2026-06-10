using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/productos")]
[Authorize]
public class CafeProductosController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private static readonly string[] CategoriasValidas = { "CAFE", "OTROS" };

    public CafeProductosController(AppDbContext db, IServiceScopeFactory scopeFactory) { _db = db; _scopeFactory = scopeFactory; }

    /// <summary>Dispara push de stock a MeLi en background (fire-and-forget) cuando el usuario
    /// edita stock manualmente desde la pantalla de productos. El push respeta los kill switches
    /// (si master_enabled = false, no hace nada).</summary>
    private void FireAndForgetPushMeli(int cafeProductoId)
    {
        var scopeFactory = _scopeFactory;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var pushSvc = scope.ServiceProvider.GetRequiredService<MeliStockPushService>();
                await pushSvc.PushStockForProductoAsync(cafeProductoId);
            }
            catch { /* errores capturados por el service, marca queda en StockChangedAt */ }
        });
    }

    /// <summary>2026-05-30: dispara push de PRECIO a MeLi en background (fire-and-forget)
    /// cuando se modifica PrecioOtro o IvaPct. Solo pushea publicaciones "claimed"
    /// (SyncPrecio=true en MeliItem_SyncConfig). Las no-claimed se respetan en silencio.</summary>
    private void FireAndForgetPushPrecioMeli(int cafeProductoId)
    {
        var scopeFactory = _scopeFactory;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var pushSvc = scope.ServiceProvider.GetRequiredService<MeliPricePushService>();
                await pushSvc.PushPrecioForProductoAsync(cafeProductoId);
            }
            catch { /* errores capturados por el service, marca queda en PriceChangedAt */ }
        });
    }

    private static CafeProductoDto Map(CafeProducto p) => new(
        p.Id, p.Sku, p.Barcode,
        p.Nombre, p.Categoria, p.Marca,
        p.MarcaId, p.MarcaNav?.Nombre,
        p.Costo, p.PrecioPorKg,
        p.Pvp1, p.Pvp2,
        p.BarPctSobreCosto, p.UxB,
        p.OemId, p.OemNav?.Codigo,
        p.StockGramos, p.StockUnidades,
        p.Notas, p.IsActive, p.IvaPct, p.CreatedAt, p.UpdatedAt,
        p.OemNav?.PvpConIva, p.OemNav?.IvaPct,
        p.PrecioOtro, p.PrecioBar,
        p.PrecioBulto, p.PrecioBultoOtro,
        p.FechaAplicaPreciosFuturos,
        p.PrecioPorKgFuturo, p.PrecioBarFuturo, p.PrecioOtroFuturo,
        p.PrecioBultoFuturo, p.PrecioBultoOtroFuturo,
        p.UsaPreciosFuturos,
        p.IsVisibleEnVentas,
        p.ImportSource,
        p.Packs?.Where(pk => pk.IsActive)
            .OrderBy(pk => pk.SortOrder).ThenBy(pk => pk.Cantidad)
            .Select(pk => new CafeProductoPackDto(pk.Id, pk.Cantidad, pk.Nombre, pk.PrecioOverride, pk.IsActive, pk.SortOrder))
            .ToList() ?? new List<CafeProductoPackDto>(),
        StockMinimoMeLi: p.StockMinimoMeLi,
        MultiplicadorOem: p.MultiplicadorOem,
        SinPrecioBar: p.SinPrecioBar);

    /// <summary>Búsqueda rápida (solo Id, Sku, Nombre, StockUnidades). Usado por la UI de
    /// edición de componentes MeLi en /cafe/skus-meli (selector de producto).</summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int limit = 15)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Ok(new List<object>());
        var qUp = q.Trim().ToUpperInvariant();
        var result = await _db.CafeProductos.AsNoTracking()
            .Where(p => p.IsActive && (p.Sku!.ToUpper().Contains(qUp) || p.Nombre.ToUpper().Contains(qUp)))
            .OrderBy(p => p.Sku)
            .Take(Math.Min(limit, 50))
            .Select(p => new { Id = p.Id, Sku = p.Sku ?? "", Nombre = p.Nombre, StockUnidades = p.StockUnidades })
            .ToListAsync();
        return Ok(result);
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? categoria = null)
    {
        var q = _db.CafeProductos.Include(p => p.OemNav).Include(p => p.MarcaNav).Include(p => p.Packs).AsQueryable();
        if (!string.IsNullOrWhiteSpace(categoria))
        {
            var c = NormCat(categoria);
            q = q.Where(p => p.Categoria == c);
        }
        var list = await q.ToListAsync();
        // Orden natural por SKU (F1, F2, F3, ..., F10, F11) y luego por nombre.
        // Productos sin SKU caen al final, ordenados por nombre.
        list = list
            .OrderBy(p => p.Categoria)
            .ThenBy(p => string.IsNullOrEmpty(p.Sku) ? 1 : 0)
            .ThenBy(p => SkuLetras(p.Sku))
            .ThenBy(p => SkuNumero(p.Sku))
            .ThenBy(p => p.Sku)
            .ThenBy(p => p.Nombre)
            .ToList();

        // 2026-06-01: calcular StockArmable para productos "shell" (con OemId + linkeados a MeliItems
        // via CafeProductoId). Stock armable = min(stock componente - reserva componente) / cantidad.
        // Para productos físicos normales (sin OemId o sin linkeo a MeLi) queda null y la UI muestra StockUnidades.
        var armableMap = await CalcularStockArmableBulkAsync(list.Select(p => p.Id).ToList());

        // 2026-06-02: desglose de stock por depósito para que la UI muestre '280 propio + 50 Full'
        // en lugar del total 330. Bulk query por todos los productos a la vez.
        var productoIds = list.Select(p => p.Id).ToList();
        var stockPorDep = await _db.CafeStockPorDeposito.AsNoTracking()
            .Where(s => productoIds.Contains(s.ProductoId))
            .Join(_db.CafeDepositos.AsNoTracking(), s => s.DepositoId, d => d.Id, (s, d) => new { s.ProductoId, DepNombre = d.Nombre, s.StockUnidades })
            .ToListAsync();
        var stockMap = stockPorDep.GroupBy(x => x.ProductoId)
            .ToDictionary(g => g.Key, g => new {
                Propio = g.Where(x => x.DepNombre == "9 de Abril").Sum(x => (int?)x.StockUnidades),
                Full = g.Where(x => x.DepNombre == "Full MeLi").Sum(x => (int?)x.StockUnidades)
            });

        return Ok(list.Select(p => {
            var dto = Map(p);
            if (armableMap.TryGetValue(p.Id, out var armable))
                dto = dto with { StockArmable = armable };
            if (stockMap.TryGetValue(p.Id, out var s))
                dto = dto with { StockPropio = s.Propio, StockFull = s.Full };
            return dto;
        }).ToList());
    }

    /// <summary>2026-06-01 — Calcula el stock armable (en lote) para una lista de productos.
    /// Solo aplica a los que están linkeados a MeliItems via CafeProductoId Y esas publicaciones
    /// tienen componentes (MeliItemComponentes). Para otros productos no devuelve nada.</summary>
    private async Task<Dictionary<int, int>> CalcularStockArmableBulkAsync(List<int> productoIds)
    {
        var result = new Dictionary<int, int>();
        if (productoIds.Count == 0) return result;

        // 1) MeliItems linkeados a alguno de estos productos (activos o paused).
        // 2026-06-01 (revertido): volvemos a filtrar por Status active/paused. La hipotesis
        // de "ignorar status" producia falsos positivos: productos individuales (C186BL/GR/ROJ)
        // que NO son combos tenian MeliItemComponentes fantasma de una MLA vieja closed
        // (mapping erroneo de Contabilium) y se mostraban como shell. La composicion mal
        // cargada en MeliItemComponentes hay que limpiarla por separado, no compensarla aca.
        var meliItems = await _db.MeliItems.AsNoTracking()
            .Where(mi => mi.CafeProductoId != null
                && productoIds.Contains(mi.CafeProductoId.Value)
                && (mi.Status == "active" || mi.Status == "paused"))
            .Select(mi => new { mi.Id, mi.MeliItemId, ProdId = mi.CafeProductoId!.Value })
            .ToListAsync();
        if (meliItems.Count == 0) return result;

        var meliIds = meliItems.Select(x => x.MeliItemId).Distinct().ToList();

        // 2) Componentes de esas publicaciones
        var comps = await _db.MeliItemComponentes.AsNoTracking()
            .Where(c => meliIds.Contains(c.MeliItemId))
            .Select(c => new { c.MeliItemId, c.CafeProductoId, c.Cantidad })
            .ToListAsync();
        if (comps.Count == 0) return result;

        // 3) Stock + reserva de los componentes
        var compProdIds = comps.Select(c => c.CafeProductoId).Distinct().ToList();
        var compStocks = await _db.CafeProductos.AsNoTracking()
            .Where(p => compProdIds.Contains(p.Id))
            .Select(p => new { p.Id, p.StockUnidades, p.StockMinimoMeLi })
            .ToDictionaryAsync(p => p.Id);

        // 4) Calcular armable por producto = max(armable por publicacion linkeada)
        // Agrupar comps por MeliItemId y, para cada producto, recorrer sus MeliItems linkeadas.
        var compsByMeliId = comps.GroupBy(c => c.MeliItemId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var prodGroup in meliItems.GroupBy(x => x.ProdId))
        {
            int armableMax = 0;
            bool tieneAlgunaCompReal = false;  // 2026-06-02: track si al menos una MLA tiene componentes cargados
            foreach (var mi in prodGroup)
            {
                if (!compsByMeliId.TryGetValue(mi.MeliItemId, out var compsList)) continue;
                // 2026-06-02 FIX BUG: ignorar componentes RECURSIVOS (el producto es componente de
                // si mismo). Eso es basura — la mayoria viene de Contabilium con mappings autoreferentes.
                // Caso real: C949BL (cesto) aparece como componente de su propia MLA, y eso le
                // marcaba "0 armable" tapando su stock real. 5394 entradas asi en la DB.
                var compsValidas = compsList.Where(c => c.CafeProductoId != mi.ProdId).ToList();
                if (compsValidas.Count == 0) continue;  // todas las componentes eran recursivas → no es shell
                tieneAlgunaCompReal = true;  // esta MLA SI tiene componentes reales — el producto es un shell real
                int armableThis = int.MaxValue;
                foreach (var c in compsValidas)
                {
                    if (!compStocks.TryGetValue(c.CafeProductoId, out var ps)) { armableThis = 0; break; }
                    int reservaComp = ps.StockMinimoMeLi ?? 0;
                    int disponible = Math.Max(0, ps.StockUnidades - reservaComp);
                    int armableCalc = c.Cantidad > 0 ? (int)(disponible / c.Cantidad) : 0;
                    if (armableCalc < armableThis) armableThis = armableCalc;
                }
                if (armableThis != int.MaxValue && armableThis > armableMax) armableMax = armableThis;
            }
            // 2026-06-02 BUG FIX: solo marcar como shell/armable si AL MENOS UNA MLA active/paused
            // tiene componentes cargados realmente. Si ninguna los tiene, NO guardamos nada — la UI
            // muestra el StockUnidades normal del producto en lugar de "0 (armable)".
            // Antes este metodo guardaba result[prodId] = 0 siempre, tapando el stock real del
            // producto fisico (caso D400/D401: tenian 730/543 unidades pero la UI decia "0 armable").
            if (tieneAlgunaCompReal)
            {
                result[prodGroup.Key] = armableMax;
            }
        }
        return result;
    }

    // "F1" → "F", "C8733NEG" → "C", "PAL50COLOR" → "PAL"
    private static string SkuLetras(string? sku)
    {
        if (string.IsNullOrEmpty(sku)) return "";
        var i = 0;
        while (i < sku.Length && !char.IsDigit(sku[i])) i++;
        return sku.Substring(0, i).ToUpperInvariant();
    }

    // "F1" → 1, "F11" → 11, "C8733NEG" → 8733, "ABC" → 0
    private static int SkuNumero(string? sku)
    {
        if (string.IsNullOrEmpty(sku)) return 0;
        var i = 0;
        while (i < sku.Length && !char.IsDigit(sku[i])) i++;
        var start = i;
        while (i < sku.Length && char.IsDigit(sku[i])) i++;
        if (i == start) return 0;
        return int.TryParse(sku.AsSpan(start, i - start), out var n) ? n : 0;
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var p = await _db.CafeProductos.Include(x => x.OemNav).Include(x => x.MarcaNav).Include(x => x.Packs).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound(new { error = "Producto no encontrado" });
        return Ok(Map(p));
    }

    /// <summary>
    /// Preview de sincronizacion a MeLi: lista todas las publicaciones vinculadas a este cafe,
    /// con el stock+precio actual en MeLi vs el que se va a pushear.
    /// </summary>
    [HttpGet("{id:int}/meli-preview")]
    public async Task<IActionResult> MeliPreview(int id)
    {
        var cafe = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == id);
        if (cafe is null) return NotFound(new { error = "Cafe no encontrado" });
        var settings = await _db.CafeSettings.FindAsync(1) ?? new Models.CafeSetting { Id = 1 };

        var items = await _db.MeliItems
            .Include(i => i.MeliAccount)
            .Where(i => i.CafeProductoId == id && i.Status == "active")
            .OrderBy(i => i.CafeFormato).ThenBy(i => i.MeliItemId)
            .ToListAsync();

        var listaKg = cafe.Pvp1 ?? cafe.Pvp2 ?? cafe.PrecioPorKg ?? 0m;
        var rows = items.Select(it =>
        {
            var formato = string.IsNullOrEmpty(it.CafeFormato) ? "1KG" : it.CafeFormato;
            decimal precioSinIva = formato switch
            {
                "1KG" => listaKg,
                "MEDIO" => Math.Round(listaKg / 2m + settings.CostoFraccionamiento, 2, MidpointRounding.AwayFromZero),
                "CUARTO" => Math.Round(listaKg / 4m + settings.CostoFraccionamiento, 2, MidpointRounding.AwayFromZero),
                _ => listaKg
            };
            decimal precioConIva = cafe.IvaPct > 0
                ? Math.Round(precioSinIva * (1m + cafe.IvaPct / 100m), 2, MidpointRounding.AwayFromZero)
                : precioSinIva;
            int gramosPorUnidad = formato switch { "MEDIO" => 500, "CUARTO" => 250, _ => 1000 };
            int stockNuevo = (int)Math.Floor(cafe.StockGramos / gramosPorUnidad);

            bool esFull = string.Equals(it.LogisticType, "fulfillment", StringComparison.OrdinalIgnoreCase);
            return new
            {
                meliItemId = it.MeliItemId,
                title = it.Title,
                cuenta = it.MeliAccount != null ? it.MeliAccount.Nickname : "—",
                formato,
                logisticType = it.LogisticType,
                esFull,
                skuMeli = it.Sku,
                stockMeli = it.AvailableQuantity,
                stockNuevo,
                stockDelta = stockNuevo - it.AvailableQuantity,
                precioMeli = it.Price,
                precioNuevo = precioConIva,
                precioDelta = precioConIva - it.Price,
                cambia = stockNuevo != it.AvailableQuantity || precioConIva != it.Price
            };
        }).ToList();

        return Ok(new
        {
            cafe = new { id = cafe.Id, sku = cafe.Sku, nombre = cafe.Nombre, stockGramos = cafe.StockGramos, pvp1 = cafe.Pvp1, ivaPct = cafe.IvaPct },
            publicaciones = rows
        });
    }

    /// <summary>
    /// Consulta a MeLi el tipo de logistica (Full, drop_off, etc.) de cada publicacion vinculada al cafe
    /// y actualiza la columna LogisticType. Necesario para no pushear stock a publicaciones Full.
    /// </summary>
    [HttpPost("{id:int}/refresh-meli-logistic")]
    public async Task<IActionResult> RefreshMeliLogistic(int id, [FromServices] Api.Services.MeliAccountService accountService, [FromServices] IHttpClientFactory httpFactory)
    {
        var items = await _db.MeliItems
            .Include(i => i.MeliAccount)
            .Where(i => i.CafeProductoId == id && i.Status == "active")
            .ToListAsync();
        if (items.Count == 0) return Ok(new { updated = 0, items = new object[0] });

        int updated = 0;
        var details = new List<object>();
        foreach (var item in items)
        {
            try
            {
                if (item.MeliAccount is null) { details.Add(new { item.MeliItemId, error = "sin cuenta" }); continue; }
                var token = await accountService.GetValidTokenAsync(item.MeliAccount);
                if (token is null) { details.Add(new { item.MeliItemId, error = "sin token" }); continue; }

                var http = httpFactory.CreateClient();
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                var url = $"https://api.mercadolibre.com/items/{item.MeliItemId}?attributes=shipping";
                var resp = await http.GetAsync(url);
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    var newTok = await accountService.GetValidTokenAsync(item.MeliAccount, forceRefresh: true);
                    if (newTok is not null)
                    {
                        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", newTok);
                        resp = await http.GetAsync(url);
                    }
                }
                if (!resp.IsSuccessStatusCode) { details.Add(new { item.MeliItemId, error = $"http {(int)resp.StatusCode}" }); continue; }

                var body = await resp.Content.ReadAsStringAsync();
                var doc = System.Text.Json.JsonDocument.Parse(body).RootElement;
                string? logistic = null;
                if (doc.TryGetProperty("shipping", out var sh) && sh.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (sh.TryGetProperty("logistic_type", out var lt) && lt.ValueKind == System.Text.Json.JsonValueKind.String)
                        logistic = lt.GetString();
                }
                item.LogisticType = logistic;
                item.UpdatedAt = DateTime.UtcNow;
                updated++;
                details.Add(new { item.MeliItemId, logistic });
            }
            catch (Exception ex)
            {
                details.Add(new { item.MeliItemId, error = ex.Message });
            }
        }
        await _db.SaveChangesAsync();
        return Ok(new { updated, total = items.Count, items = details });
    }

    public record PushMeliRequest(List<int>? MeliItemIds, bool PushPrice = true, bool PushStock = true);

    public record RenameMeliSkuRequest(List<int>? MeliItemIds);

    /// <summary>
    /// Renombra el SKU en MeLi para las publicaciones vinculadas a este cafe, aplicando el SKU del cafe.
    /// Usa seller_custom_field + attributes[SELLER_SKU] para cubrir categorias viejas y nuevas.
    /// </summary>
    [HttpPost("{id:int}/rename-meli-sku")]
    public async Task<IActionResult> RenameMeliSku(int id, [FromBody] RenameMeliSkuRequest req,
        [FromServices] Api.Services.MeliAccountService accountService,
        [FromServices] IHttpClientFactory httpFactory)
    {
        var cafe = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == id);
        if (cafe is null) return NotFound(new { error = "Cafe no encontrado" });
        if (string.IsNullOrWhiteSpace(cafe.Sku)) return BadRequest(new { error = "El cafe no tiene SKU cargado." });

        var newSku = cafe.Sku.Trim().ToUpperInvariant();
        var q = _db.MeliItems.Include(i => i.MeliAccount).Where(i => i.CafeProductoId == id && i.Status == "active");
        if (req.MeliItemIds is not null && req.MeliItemIds.Count > 0)
        {
            var ids = req.MeliItemIds;
            q = q.Where(i => ids.Contains(i.Id));
        }
        var items = await q.ToListAsync();

        int ok = 0;
        var details = new List<object>();
        foreach (var item in items)
        {
            try
            {
                if (item.MeliAccount is null) { details.Add(new { item.MeliItemId, success = false, message = "sin cuenta" }); continue; }
                if (string.Equals(item.Sku, newSku, StringComparison.OrdinalIgnoreCase))
                { details.Add(new { item.MeliItemId, success = true, message = "ya tenia el SKU correcto", oldSku = item.Sku, newSku }); ok++; continue; }
                var token = await accountService.GetValidTokenAsync(item.MeliAccount);
                if (token is null) { details.Add(new { item.MeliItemId, success = false, message = "sin token" }); continue; }

                var http = httpFactory.CreateClient();
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                // Pushear ambos: seller_custom_field (legacy) + attributes[SELLER_SKU] (categorias nuevas).
                var payload = new
                {
                    seller_custom_field = newSku,
                    attributes = new[] { new { id = "SELLER_SKU", value_name = newSku } }
                };
                var json = System.Text.Json.JsonSerializer.Serialize(payload);
                var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var resp = await http.PutAsync($"https://api.mercadolibre.com/items/{item.MeliItemId}", content);
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    var newTok = await accountService.GetValidTokenAsync(item.MeliAccount, forceRefresh: true);
                    if (newTok is not null)
                    {
                        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", newTok);
                        content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
                        resp = await http.PutAsync($"https://api.mercadolibre.com/items/{item.MeliItemId}", content);
                    }
                }
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    details.Add(new { item.MeliItemId, success = false, message = $"http {(int)resp.StatusCode}: {body.Substring(0, Math.Min(body.Length, 200))}" });
                    continue;
                }
                var oldSku = item.Sku;
                item.Sku = newSku;
                item.UpdatedAt = DateTime.UtcNow;
                ok++;
                details.Add(new { item.MeliItemId, success = true, message = "renombrado", oldSku, newSku });
            }
            catch (Exception ex)
            {
                details.Add(new { item.MeliItemId, success = false, message = ex.Message });
            }
        }
        await _db.SaveChangesAsync();
        return Ok(new { total = items.Count, ok, newSku, results = details });
    }

    /// <summary>
    /// Pushea precio + stock a las publicaciones MeLi vinculadas al cafe.
    /// Si no se pasan IDs, pushea todas. Devuelve resultado por publicacion.
    /// </summary>
    [HttpPost("{id:int}/push-meli")]
    public async Task<IActionResult> PushMeli(int id, [FromBody] PushMeliRequest req, [FromServices] Api.Services.MeliItemService meliService)
    {
        if (!await _db.CafeProductos.AnyAsync(p => p.Id == id))
            return NotFound(new { error = "Cafe no encontrado" });

        var q = _db.MeliItems.Where(i => i.CafeProductoId == id && i.Status == "active");
        if (req.MeliItemIds is not null && req.MeliItemIds.Count > 0)
        {
            var ids = req.MeliItemIds;
            q = q.Where(i => ids.Contains(i.Id));
        }
        var items = await q.ToListAsync();

        var results = new List<object>();
        foreach (var it in items)
        {
            try
            {
                var r = await meliService.PushFromProductAsync(it.Id, req.PushPrice, req.PushStock);
                results.Add(new { meliItemId = it.MeliItemId, success = r.Success, message = r.Message, pushedPrice = r.PushedPrice, pushedStock = r.PushedStock });
            }
            catch (Exception ex)
            {
                results.Add(new { meliItemId = it.MeliItemId, success = false, message = ex.Message, pushedPrice = (decimal?)null, pushedStock = (int?)null });
            }
        }
        return Ok(new { total = items.Count, ok = results.Count(r => (bool)r.GetType().GetProperty("success")!.GetValue(r)!), results });
    }

    [HttpGet("{id:int}/historial-precios")]
    public async Task<IActionResult> HistorialPrecios(int id)
    {
        if (!await _db.CafeProductos.AnyAsync(p => p.Id == id))
            return NotFound(new { error = "Producto no encontrado" });

        var rows = await _db.CafeHistorialPrecios
            .Where(h => h.ProductoId == id)
            .OrderByDescending(h => h.ChangedAt)
            .Select(h => new CafeHistorialPrecioDto(
                h.Id,
                h.Pvp1Anterior, h.Pvp2Anterior, h.CostoAnterior, h.IvaPctAnterior,
                h.Pvp1Nuevo, h.Pvp2Nuevo, h.CostoNuevo, h.IvaPctNuevo,
                h.ChangedAt, h.ChangedBy, h.Motivo))
            .ToListAsync();

        return Ok(rows);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCafeProductoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { error = "El nombre es obligatorio" });
        if (req.Costo < 0) return BadRequest(new { error = "El costo no puede ser negativo" });
        var cat = NormCat(req.Categoria);

        // OTROS exige PVP cargado a mano. Acepta cualquiera de los dos modelos:
        //   - Legacy: Pvp2
        //   - Nuevo (2026-05): PrecioOtro
        if (cat == "OTROS"
            && (!req.Pvp2.HasValue || req.Pvp2.Value <= 0)
            && (!req.PrecioOtro.HasValue || req.PrecioOtro.Value <= 0))
            return BadRequest(new { error = "Para productos OTROS el PVP es obligatorio" });

        // Resolver MarcaId: si vino MarcaId valido, lo uso. Si no, intento crear marca al vuelo desde el string Marca.
        var (marcaId, marcaNombre) = await ResolveMarcaAsync(req.MarcaId, req.Marca);

        var p = new CafeProducto
        {
            Sku = string.IsNullOrWhiteSpace(req.Sku) ? null : req.Sku.Trim().ToUpperInvariant(),
            Barcode = string.IsNullOrWhiteSpace(req.Barcode) ? null : req.Barcode.Trim(),
            Nombre = req.Nombre.Trim(),
            Categoria = cat,
            Marca = marcaNombre,
            MarcaId = marcaId,
            Costo = req.Costo,
            PrecioPorKg = req.PrecioPorKg,
            Pvp1 = req.Pvp1,
            Pvp2 = req.Pvp2,
            BarPctSobreCosto = cat == "OTROS" ? req.BarPctSobreCosto : null,
            // Modelo NUEVO de precios (solo OTROS, en CAFE quedan null):
            PrecioOtro = cat == "OTROS" ? req.PrecioOtro : null,
            PrecioBar = cat == "OTROS" ? req.PrecioBar : null,
            SinPrecioBar = cat == "OTROS" && req.SinPrecioBar,
            PrecioBulto = cat == "OTROS" ? req.PrecioBulto : null,
            PrecioBultoOtro = cat == "OTROS" ? req.PrecioBultoOtro : null,
            UxB = cat == "OTROS" ? req.UxB : null,
            OemId = cat == "OTROS" ? req.OemId : null,
            StockGramos = Math.Max(0m, req.StockGramos ?? 0m),
            StockUnidades = Math.Max(0, req.StockUnidades ?? 0),
            StockMinimoMeLi = req.StockMinimoMeLi.HasValue && req.StockMinimoMeLi.Value >= 0
                ? req.StockMinimoMeLi.Value : (int?)null,
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            IvaPct = NormalizeIva(req.IvaPct),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeProductos.Add(p);
        await _db.SaveChangesAsync();
        if (cat == "OTROS" && req.Packs is { Count: > 0 })
        {
            foreach (var pk in req.Packs)
            {
                if (pk.Cantidad <= 0 || string.IsNullOrWhiteSpace(pk.Nombre)) continue;
                _db.CafeProductoPacks.Add(new CafeProductoPack
                {
                    ProductoId = p.Id,
                    Cantidad = pk.Cantidad,
                    Nombre = pk.Nombre.Trim(),
                    PrecioOverride = pk.PrecioOverride,
                    SortOrder = pk.SortOrder,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
            }
            await _db.SaveChangesAsync();
        }
        var saved = await _db.CafeProductos.Include(x => x.OemNav).Include(x => x.MarcaNav).Include(x => x.Packs).FirstAsync(x => x.Id == p.Id);
        return Ok(Map(saved));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCafeProductoRequest req)
    {
        var p = await _db.CafeProductos.FindAsync(id);
        if (p is null) return NotFound(new { error = "Producto no encontrado" });

        // Snapshot de los valores ANTERIORES para grabar historial si cambian.
        var oldPvp1 = p.Pvp1; var oldPvp2 = p.Pvp2; var oldCosto = p.Costo; var oldIva = p.IvaPct;
        // 2026-05-30: snapshot de precio (PrecioOtro) e IVA para detectar cambio y disparar push.
        var oldPrecioOtro = p.PrecioOtro;
        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "El nombre no puede ser vacio" });
            p.Nombre = req.Nombre.Trim();
        }
        if (req.Sku is not null) p.Sku = string.IsNullOrWhiteSpace(req.Sku) ? null : req.Sku.Trim().ToUpperInvariant();
        if (req.Barcode is not null) p.Barcode = string.IsNullOrWhiteSpace(req.Barcode) ? null : req.Barcode.Trim();
        if (req.Categoria is not null) p.Categoria = NormCat(req.Categoria);
        // Marca: si viene MarcaId (incluyendo 0/null+ClearMarcaId), lo aplico. El string Marca queda
        // sincronizado con el nombre de la marca correspondiente, o null si se desvinculo.
        if (req.MarcaId.HasValue && req.MarcaId.Value > 0)
        {
            var (mid, mnombre) = await ResolveMarcaAsync(req.MarcaId, null);
            p.MarcaId = mid;
            p.Marca = mnombre;
        }
        else if (req.ClearMarcaId)
        {
            p.MarcaId = null;
            p.Marca = null;
        }
        else if (req.Marca is not null)
        {
            // Compatibilidad: si solo viene el texto, intento crear/matchear marca por nombre.
            var (mid, mnombre) = await ResolveMarcaAsync(null, req.Marca);
            p.MarcaId = mid;
            p.Marca = mnombre;
        }
        if (req.Costo.HasValue)
        {
            if (req.Costo.Value < 0) return BadRequest(new { error = "El costo no puede ser negativo" });
            p.Costo = req.Costo.Value;
        }
        if (req.PrecioPorKg.HasValue) p.PrecioPorKg = req.PrecioPorKg.Value;
        if (req.Pvp1.HasValue) p.Pvp1 = req.Pvp1.Value;
        if (req.Pvp2.HasValue) p.Pvp2 = req.Pvp2.Value;
        if (req.BarPctSobreCosto.HasValue) p.BarPctSobreCosto = req.BarPctSobreCosto.Value;
        else if (req.ClearBarPctSobreCosto) p.BarPctSobreCosto = null;
        // Modelo NUEVO de precios (solo OTROS):
        if (req.PrecioOtro.HasValue) p.PrecioOtro = req.PrecioOtro.Value;
        else if (req.ClearPrecioOtro) p.PrecioOtro = null;
        if (req.PrecioBar.HasValue) p.PrecioBar = req.PrecioBar.Value;
        else if (req.ClearPrecioBar) p.PrecioBar = null;
        // 2026-06-10: flag explicito "sin precio diferenciado BAR" (todos pagan PrecioOtro)
        if (req.SinPrecioBar.HasValue) p.SinPrecioBar = req.SinPrecioBar.Value;
        if (req.PrecioBulto.HasValue) p.PrecioBulto = req.PrecioBulto.Value;
        else if (req.ClearPrecioBulto) p.PrecioBulto = null;
        if (req.PrecioBultoOtro.HasValue) p.PrecioBultoOtro = req.PrecioBultoOtro.Value;
        else if (req.ClearPrecioBultoOtro) p.PrecioBultoOtro = null;
        // Precios FUTUROS (cambio programado)
        if (req.FechaAplicaPreciosFuturos.HasValue) p.FechaAplicaPreciosFuturos = req.FechaAplicaPreciosFuturos.Value.Date;
        else if (req.ClearFechaAplicaPreciosFuturos) p.FechaAplicaPreciosFuturos = null;
        if (req.PrecioPorKgFuturo.HasValue) p.PrecioPorKgFuturo = req.PrecioPorKgFuturo.Value;
        else if (req.ClearPrecioPorKgFuturo) p.PrecioPorKgFuturo = null;
        if (req.PrecioBarFuturo.HasValue) p.PrecioBarFuturo = req.PrecioBarFuturo.Value;
        else if (req.ClearPrecioBarFuturo) p.PrecioBarFuturo = null;
        if (req.PrecioOtroFuturo.HasValue) p.PrecioOtroFuturo = req.PrecioOtroFuturo.Value;
        else if (req.ClearPrecioOtroFuturo) p.PrecioOtroFuturo = null;
        if (req.PrecioBultoFuturo.HasValue) p.PrecioBultoFuturo = req.PrecioBultoFuturo.Value;
        else if (req.ClearPrecioBultoFuturo) p.PrecioBultoFuturo = null;
        if (req.PrecioBultoOtroFuturo.HasValue) p.PrecioBultoOtroFuturo = req.PrecioBultoOtroFuturo.Value;
        else if (req.ClearPrecioBultoOtroFuturo) p.PrecioBultoOtroFuturo = null;
        if (req.UxB.HasValue) p.UxB = req.UxB.Value;
        else if (req.ClearUxB) p.UxB = null;
        if (req.OemId.HasValue) p.OemId = req.OemId.Value;
        else if (req.ClearOemId) p.OemId = null;
        var stockCambio = false;
        if (req.StockGramos.HasValue && req.StockGramos.Value != p.StockGramos)
        {
            p.StockGramos = Math.Max(0m, req.StockGramos.Value);
            stockCambio = true;
        }
        if (req.StockUnidades.HasValue && req.StockUnidades.Value != p.StockUnidades)
        {
            p.StockUnidades = Math.Max(0, req.StockUnidades.Value);
            stockCambio = true;
        }
        if (stockCambio) p.StockChangedAt = DateTime.UtcNow;
        // 2026-05-25: stock mínimo MeLi por producto. Si vino el flag clear → null. Si no, asignar el valor.
        // 2026-06-02 FIX: detectar cambio para disparar push event-driven. El stock efectivo
        // que MeLi recibe es (StockUnidades - StockMinimoMeLi), entonces si cambia StockMinimoMeLi
        // tambien hay que avisarle a MeLi. Antes no se hacia → C949NEG (entre otros) quedo paused
        // despues de bajar la reserva porque el push nunca se disparo.
        var oldStockMinimoMeLi = p.StockMinimoMeLi;
        if (req.ClearStockMinimoMeLi) p.StockMinimoMeLi = null;
        else if (req.StockMinimoMeLi.HasValue && req.StockMinimoMeLi.Value >= 0) p.StockMinimoMeLi = req.StockMinimoMeLi.Value;
        bool stockMinimoCambio = oldStockMinimoMeLi != p.StockMinimoMeLi;
        if (stockMinimoCambio)
        {
            stockCambio = true;
            p.StockChangedAt = DateTime.UtcNow;
        }
        // Si cambió el stock, sincronizar Cafe_StockPorDeposito (deposito principal)
        // para que la pantalla stock-masivo no quede desfasada.
        if (stockCambio)
        {
            var depDefault = await _db.CafeDepositos
                .Where(d => d.IsDefault && d.IsActive)
                .Select(d => (int?)d.Id).FirstOrDefaultAsync();
            depDefault ??= await _db.CafeDepositos.OrderBy(d => d.Id).Select(d => (int?)d.Id).FirstOrDefaultAsync();
            if (depDefault.HasValue)
            {
                var spd = await _db.CafeStockPorDeposito
                    .FirstOrDefaultAsync(s => s.ProductoId == p.Id && s.DepositoId == depDefault.Value);
                if (spd is null)
                {
                    _db.CafeStockPorDeposito.Add(new CafeStockPorDeposito
                    {
                        ProductoId = p.Id, DepositoId = depDefault.Value,
                        StockUnidades = p.StockUnidades, StockGramos = p.StockGramos,
                        UpdatedAt = DateTime.UtcNow
                    });
                }
                else
                {
                    spd.StockUnidades = p.StockUnidades;
                    spd.StockGramos = p.StockGramos;
                    spd.UpdatedAt = DateTime.UtcNow;
                }
            }
        }
        // Si cambió el stock, disparar push event-driven a MeLi después del SaveChanges
        var dispararPush = stockCambio;
        if (req.Notas is not null) p.Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim();
        if (req.IsActive.HasValue) p.IsActive = req.IsActive.Value;
        if (req.IvaPct.HasValue) p.IvaPct = NormalizeIva(req.IvaPct);
        p.UpdatedAt = DateTime.UtcNow;

        // 2026-05-30: detectar cambio de PRECIO (PrecioOtro o IvaPct) y disparar push event-driven.
        // Marca PriceChangedAt para que el background service también lo procese si el push inline falla.
        bool precioOtroOIvaCambio = (oldPrecioOtro ?? -1m) != (p.PrecioOtro ?? -1m) || oldIva != p.IvaPct;
        if (precioOtroOIvaCambio) p.PriceChangedAt = DateTime.UtcNow;
        var dispararPushPrecio = precioOtroOIvaCambio;

        // Si algun precio cambio, grabar historial.
        bool precioCambio = oldPvp1 != p.Pvp1 || oldPvp2 != p.Pvp2 || oldCosto != p.Costo || oldIva != p.IvaPct;
        if (precioCambio)
        {
            _db.CafeHistorialPrecios.Add(new CafeHistorialPrecio
            {
                ProductoId = p.Id,
                Pvp1Anterior = oldPvp1, Pvp2Anterior = oldPvp2, CostoAnterior = oldCosto, IvaPctAnterior = oldIva,
                Pvp1Nuevo = p.Pvp1, Pvp2Nuevo = p.Pvp2, CostoNuevo = p.Costo, IvaPctNuevo = p.IvaPct,
                ChangedAt = DateTime.UtcNow,
                ChangedBy = User?.Identity?.Name ?? "Sistema"
            });
        }

        // Sincronizar packs si vinieron en el request (null = no tocar; lista vacia = borrar todos).
        if (req.Packs is not null)
        {
            var existentes = await _db.CafeProductoPacks.Where(x => x.ProductoId == p.Id).ToListAsync();
            var idsRecibidos = req.Packs.Where(x => x.Id.HasValue).Select(x => x.Id!.Value).ToHashSet();
            // Borrar los que ya no vienen
            foreach (var viejo in existentes.Where(x => !idsRecibidos.Contains(x.Id)).ToList())
            {
                _db.CafeProductoPacks.Remove(viejo);
            }
            // Crear / actualizar
            foreach (var pk in req.Packs)
            {
                if (pk.Cantidad <= 0 || string.IsNullOrWhiteSpace(pk.Nombre)) continue;
                if (pk.Id.HasValue)
                {
                    var existente = existentes.FirstOrDefault(x => x.Id == pk.Id.Value);
                    if (existente is null) continue;
                    existente.Cantidad = pk.Cantidad;
                    existente.Nombre = pk.Nombre.Trim();
                    existente.PrecioOverride = pk.PrecioOverride;
                    existente.SortOrder = pk.SortOrder;
                }
                else
                {
                    _db.CafeProductoPacks.Add(new CafeProductoPack
                    {
                        ProductoId = p.Id,
                        Cantidad = pk.Cantidad,
                        Nombre = pk.Nombre.Trim(),
                        PrecioOverride = pk.PrecioOverride,
                        SortOrder = pk.SortOrder,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }
        }

        await _db.SaveChangesAsync();
        var saved = await _db.CafeProductos.Include(x => x.OemNav).Include(x => x.MarcaNav).Include(x => x.Packs).FirstAsync(x => x.Id == p.Id);
        // Si cambió el stock, disparar push a MeLi en background (respeta kill switches)
        if (dispararPush) FireAndForgetPushMeli(p.Id);
        // 2026-05-30: si cambió el precio (PrecioOtro o IvaPct), disparar push de PRECIO a las
        // publicaciones "claimed" (SyncPrecio=true). Las no-claimed se ignoran en silencio.
        if (dispararPushPrecio) FireAndForgetPushPrecioMeli(p.Id);
        return Ok(Map(saved));
    }

    /// <summary>Resuelve marca: si viene un MarcaId valido, lo busca. Si no y viene texto, lo busca/crea
    /// por nombre. Devuelve (MarcaId, NombreMarca) — null/null si no hay marca.</summary>
    private async Task<(int?, string?)> ResolveMarcaAsync(int? marcaId, string? marcaTexto)
    {
        if (marcaId.HasValue && marcaId.Value > 0)
        {
            var existing = await _db.CafeMarcas.FindAsync(marcaId.Value);
            if (existing is null) return (null, null);
            return (existing.Id, existing.Nombre);
        }
        if (string.IsNullOrWhiteSpace(marcaTexto)) return (null, null);
        var nombre = marcaTexto.Trim();
        var match = await _db.CafeMarcas.FirstOrDefaultAsync(m => m.Nombre == nombre);
        if (match is not null) return (match.Id, match.Nombre);
        // Crear al vuelo
        var nuevo = new CafeMarca { Nombre = nombre, IsActive = true, CreatedAt = DateTime.UtcNow };
        _db.CafeMarcas.Add(nuevo);
        await _db.SaveChangesAsync();
        return (nuevo.Id, nuevo.Nombre);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var p = await _db.CafeProductos.FindAsync(id);
        if (p is null) return NotFound(new { error = "Producto no encontrado" });
        _db.CafeProductos.Remove(p);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    private static string NormCat(string? c)
    {
        if (string.IsNullOrWhiteSpace(c)) return "CAFE";
        var v = c.Trim().ToUpperInvariant();
        return CategoriasValidas.Contains(v) ? v : "CAFE";
    }

    // IVA permitido: 21 (default) o 10.5. Cualquier otro valor cae a 21.
    private static decimal NormalizeIva(decimal? iva)
    {
        if (!iva.HasValue) return 21m;
        var v = iva.Value;
        if (v == 10.5m) return 10.5m;
        return 21m;
    }
}
