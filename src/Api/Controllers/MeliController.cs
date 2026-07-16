using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MeliController : ControllerBase
{
    private readonly MeliAccountService _service;
    private readonly MeliOrderService _orderService;
    private readonly MeliItemService _itemService;
    private readonly AiService _aiService;
    private readonly SyncProgressService _syncProgress;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MeliStockSyncService _stockSync;

    public MeliController(MeliAccountService service, MeliOrderService orderService, MeliItemService itemService, AiService aiService, SyncProgressService syncProgress, IServiceScopeFactory scopeFactory, MeliStockSyncService stockSync)
    {
        _service = service;
        _orderService = orderService;
        _itemService = itemService;
        _aiService = aiService;
        _syncProgress = syncProgress;
        _scopeFactory = scopeFactory;
        _stockSync = stockSync;
    }

    [HttpGet("accounts")]
    public async Task<IActionResult> GetAccounts()
    {
        var accounts = await _service.GetAccountsAsync();
        return Ok(accounts);
    }

    [HttpGet("auth-url")]
    public IActionResult GetAuthUrl()
    {
        try
        {
            var url = _service.GetAuthUrl();
            return Ok(new MeliAuthUrlResponse(url));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("callback")]
    public async Task<IActionResult> HandleCallback([FromBody] MeliCallbackRequest request)
    {
        try
        {
            var account = await _service.HandleCallbackAsync(request.Code);
            return Ok(account);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }


    [HttpPost("test-user")]
    public async Task<IActionResult> CreateTestUser([FromBody] CreateTestUserRequest request)
    {
        try
        {
            var json = await _service.CreateTestUserAsync(request.SiteId);
            return Content(json, "application/json");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("accounts/{id}/stats")]
    public async Task<IActionResult> GetAccountStats(int id)
    {
        var stats = await _service.GetAccountStatsAsync(id);
        if (stats is null) return NotFound();
        return Ok(stats);
    }

    [HttpDelete("accounts/{id}")]
    public async Task<IActionResult> DeleteAccount(int id)
    {
        var deleted = await _service.DeleteAccountAsync(id);
        if (!deleted) return NotFound();
        return NoContent();
    }

    [HttpGet("orders")]
    public async Task<IActionResult> GetOrders(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? accountId)
    {
        try
        {
            var dateFrom = from ?? DateTime.UtcNow.AddDays(-30);
            var dateTo = to ?? DateTime.UtcNow;
            var result = await _orderService.GetOrdersAsync(dateFrom, dateTo, accountId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("orders/detail/{meliOrderId}")]
    public async Task<IActionResult> GetOrderDetail(long meliOrderId)
    {
        try
        {
            var result = await _orderService.GetOrderDetailAsync(meliOrderId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("orders/pack-detail/{packId}")]
    public async Task<IActionResult> GetPackDetail(long packId)
    {
        try
        {
            var result = await _orderService.GetPackDetailAsync(packId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("orders/sync")]
    public async Task<IActionResult> SyncOrders(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to)
    {
        try
        {
            var dateFrom = from ?? DateTime.UtcNow.AddDays(-30);
            var dateTo = to ?? DateTime.UtcNow;
            var result = await _orderService.SyncOrdersAsync(dateFrom, dateTo);
            // Despues de sincronizar ordenes, descontar stock de las nuevas (las que tienen
            // StockDiscounted=false). Es no-bloqueante: si falla, el sync queda OK igual.
            try { await _stockSync.ProcessPendingAsync(); }
            catch (Exception ex2) { Console.WriteLine($"Stock sync post-orders fallo: {ex2.Message}"); }
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>2026-06-15: Refresca el estado de envío de las órdenes paid pendientes
    /// (ready_to_print, etc) consultando una por una a MeLi. Sirve para destrabar
    /// la sobre-estimación del stock reservado cuando hay órdenes ya despachadas
    /// que quedaron congeladas en estado pre-despacho en nuestra base.</summary>
    [HttpPost("orders/refresh-pending")]
    public async Task<IActionResult> RefreshPendingOrders([FromQuery] int dias = 7)
    {
        try
        {
            var refrescadas = await _orderService.RefreshPendingOrdersAsync(dias);
            return Ok(new { refrescadas, dias });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Procesa ordenes MeLi con StockDiscounted=false: descuenta stock de cafes/otros
    /// segun el linkeo de MeliItems.CafeProductoId + CafeFormato. Util para correr a demanda
    /// si el auto-trigger no se disparo.</summary>
    [HttpPost("orders/process-stock-pending")]
    public async Task<IActionResult> ProcessStockPending()
    {
        try
        {
            var r = await _stockSync.ProcessPendingAsync(maxBatch: 1000);
            return Ok(new { ok = true, procesadas = r.Procesadas, descontadasCafe = r.DescontadasCafe, descontadasOtros = r.DescontadasOtros, sinLinkear = r.SinLinkear, errores = r.Errores });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    public record CafePushPreviewRow(string MeliSku, string MeliItemId, string ProductoSku, string ProductoNombre,
        string Formato, decimal PrecioActualMeLi, decimal PrecioNuevoMeLi, decimal PrecioNetoSistema,
        decimal Ratio, int StockActualMeLi, int StockNuevoMeLi);

    /// <summary>Muestra una tabla de cómo quedarían los precios y stocks si se hace push de cafés a MeLi.
    /// NO hace push — es solo preview.</summary>
    [HttpGet("cafe/push-preview")]
    public async Task<IActionResult> CafePushPreview([FromServices] MeliCafePricePushService pushSvc,
                                                     [FromServices] Api.Data.AppDbContext db)
    {
        var cfg = await db.CafeSettings.FindAsync(1) ?? new Api.Models.CafeSetting { Id = 1 };
        var items = await db.MeliItems
            .Where(mi => mi.CafeProductoId != null && mi.CafeFormato != null && mi.PriceRatioOverIva != null)
            .OrderBy(mi => mi.Sku).Take(500)
            .ToListAsync();
        var prodIds = items.Select(i => i.CafeProductoId!.Value).Distinct().ToList();
        var prods = await db.CafeProductos.Where(p => prodIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        var rows = new List<CafePushPreviewRow>();
        foreach (var mi in items)
        {
            if (!prods.TryGetValue(mi.CafeProductoId!.Value, out var prod)) continue;
            var precioNeto = pushSvc.CalcularPrecioSistemaNeto(prod, mi.CafeFormato!, cfg);
            if (precioNeto <= 0) continue;
            var precioNuevo = Math.Round(precioNeto * 1.21m * mi.PriceRatioOverIva!.Value, 0);
            var gramos = mi.CafeFormato!.ToUpperInvariant() switch { "1KG" => 1000, "MEDIO" => 500, "CUARTO" => 250, _ => 1000 };
            var stockNuevo = (int)Math.Floor(prod.StockGramos / gramos);
            if (stockNuevo < 0) stockNuevo = 0;
            rows.Add(new CafePushPreviewRow(
                mi.Sku ?? "", mi.MeliItemId, prod.Sku ?? "", prod.Nombre,
                mi.CafeFormato!, mi.Price, precioNuevo, precioNeto,
                mi.PriceRatioOverIva!.Value, mi.AvailableQuantity, stockNuevo));
        }
        return Ok(new { rows, count = rows.Count });
    }

    private static int _cafePushRunning = 0;
    private static DateTime? _cafePushStartedAt;
    private static DateTime? _cafePushFinishedAt;
    private static object? _cafePushResult;
    private static string? _cafePushError;

    /// <summary>Push de precios+stock de cafés a MeLi. Background fire-and-forget.</summary>
    [HttpPost("cafe/push")]
    public IActionResult CafePush([FromServices] IServiceScopeFactory scopeFactory)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _cafePushRunning, 1, 0) != 0)
            return Ok(new { ok = true, started = false, message = "Ya hay un push corriendo" });
        _cafePushStartedAt = DateTime.UtcNow;
        _cafePushFinishedAt = null;
        _cafePushResult = null;
        _cafePushError = null;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<MeliCafePricePushService>();
                var r = await svc.PushAllCafesAsync(CancellationToken.None);
                _cafePushResult = new { procesadas = r.Procesadas, ok = r.Ok, errores = r.Errores, mensajes = r.Mensajes.Take(50).ToList() };
            }
            catch (Exception ex)
            {
                _cafePushError = ex.Message;
            }
            finally
            {
                _cafePushFinishedAt = DateTime.UtcNow;
                System.Threading.Interlocked.Exchange(ref _cafePushRunning, 0);
            }
        });
        return Ok(new { ok = true, started = true, message = "Push iniciado en background" });
    }

    /// <summary>Pushea SOLO una publicación a MeLi. Útil para piloto/testing.
    /// Devuelve sincrónico (no es background) porque es una sola.</summary>
    [HttpPost("cafe/push-one/{meliItemId}")]
    public async Task<IActionResult> CafePushOne(string meliItemId, [FromServices] MeliCafePricePushService svc)
    {
        var r = await svc.PushSingleAsync(meliItemId, HttpContext.RequestAborted);
        return Ok(new {
            procesadas = r.Procesadas,
            ok = r.Ok,
            errores = r.Errores,
            mensajes = r.Mensajes
        });
    }

    [HttpGet("cafe/push/status")]
    public IActionResult CafePushStatus()
    {
        return Ok(new {
            running = _cafePushRunning != 0,
            startedAt = _cafePushStartedAt, finishedAt = _cafePushFinishedAt,
            error = _cafePushError, result = _cafePushResult
        });
    }

    [HttpGet("items")]
    public async Task<IActionResult> GetItems(
        [FromQuery] int? accountId,
        [FromQuery] string? status)
    {
        try
        {
            var result = await _itemService.GetItemsAsync(accountId, status);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("items/sync-by-id")]
    public async Task<IActionResult> SyncItemById([FromBody] SyncItemByIdRequest request)
    {
        try
        {
            var result = await _itemService.SyncItemsByIdAsync(request.MeliItemId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    public class CambioDto
    {
        public int Id { get; set; }
        public string MeliItemId { get; set; } = "";
        public string? Sku { get; set; }
        public string? Title { get; set; }
        public string Tipo { get; set; } = "";
        public string? ValorAnterior { get; set; }
        public string? ValorNuevo { get; set; }
        public decimal? Delta { get; set; }
        public decimal? DeltaPct { get; set; }
        public string Source { get; set; } = "";
        public DateTime DetectedAt { get; set; }
        public DateTime? SeenAt { get; set; }
        public string? AccountNickname { get; set; }
    }

    /// <summary>Lista cambios detectados (precio sube/baja, status active/paused).
    /// Filtros: soloSinVer, tipos, desde, hasta, limit. Default: últimos 200 sin filtro.</summary>
    [HttpGet("cambios")]
    public async Task<IActionResult> GetCambios(
        [FromQuery] bool soloSinVer = false,
        [FromQuery] string? tipo = null,
        [FromQuery] DateTime? desde = null,
        [FromQuery] DateTime? hasta = null,
        [FromQuery] int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var q = _itemService is null ? null : _itemService.GetType(); // dummy, real query below
        var query = HttpContext.RequestServices.GetRequiredService<Api.Data.AppDbContext>().MeliCambiosDetectados
            .AsNoTracking().AsQueryable();
        if (soloSinVer) query = query.Where(c => c.SeenAt == null);
        if (!string.IsNullOrWhiteSpace(tipo)) query = query.Where(c => c.Tipo == tipo);
        if (desde.HasValue) query = query.Where(c => c.DetectedAt >= desde.Value);
        if (hasta.HasValue) query = query.Where(c => c.DetectedAt <= hasta.Value.AddDays(1));

        var lista = await query
            .OrderByDescending(c => c.DetectedAt)
            .Take(limit)
            .ToListAsync();

        // Resolver nicknames de cuenta
        var accIds = lista.Where(c => c.MeliAccountId.HasValue).Select(c => c.MeliAccountId!.Value).Distinct().ToList();
        var accs = await HttpContext.RequestServices.GetRequiredService<Api.Data.AppDbContext>().MeliAccounts
            .Where(a => accIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Nickname);

        return Ok(lista.Select(c => new CambioDto
        {
            Id = c.Id, MeliItemId = c.MeliItemId, Sku = c.Sku, Title = c.Title,
            Tipo = c.Tipo, ValorAnterior = c.ValorAnterior, ValorNuevo = c.ValorNuevo,
            Delta = c.Delta, DeltaPct = c.DeltaPct, Source = c.Source,
            DetectedAt = c.DetectedAt, SeenAt = c.SeenAt,
            AccountNickname = c.MeliAccountId.HasValue && accs.TryGetValue(c.MeliAccountId.Value, out var nk) ? nk : null
        }).ToList());
    }

    [HttpGet("cambios/count-pending")]
    public async Task<IActionResult> CountCambiosPendientes([FromServices] MeliCambioDetectadoService svc)
    {
        var n = await svc.CountUnseenAsync(HttpContext.RequestAborted);
        return Ok(new { count = n });
    }

    [HttpPost("cambios/{id:int}/mark-seen")]
    public async Task<IActionResult> MarkCambioSeen(int id, [FromServices] Api.Data.AppDbContext db)
    {
        var c = await db.MeliCambiosDetectados.FindAsync(id);
        if (c is null) return NotFound();
        c.SeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok();
    }

    [HttpPost("cambios/mark-all-seen")]
    public async Task<IActionResult> MarkAllCambiosSeen([FromServices] Api.Data.AppDbContext db)
    {
        var sql = "UPDATE MeliCambiosDetectados SET SeenAt = SYSUTCDATETIME() WHERE SeenAt IS NULL";
        var n = await db.Database.ExecuteSqlRawAsync(sql);
        return Ok(new { updated = n });
    }

    /// <summary>2026-07-16: cantidad de publicaciones que esperan revisión de precio (pausadas con
    /// stock que el robot NO despertó + reactivadas detectadas). Alimenta el cartel rojo del layout.</summary>
    [HttpGet("cambios/count-revisar")]
    public async Task<IActionResult> CountCambiosRevisar([FromServices] Api.Data.AppDbContext db)
    {
        var tipos = new[] { "PAUSADA_CON_STOCK", "STATUS_ACTIVE" };
        var n = await db.MeliCambiosDetectados.AsNoTracking()
            .CountAsync(c => c.SeenAt == null && tipos.Contains(c.Tipo));
        return Ok(new { count = n });
    }

    /// <summary>2026-07-16: activa una publicación pausada DESDE EL SISTEMA (el usuario ya revisó el
    /// precio). PUT status=active en MeLi + empuja el stock actual (que el robot no tocó mientras
    /// estaba pausada) + marca el cambio como visto. El id es el del registro en cambios detectados.</summary>
    [HttpPost("cambios/{id:int}/activar-publicacion")]
    public async Task<IActionResult> ActivarPublicacion(int id,
        [FromServices] Api.Data.AppDbContext db,
        [FromServices] MeliStockPushService pushService)
    {
        var cambio = await db.MeliCambiosDetectados.FindAsync(id);
        if (cambio is null) return NotFound(new { error = "Aviso no encontrado" });

        var item = await db.MeliItems.Include(i => i.MeliAccount)
            .FirstOrDefaultAsync(i => i.MeliItemId == cambio.MeliItemId);
        if (item?.MeliAccount is null)
            return BadRequest(new { error = "No encuentro la publicación o su cuenta MeLi en el sistema" });

        var token = await _service.GetValidTokenAsync(item.MeliAccount);
        if (token is null)
            return BadRequest(new { error = "Token de MercadoLibre inválido — reconectá la cuenta en Integraciones" });

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        http.Timeout = TimeSpan.FromSeconds(30);
        var body = new StringContent("{\"status\":\"active\"}", System.Text.Encoding.UTF8, "application/json");
        var resp = await http.PutAsync($"https://api.mercadolibre.com/items/{cambio.MeliItemId}", body);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            return BadRequest(new { error = $"MeLi rechazó la activación ({(int)resp.StatusCode}): {(err.Length > 200 ? err[..200] : err)}" });
        }

        // Reflejar en nuestra copia y cerrar el aviso
        var filas = await db.MeliItems.Where(i => i.MeliItemId == cambio.MeliItemId).ToListAsync();
        foreach (var f in filas) f.Status = "active";
        cambio.SeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Ahora que está activa, empujar el stock real (mientras estuvo pausada no se tocó).
        string? pushDetalle = null;
        try
        {
            var r = await pushService.PushStockForMeliItemsAsync(new List<string> { cambio.MeliItemId });
            pushDetalle = r.Mensajes.FirstOrDefault();
        }
        catch (Exception ex) { pushDetalle = "Activada OK, pero el push de stock falló: " + ex.Message; }

        return Ok(new { ok = true, detalle = pushDetalle });
    }

    /// <summary>PUSH MASIVO: marca todos los productos OTROS como "stock pendiente de push" y los
    /// procesa via el background sweep. Útil después de un import masivo donde el push event-driven
    /// no se disparó. Idempotente: se puede llamar las veces que sea.</summary>
    [HttpPost("items/push-stock-masivo-otros")]
    public async Task<IActionResult> PushStockMasivoOtros([FromServices] MeliStockPushService pushSvc,
        [FromServices] Api.Data.AppDbContext db,
        [FromQuery] int batchSize = 50)
    {
        // 1) Activar el master kill switch si está OFF (la idea es operar ahora)
        var master = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == "meli.stock_push.master_enabled");
        if (master == null || !string.Equals(master.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "master_enabled está en false. Activalo primero." });

        // 2) Tomar los productos OTROS linkeados a MLAs activas
        var productosIds = await (
            from p in db.CafeProductos
            join mc in db.MeliItemComponentes on p.Id equals mc.CafeProductoId
            join mi in db.MeliItems on mc.MeliItemId equals mi.MeliItemId
            where p.Categoria == "OTROS" && p.IsActive && mi.Status == "active"
            select p.Id
        ).Distinct().Take(batchSize).ToListAsync();

        if (productosIds.Count == 0) return Ok(new { ok = true, message = "No hay productos para procesar" });

        int ok = 0, skipped = 0, err = 0;
        var detalle = new List<string>();
        foreach (var pid in productosIds)
        {
            try
            {
                var r = await pushSvc.PushStockForProductoAsync(pid, HttpContext.RequestAborted);
                ok += r.Ok; skipped += r.Skipped; err += r.Errores;
                if (r.Mensajes.Count > 0) detalle.Add($"Producto {pid}: {string.Join(" | ", r.Mensajes.Take(2))}");
            }
            catch (Exception ex) { err++; detalle.Add($"Producto {pid}: {ex.Message}"); }
        }
        return Ok(new { procesados = productosIds.Count, ok, skipped, err, detalle = detalle.Take(20).ToList() });
    }

    /// <summary>2026-06-01: devuelve el costo total del producto/combo asociado a una MLA. Util para
    /// calcular margen en el modal de detalle. Si la MLA tiene CafeProductoId directo, devuelve su Costo.
    /// Si tiene CafeComboId o MeliItemComponentes, suma costos de los componentes × cantidad.</summary>
    public record ProductCostDto(decimal TotalCost, List<ProductCostDto.Comp> Components, string Source)
    {
        public record Comp(string Sku, string Nombre, decimal CostoUnit, decimal Cantidad, decimal CostoTotal);
    }

    [HttpGet("items/{meliItemId}/product-cost")]
    public async Task<IActionResult> GetProductCost(string meliItemId, [FromServices] Api.Data.AppDbContext db)
    {
        var mi = await db.MeliItems.AsNoTracking().FirstOrDefaultAsync(x => x.MeliItemId == meliItemId);
        if (mi == null) return NotFound(new { error = "MLA no encontrada" });

        // 2026-07-14: COMPUESTO con OEM (caja+tapa) → el costo sale del OEM del producto COMPLETO, en UNA
        // línea, NO de la suma de las piezas. Igual que la venta y que MeliPricePushService.
        if (mi.CafeComboId.HasValue)
        {
            var comboC = await db.CafeCombos.AsNoTracking().FirstOrDefaultAsync(c => c.Id == mi.CafeComboId.Value);
            if (comboC is not null && comboC.EsCompuesto && comboC.OemId.HasValue)
            {
                var oemC = await db.CafeOems.AsNoTracking().FirstOrDefaultAsync(o => o.Id == comboC.OemId.Value);
                if (oemC is not null && oemC.Costo > 0)
                {
                    var multC = comboC.MultiplicadorOem ?? 1m;
                    if (multC <= 0) multC = 1m;
                    var costoOemC = Math.Round(oemC.Costo * multC, 2);
                    var compOem = new ProductCostDto.Comp(oemC.Codigo ?? "OEM", $"OEM {oemC.Codigo} · {oemC.Descripcion}", oemC.Costo, multC, costoOemC);
                    return Ok(new ProductCostDto(costoOemC, new List<ProductCostDto.Comp> { compOem }, "compuesto_oem"));
                }
            }
        }

        var comps = new List<ProductCostDto.Comp>();
        string source = "none";

        // 1) Modelo nuevo: MeliItemComponentes (combos via componentes)
        var mecs = await (
            from c in db.MeliItemComponentes
            join p in db.CafeProductos on c.CafeProductoId equals p.Id
            where c.MeliItemId == meliItemId
            select new { p.Sku, p.Nombre, p.Costo, c.Cantidad }
        ).ToListAsync();

        if (mecs.Count > 0)
        {
            source = "componentes";
            foreach (var x in mecs)
                comps.Add(new ProductCostDto.Comp(x.Sku ?? "", x.Nombre ?? "", x.Costo, x.Cantidad, x.Costo * x.Cantidad));
        }
        else if (mi.CafeComboId.HasValue)
        {
            // 2) Legacy: combo directo, expandir items
            var items = await (
                from ci in db.CafeComboItems
                join p in db.CafeProductos on ci.ProductoId equals p.Id
                where ci.ComboId == mi.CafeComboId.Value
                select new { p.Sku, p.Nombre, p.Costo, ci.Cantidad }
            ).ToListAsync();
            source = "combo_legacy";
            foreach (var x in items)
                comps.Add(new ProductCostDto.Comp(x.Sku ?? "", x.Nombre ?? "", x.Costo, x.Cantidad, x.Costo * x.Cantidad));
        }
        else if (mi.CafeProductoId.HasValue)
        {
            // 3) Legacy: producto suelto directo
            var p = await db.CafeProductos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mi.CafeProductoId.Value);
            if (p != null)
            {
                source = "producto_directo";
                // 2026-06-19: cafe fraccionado — sku F*.4 = 0.25 kg, F*.2 = 0.5 kg, sin sufijo = 1 kg.
                decimal cant = 1m;
                if (!string.IsNullOrEmpty(mi.Sku))
                {
                    if (mi.Sku.EndsWith(".4")) cant = 0.25m;
                    else if (mi.Sku.EndsWith(".2")) cant = 0.5m;
                }
                comps.Add(new ProductCostDto.Comp(p.Sku ?? "", p.Nombre ?? "", p.Costo, cant, p.Costo * cant));
            }
        }

        // 2026-06-19: dedup + factor por SKU tambien para path componentes (mismas reglas que
        // MeliItemService): si todos los componentes son del mismo producto, deduplicar.
        if (source == "componentes" && comps.Count > 1)
        {
            comps = comps
                .GroupBy(c => c.Sku)
                .Select(g => g.First())
                .ToList();
        }

        var total = comps.Sum(c => c.CostoTotal);
        return Ok(new ProductCostDto(total, comps, source));
    }

    /// <summary>2026-06-12: precios mayoristas (PxQ) + límites de unidades por compra. Lee en vivo de MeLi.</summary>
    [HttpGet("items/{meliItemId}/mayorista")]
    public async Task<IActionResult> GetMayorista(string meliItemId, [FromServices] MeliItemService svc)
    {
        try { return Ok(await svc.GetMayoristaAsync(meliItemId)); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    public record SaveMayoristaRequest(List<MeliItemService.MayoristaTier>? Tiers, int? MinPorCompra, int? MaxPorCompra);

    /// <summary>2026-06-12: guarda escalones PxQ + límites min/max por compra directo en MeLi.</summary>
    [HttpPut("items/{meliItemId}/mayorista")]
    public async Task<IActionResult> SaveMayorista(string meliItemId, [FromBody] SaveMayoristaRequest req,
        [FromServices] MeliItemService svc)
    {
        try
        {
            var result = await svc.SaveMayoristaAsync(meliItemId, req.Tiers ?? new(), req.MinPorCompra, req.MaxPorCompra);
            return Ok(result);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>2026-06-12: stock en el depósito "Full MeLi" de los productos linkeados a esta MLA.
    /// Informativo para la ficha de publicación (cartelito "Stock Full: X u.").</summary>
    [HttpGet("items/{meliItemId}/stock-full")]
    public async Task<IActionResult> GetStockFull(string meliItemId, [FromServices] Api.Data.AppDbContext db)
    {
        var mi = await db.MeliItems.AsNoTracking().FirstOrDefaultAsync(x => x.MeliItemId == meliItemId);
        if (mi == null) return NotFound(new { error = "MLA no encontrada" });

        // Resolver los productos linkeados (componentes > combo legacy > producto directo)
        var productoIds = await db.MeliItemComponentes
            .Where(c => c.MeliItemId == meliItemId)
            .Select(c => c.CafeProductoId).Distinct().ToListAsync();
        if (productoIds.Count == 0 && mi.CafeComboId.HasValue)
            productoIds = await db.CafeComboItems.Where(ci => ci.ComboId == mi.CafeComboId.Value)
                .Select(ci => ci.ProductoId).Distinct().ToListAsync();
        if (productoIds.Count == 0 && mi.CafeProductoId.HasValue)
            productoIds = new List<int> { mi.CafeProductoId.Value };
        if (productoIds.Count == 0) return Ok(new { stockFull = (int?)null });

        var stockFull = await (
            from s in db.CafeStockPorDeposito
            join d in db.CafeDepositos on s.DepositoId equals d.Id
            where d.Nombre == "Full MeLi" && productoIds.Contains(s.ProductoId)
            select (int?)s.StockUnidades
        ).SumAsync() ?? 0;

        return Ok(new { stockFull = (int?)stockFull });
    }

    /// <summary>2026-06-01 PUSH MASIVO AGRESIVO: pushea stock a TODAS las publicaciones con SyncStock=ON,
    /// en modo agresivo (puede pausar/activar segun stock). Mantiene la reserva interna de 1 unidad.
    /// Lanza la ejecucion en background y devuelve inmediato con el conteo encolado. El usuario debe
    /// despues consultar la tabla MeliPushSnapshot_*_post para ver resultados.</summary>
    [HttpPost("items/push-stock-masivo-agresivo")]
    [Microsoft.AspNetCore.Authorization.Authorize(Roles = "admin")]
    public async Task<IActionResult> PushStockMasivoAgresivo([FromServices] IServiceScopeFactory scopeFactory,
        [FromServices] Api.Data.AppDbContext db)
    {
        // Validar master switch
        var master = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == "meli.stock_push.master_enabled");
        if (master == null || !string.Equals(master.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "master_enabled está en false. Activalo primero." });

        // Obtener todas las MLAs con SyncStock=ON, status active/paused, NO Full
        var ids = await (
            from sc in db.MeliItemSyncConfigs
            join mi in db.MeliItems on sc.MeliItemId equals mi.MeliItemId
            where sc.SyncStock
                && (mi.Status == "active" || mi.Status == "paused")
                && (mi.LogisticType == null || mi.LogisticType != "fulfillment")
            select mi.MeliItemId
        ).Distinct().ToListAsync();

        if (ids.Count == 0) return Ok(new { ok = true, encolados = 0, message = "No hay publicaciones con SyncStock=ON" });

        // Background fire-and-forget: usar scope nuevo para evitar DbContext disposed
        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var pushSvc = scope.ServiceProvider.GetRequiredService<MeliStockPushService>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<MeliController>>();
            try
            {
                logger.LogWarning("[PushMasivoAgresivo] Arrancando con {Count} publicaciones", ids.Count);
                // Procesar en chunks de 100 para no saturar memoria/tokens
                int chunkSize = 100;
                int totalOk = 0, totalSkip = 0, totalErr = 0;
                for (int i = 0; i < ids.Count; i += chunkSize)
                {
                    var chunk = ids.Skip(i).Take(chunkSize).ToList();
                    var r = await pushSvc.PushStockForMeliItemsAsync(chunk, CancellationToken.None,
                        conservativeMode: false, safeBulkMode: false);
                    totalOk += r.Ok; totalSkip += r.Skipped; totalErr += r.Errores;
                    logger.LogInformation("[PushMasivoAgresivo] Chunk {From}/{Total}: ok={Ok} skip={Skip} err={Err}",
                        i + chunk.Count, ids.Count, r.Ok, r.Skipped, r.Errores);
                }
                logger.LogWarning("[PushMasivoAgresivo] COMPLETADO. Total ok={Ok} skip={Skip} err={Err}", totalOk, totalSkip, totalErr);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[PushMasivoAgresivo] Falla general");
            }
        });

        return Ok(new { ok = true, encolados = ids.Count, message = "Push lanzado en background. Mirar logs para progreso." });
    }

    /// <summary>Lista publicaciones MeLi que probablemente fueron pausadas por nuestro push automático
    /// erróneo (stock=0 en productos OTROS pusheados hoy). No reactiva nada — solo lista.</summary>
    [HttpGet("items/reactivar-pausadas/candidatos")]
    public async Task<IActionResult> ListarReactivacionCandidatos([FromServices] MeliReactivacionService svc)
    {
        try
        {
            var lista = await svc.ListarCandidatosAsync(HttpContext.RequestAborted);
            return Ok(new { count = lista.Count, items = lista });
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    public class ReactivacionRequest { public List<string>? MeliItemIds { get; set; } public int StockSafeDefault { get; set; } = 1; }

    /// <summary>Reactiva las publicaciones pausadas que detectamos como falsos pausados por push erróneo.
    /// Body opcional: { meliItemIds: ["MLA..."] } — si null, reactiva todos los candidatos.</summary>
    [HttpPost("items/reactivar-pausadas")]
    public async Task<IActionResult> ReactivarPausadas([FromServices] MeliReactivacionService svc,
        [FromBody] ReactivacionRequest? req = null)
    {
        try
        {
            var r = await svc.ReactivarAsync(req?.MeliItemIds, req?.StockSafeDefault ?? 1, HttpContext.RequestAborted);
            return Ok(r);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Audita la presencia de MLAs: compara la lista que devuelve MeLi vs lo que tenemos en DB.
    /// Devuelve { Accounts: [{ Nickname, MeliCount, SystemCount, BothCount, MeliOnly: [], SystemOnly: [] }], TotalMeli, TotalSystem, TotalBoth, ... }.
    /// No modifica nada — solo informa. La importación de faltantes va por items/sync-by-id.</summary>
    [HttpPost("items/audit")]
    public async Task<IActionResult> AuditItems([FromQuery] int? accountId = null)
    {
        try
        {
            var result = await _itemService.AuditAccountsAsync(accountId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("items/sync")]
    public IActionResult SyncItems([FromQuery] string? status = null, [FromQuery] int? accountId = null)
    {
        try
        {
            // Start progress tracking
            var progressId = _syncProgress.StartSync(
                status is not null ? $"Sincronizando {status}" :
                accountId.HasValue ? $"Sincronizando cuenta {accountId}" :
                "Sincronizando todas las publicaciones");

            // Fire and forget - run sync in background, frontend polls progress
            var scopeFactory = _scopeFactory;
            var syncProgress = _syncProgress;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var itemService = scope.ServiceProvider.GetRequiredService<MeliItemService>();
                    await itemService.SyncItemsAsync(status, accountId, progressId);
                }
                catch (Exception ex)
                {
                    syncProgress.Fail(progressId, $"Error: {ex.Message}");
                }
            });

            // Return immediately with progressId
            return Ok(new { TotalSynced = 0, TotalErrors = 0, Errors = new List<string>(), ProgressId = progressId });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // 2026-07-10: trae una familia completa (todos los colores/modalidades, activas y pausadas) por su número.
    [HttpPost("items/sync-family")]
    public IActionResult SyncFamily([FromQuery] string familyId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(familyId))
                return BadRequest(new { error = "Indicá el número de familia." });

            var progressId = _syncProgress.StartSync($"Trayendo familia {familyId}");
            var scopeFactory = _scopeFactory;
            var syncProgress = _syncProgress;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var itemService = scope.ServiceProvider.GetRequiredService<MeliItemService>();
                    await itemService.SyncFamilyAsync(familyId, progressId);
                }
                catch (Exception ex)
                {
                    syncProgress.Fail(progressId, $"Error: {ex.Message}");
                }
            });

            return Ok(new { ProgressId = progressId });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // 2026-07-11: trae TODAS las familias completas (pausadas/inactivas incluidas) de una.
    [HttpPost("items/sync-all-families")]
    public IActionResult SyncAllFamilies()
    {
        try
        {
            var progressId = _syncProgress.StartSync("Trayendo todas las familias completas");
            var scopeFactory = _scopeFactory;
            var syncProgress = _syncProgress;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var itemService = scope.ServiceProvider.GetRequiredService<MeliItemService>();
                    await itemService.SyncAllFamiliesAsync(progressId);
                }
                catch (Exception ex)
                {
                    syncProgress.Fail(progressId, $"Error: {ex.Message}");
                }
            });
            return Ok(new { ProgressId = progressId });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("items/sync/progress")]
    public IActionResult GetSyncProgress([FromQuery] string? id = null)
    {
        var info = id is not null ? _syncProgress.Get(id) : _syncProgress.GetLatest();
        if (info is null) return Ok(new { status = "idle" });
        return Ok(new
        {
            info.Id,
            info.Status,
            info.Description,
            info.CurrentStep,
            info.CurrentAccount,
            info.AccountIndex,
            info.TotalAccounts,
            info.TotalItemsFound,
            info.ItemsSynced,
            info.TotalErrors,
            info.Percentage,
            info.StartedAt,
            info.FinishedAt
        });
    }

    [HttpGet("items/{meliItemId}/promotions")]
    public async Task<IActionResult> GetItemPromotions(string meliItemId)
    {
        try
        {
            var result = await _itemService.GetItemPromotionsAsync(meliItemId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("items/{meliItemId}")]
    public async Task<IActionResult> UpdateItem(string meliItemId, [FromBody] UpdateMeliItemRequest request)
    {
        try
        {
            var result = await _itemService.UpdateItemAsync(meliItemId, request);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("items/{meliItemId}/costs")]
    public async Task<IActionResult> GetItemCosts(string meliItemId)
    {
        try
        {
            var result = await _itemService.GetListingCostsAsync(meliItemId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("items/{meliItemId}/details")]
    public async Task<IActionResult> GetItemDetails(string meliItemId)
    {
        var details = await _itemService.GetItemDetailsAsync(meliItemId);
        if (details is null) return NotFound();
        return Ok(details);
    }

    /// <summary>2026-06-19: refresca el sale_fee real (lo que MeLi cobra de comision)
    /// para una publicacion. Llama a /sites/MLA/listing_prices y guarda en MeliItems.</summary>
    [HttpPost("items/{meliItemId}/refresh-salefee")]
    public async Task<IActionResult> RefreshSaleFee(string meliItemId)
    {
        var ok = await _itemService.RefreshSaleFeeAsync(meliItemId);
        if (!ok) return BadRequest(new { error = "No se pudo refrescar el sale_fee. Verificá que el item exista y la cuenta MeLi tenga token valido." });
        return Ok(new { ok = true });
    }

    /// <summary>2026-06-19: refresca sale_fee de TODAS las publicaciones activas. Dispara
    /// en background y devuelve inmediatamente. Llama 1 API por item — para 5000 items
    /// puede tardar 10-15 min. Reportar progreso por logs.</summary>
    [HttpPost("items/refresh-salefee-bulk")]
    public IActionResult RefreshSaleFeeBulk()
    {
        var scope = HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();
        _ = Task.Run(async () =>
        {
            using var s = scope.CreateScope();
            var db = s.ServiceProvider.GetRequiredService<Api.Data.AppDbContext>();
            var svc = s.ServiceProvider.GetRequiredService<Api.Services.MeliItemService>();
            var ids = await db.Set<Api.Models.MeliItem>()
                .Where(i => i.Status == "active")
                .Select(i => i.MeliItemId!)
                .ToListAsync();
            int ok = 0, fail = 0;
            foreach (var id in ids)
            {
                try { if (await svc.RefreshSaleFeeAsync(id)) ok++; else fail++; }
                catch { fail++; }
                if ((ok + fail) % 50 == 0)
                    Console.WriteLine($"[sale-fee-bulk] progreso {ok + fail}/{ids.Count} (ok={ok}, fail={fail})");
            }
            Console.WriteLine($"[sale-fee-bulk] FIN ok={ok} fail={fail} total={ids.Count}");
        });
        return Ok(new { ok = true, msg = "Refresco masivo disparado en background. Ver logs del API para progreso." });
    }

    // 2026-07-02: refresh de comisiones SELECTIVO. Recibe una lista de IDs y refresca solo esas
    // (síncrono — para pocas publis, el usuario espera la respuesta). Útil para probar el fix
    // del financing por modalidad sin tener que esperar 3h del bulk masivo.
    public record RefreshSaleFeeSelectedRequest(List<int> ItemIds);
    public record RefreshSaleFeeSelectedResult(int Ok, int Fail, List<string> Errores);

    [HttpPost("items/refresh-salefee-selected")]
    public async Task<IActionResult> RefreshSaleFeeSelected(
        [FromBody] RefreshSaleFeeSelectedRequest req,
        [FromServices] Api.Data.AppDbContext db)
    {
        if (req.ItemIds is null || req.ItemIds.Count == 0)
            return BadRequest(new { error = "No hay publicaciones seleccionadas." });
        // Máximo 200 por request para no colgar el request HTTP.
        if (req.ItemIds.Count > 200)
            return BadRequest(new { error = "Máximo 200 publicaciones por request." });

        var items = await db.MeliItems.Where(i => req.ItemIds.Contains(i.Id)).Select(i => i.MeliItemId).ToListAsync();
        int ok = 0, fail = 0;
        var errores = new List<string>();
        foreach (var mla in items)
        {
            try
            {
                if (await _itemService.RefreshSaleFeeAsync(mla)) ok++;
                else { fail++; errores.Add($"{mla}: sin resultado"); }
            }
            catch (Exception ex) { fail++; errores.Add($"{mla}: {ex.Message}"); }
        }
        return Ok(new RefreshSaleFeeSelectedResult(ok, fail, errores.Take(10).ToList()));
    }

        [HttpPut("items/{meliItemId}/link")]
    public async Task<IActionResult> LinkItemToProduct(string meliItemId, [FromBody] LinkItemToProductRequest request)
    {
        try
        {
            var result = await _itemService.LinkToProductAsync(meliItemId, request.ProductId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("items/{meliItemId}/link")]
    public async Task<IActionResult> UnlinkItemProduct(string meliItemId)
    {
        try
        {
            var result = await _itemService.UnlinkProductAsync(meliItemId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    public record LinkItemToComboRequest(int ComboId);

    [HttpPut("items/{meliItemId}/link-combo")]
    public async Task<IActionResult> LinkItemToCombo(string meliItemId, [FromBody] LinkItemToComboRequest request)
    {
        try
        {
            var result = await _itemService.LinkToComboAsync(meliItemId, request.ComboId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("items/bulk-delete")]
    public async Task<IActionResult> BulkDeleteItems([FromBody] BulkDeleteRequest request)
    {
        if (request.Ids == null || !request.Ids.Any())
            return BadRequest(new { error = "No se proporcionaron IDs" });

        var deleted = await _itemService.DeleteItemsAsync(request.Ids);
        return Ok(new { deleted });
    }

    /// <summary>Pushea SOLO STOCK (sin tocar precio) a las publicaciones MeLi linkeadas a un
    /// CafeProducto. Util para forzar sincronizar despues de un ajuste manual.</summary>
    [HttpPost("stock-push/{cafeProductoId:int}")]
    public async Task<IActionResult> PushStockForProducto(int cafeProductoId,
        [FromServices] MeliStockPushService pushSvc)
    {
        try
        {
            var r = await pushSvc.PushStockForProductoAsync(cafeProductoId, HttpContext.RequestAborted);
            return Ok(new { procesadas = r.Procesadas, ok = r.Ok, skipped = r.Skipped, errores = r.Errores, mensajes = r.Mensajes });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ============================================================
    // PANTALLA "SKUs Mercado Libre" (árbol genealógico)
    // ============================================================

    public record SkuMeliRow(
        string Sku,
        int CantPubsActivas,
        int CantPubsPausadas,
        int? StockMeliMax,                  // max(AvailableQuantity) entre las pubs
        string Estado,                       // "ok_combo" / "ok_producto" / "sin_link" / "producto_suelto" / "sin_combo"
        int? ComboId,                        // Cafe_Combos.Id si existe
        string? ComboNombre,
        int StockArmableCombo,               // stock que se puede armar segun min(componentes/cantidad)
        List<SkuMeliComponenteRow> Componentes,
        int? ProductoSueltoId,
        string? ProductoSueltoNombre,
        int? ProductoSueltoStock
    );

    public record SkuMeliComponenteRow(int ProductoId, string Sku, string Nombre, int Cantidad, int StockDisponible, int ComponenteId);
    public record SkuMeliPubRow(string MeliItemId, string Status, int AvailableQuantity, string? Permalink);

    /// <summary>Devuelve el listado de SKUs únicos de MeliItems con su árbol genealógico:
    /// publicaciones que lo usan, combo del sistema (si existe), componentes, stock armable.
    /// Soporta filtros: estado (ok_combo/ok_producto/sin_link/producto_suelto/sin_combo) + búsqueda por SKU.
    /// Acepta tambien "ok" como alias retrocompatible (combina ok_combo + ok_producto).</summary>
    [HttpGet("skus-meli")]
    public async Task<IActionResult> GetSkusMeli(
        [FromServices] Api.Data.AppDbContext db,
        [FromQuery] string? buscar = null,
        [FromQuery] string? estado = null,
        [FromQuery] string? armableRango = null,  // "0", "1-4", "5-9", "10+"
        [FromQuery] string ordenarPor = "armable_desc",  // armable_desc | armable_asc | sku
        [FromQuery] int limit = 1000)
    {
        // 1) SKUs únicos de MeliItems (excluye cerradas, incluye paused y active)
        var skusBaseQ = db.MeliItems.AsNoTracking()
            .Where(mi => mi.Sku != null && mi.Sku != "" && (mi.Status == "active" || mi.Status == "paused"));
        if (!string.IsNullOrWhiteSpace(buscar))
        {
            // 2026-06-03: busca por SKU O por NOMBRE (Title de la publicacion MeLi).
            var t = buscar.Trim().ToUpper();
            skusBaseQ = skusBaseQ.Where(mi => mi.Sku!.ToUpper().Contains(t) || (mi.Title != null && mi.Title.ToUpper().Contains(t)));
        }

        var skusAgg = await skusBaseQ
            .GroupBy(mi => mi.Sku)
            .Select(g => new {
                Sku = g.Key!,
                CantActivas = g.Sum(x => x.Status == "active" ? 1 : 0),
                CantPausadas = g.Sum(x => x.Status == "paused" ? 1 : 0),
                StockMeliMax = g.Max(x => (int?)x.AvailableQuantity),
                HayLinkeado = g.Sum(x => x.CafeComboId != null || x.CafeProductoId != null ? 1 : 0)
            })
            .OrderBy(x => x.Sku)
            .Take(limit + 200)  // tomamos un poquito mas porque despues filtramos
            .ToListAsync();

        var skuList = skusAgg.Select(x => x.Sku).ToList();

        // 2) Combos que matchean por SKU
        var combos = await db.CafeCombos.AsNoTracking()
            .Where(c => skuList.Contains(c.Sku))
            .Select(c => new { c.Id, c.Sku, c.Nombre })
            .ToListAsync();
        var comboBySku = combos.ToDictionary(c => c.Sku, c => c);
        var comboIds = combos.Select(c => c.Id).ToList();

        // 3) Componentes de esos combos
        var compsRaw = await db.CafeComboItems.AsNoTracking()
            .Where(ci => comboIds.Contains(ci.ComboId))
            .Join(db.CafeProductos.AsNoTracking(),
                ci => ci.ProductoId, p => p.Id,
                (ci, p) => new {
                    ci.ComboId, ci.ProductoId, p.Sku, p.Nombre, ci.Cantidad, p.StockUnidades
                })
            .ToListAsync();
        var compsByCombo = compsRaw.GroupBy(c => c.ComboId).ToDictionary(g => g.Key, g => g.ToList());

        // 4) Productos sueltos (cuando el SKU del MeLi corresponde a un Cafe_Productos, NO a Cafe_Combos)
        var productos = await db.CafeProductos.AsNoTracking()
            .Where(p => skuList.Contains(p.Sku!))
            .Select(p => new { p.Id, p.Sku, p.Nombre, p.StockUnidades })
            .ToListAsync();
        var prodBySku = productos.ToDictionary(p => p.Sku!, p => p);

        // 4b) MeliItemComponentes asociados a estos SKUs (para sacar el ComponenteId que la UI necesita para editar).
        // Lookup: (Sku, CafeProductoId) → primer ComponenteId disponible (cualquiera de las MLAs sirve, son equivalentes)
        var compIdsRaw = await db.MeliItemComponentes.AsNoTracking()
            .Join(db.MeliItems.AsNoTracking(), c => c.MeliItemId, mi => mi.MeliItemId,
                (c, mi) => new { CompId = c.Id, mi.Sku, c.CafeProductoId })
            .Where(x => x.Sku != null && skuList.Contains(x.Sku!))
            .ToListAsync();
        var compIdBySkuProd = compIdsRaw
            .GroupBy(x => new { x.Sku, x.CafeProductoId })
            .ToDictionary(g => g.Key, g => g.First().CompId);

        var result = new List<SkuMeliRow>();
        foreach (var s in skusAgg)
        {
            string estadoCalc;
            int? comboId = null; string? comboNombre = null;
            int stockArmable = 0;
            List<SkuMeliComponenteRow> componentes = new();
            int? prodId = null; string? prodNombre = null; int? prodStock = null;

            if (comboBySku.TryGetValue(s.Sku, out var cb))
            {
                comboId = cb.Id; comboNombre = cb.Nombre;
                if (compsByCombo.TryGetValue(cb.Id, out var comps))
                {
                    componentes = comps.Select(c =>
                    {
                        var lookupKey = new { Sku = (string?)s.Sku, CafeProductoId = c.ProductoId };
                        var compId = compIdBySkuProd.TryGetValue(lookupKey, out var cid) ? cid : 0;
                        return new SkuMeliComponenteRow(c.ProductoId, c.Sku ?? "", c.Nombre, c.Cantidad, c.StockUnidades, compId);
                    }).ToList();
                    // stock armable = MIN(stock_componente / cantidad) entre todos los componentes
                    if (componentes.Count > 0)
                    {
                        stockArmable = componentes
                            .Where(c => c.Cantidad > 0)
                            .Select(c => (int)Math.Floor(c.StockDisponible / (double)c.Cantidad))
                            .DefaultIfEmpty(0).Min();
                    }
                }
                estadoCalc = s.HayLinkeado > 0 ? "ok_combo" : "sin_link";
            }
            else if (prodBySku.TryGetValue(s.Sku, out var prd))
            {
                prodId = prd.Id; prodNombre = prd.Nombre; prodStock = prd.StockUnidades;
                stockArmable = prd.StockUnidades;
                estadoCalc = s.HayLinkeado > 0 ? "ok_producto" : "producto_suelto";
            }
            else
            {
                estadoCalc = "sin_combo";
            }

            // Filtro por estado (acepta "ok" como alias para ok_combo+ok_producto retrocompat)
            if (!string.IsNullOrEmpty(estado))
            {
                bool match = estado == estadoCalc || (estado == "ok" && (estadoCalc == "ok_combo" || estadoCalc == "ok_producto"));
                if (!match) continue;
            }

            // Filtro por rango de combos armables
            int stockEfectivo = componentes.Count > 0 ? stockArmable : (prodStock ?? 0);
            if (!string.IsNullOrEmpty(armableRango))
            {
                bool pasa = armableRango switch
                {
                    "0" => stockEfectivo == 0,
                    "1-4" => stockEfectivo >= 1 && stockEfectivo <= 4,
                    "5-9" => stockEfectivo >= 5 && stockEfectivo <= 9,
                    "10+" => stockEfectivo >= 10,
                    _ => true
                };
                if (!pasa) continue;
            }

            result.Add(new SkuMeliRow(
                Sku: s.Sku,
                CantPubsActivas: s.CantActivas,
                CantPubsPausadas: s.CantPausadas,
                StockMeliMax: s.StockMeliMax,
                Estado: estadoCalc,
                ComboId: comboId, ComboNombre: comboNombre,
                StockArmableCombo: stockArmable,
                Componentes: componentes,
                ProductoSueltoId: prodId, ProductoSueltoNombre: prodNombre, ProductoSueltoStock: prodStock
            ));
        }

        // Ordenar
        result = ordenarPor switch
        {
            "armable_asc" => result.OrderBy(r => r.Componentes.Count > 0 ? r.StockArmableCombo : (r.ProductoSueltoStock ?? 0)).ThenBy(r => r.Sku).ToList(),
            "armable_desc" => result.OrderByDescending(r => r.Componentes.Count > 0 ? r.StockArmableCombo : (r.ProductoSueltoStock ?? 0)).ThenBy(r => r.Sku).ToList(),
            "sku" => result.OrderBy(r => r.Sku).ToList(),
            _ => result.OrderByDescending(r => r.Componentes.Count > 0 ? r.StockArmableCombo : (r.ProductoSueltoStock ?? 0)).ThenBy(r => r.Sku).ToList()
        };
        if (result.Count > limit) result = result.Take(limit).ToList();

        // Stats GLOBALES (sobre TODOS los SKUs sin paginar, no solo los mostrados)
        // Recorremos skusAgg entero (que ya esta limitado a limit+200) si el filtro de buscar lo achico,
        // pero si no, hacemos un query liviano por separado.
        var statsBaseQ = db.MeliItems.AsNoTracking()
            .Where(mi => mi.Sku != null && mi.Sku != "" && (mi.Status == "active" || mi.Status == "paused"));
        if (!string.IsNullOrWhiteSpace(buscar))
        {
            var t = buscar.Trim().ToUpper();
            statsBaseQ = statsBaseQ.Where(mi => mi.Sku!.ToUpper().Contains(t) || (mi.Title != null && mi.Title.ToUpper().Contains(t)));
        }
        var statsRaw = await statsBaseQ
            .GroupBy(mi => mi.Sku)
            .Select(g => new {
                Sku = g.Key!,
                HayLink = g.Sum(x => x.CafeComboId != null || x.CafeProductoId != null ? 1 : 0)
            }).ToListAsync();

        var skusAllList = statsRaw.Select(x => x.Sku).ToList();
        var combosAll = await db.CafeCombos.AsNoTracking()
            .Where(c => skusAllList.Contains(c.Sku!))
            .Select(c => c.Sku!).ToListAsync();
        var prodsAll = await db.CafeProductos.AsNoTracking()
            .Where(p => skusAllList.Contains(p.Sku!))
            .Select(p => p.Sku!).ToListAsync();
        var combosAllSet = new HashSet<string>(combosAll);
        var prodsAllSet = new HashSet<string>(prodsAll);

        int statsOkCombo = 0, statsOkProd = 0, statsSinLink = 0, statsSinCombo = 0, statsProdSueltoSinLink = 0;
        foreach (var s in statsRaw)
        {
            bool tieneCombo = combosAllSet.Contains(s.Sku);
            bool tieneProd  = prodsAllSet.Contains(s.Sku) && !tieneCombo;
            string est;
            if (tieneCombo) est = s.HayLink > 0 ? "ok_combo" : "sin_link";
            else if (tieneProd) est = s.HayLink > 0 ? "ok_producto" : "producto_suelto";
            else est = "sin_combo";
            if (est == "ok_combo") statsOkCombo++;
            else if (est == "ok_producto") statsOkProd++;
            else if (est == "sin_link") statsSinLink++;
            else if (est == "sin_combo") statsSinCombo++;
            else statsProdSueltoSinLink++;
        }

        return Ok(new {
            total = statsRaw.Count,
            mostrados = result.Count,
            stats = new {
                ok_combo = statsOkCombo,
                ok_producto = statsOkProd,
                sin_link = statsSinLink,
                producto_suelto = statsProdSueltoSinLink,
                sin_combo = statsSinCombo,
                ok = statsOkCombo + statsOkProd  // retrocompat
            },
            items = result
        });
    }

    /// <summary>Exporta CSV con SKU MeLi + composicion del sistema, para cotejar contra Contabilium.</summary>
    [HttpGet("skus-meli/export-csv")]
    public async Task<IActionResult> ExportSkusMeliCsv([FromServices] Api.Data.AppDbContext db)
    {
        // 1. SKUs unicos de MeliItems activos/pausados
        var skusAgg = await db.MeliItems.AsNoTracking()
            .Where(mi => mi.Sku != null && mi.Sku != "" && (mi.Status == "active" || mi.Status == "paused"))
            .GroupBy(mi => mi.Sku)
            .Select(g => new {
                Sku = g.Key!,
                CantActivas = g.Sum(x => x.Status == "active" ? 1 : 0),
                CantPausadas = g.Sum(x => x.Status == "paused" ? 1 : 0),
                StockMeliMax = g.Max(x => (int?)x.AvailableQuantity),
                MlasSample = g.OrderByDescending(x => x.AvailableQuantity).Select(x => x.MeliItemId).FirstOrDefault()
            })
            .OrderBy(x => x.Sku)
            .ToListAsync();

        var skuList = skusAgg.Select(x => x.Sku).ToList();

        // 2. Combos por SKU
        var combos = await db.CafeCombos.AsNoTracking()
            .Where(c => skuList.Contains(c.Sku))
            .Select(c => new { c.Id, c.Sku, c.Nombre })
            .ToListAsync();
        var comboBySku = combos.ToDictionary(c => c.Sku, c => c);
        var comboIds = combos.Select(c => c.Id).ToList();

        // 3. Componentes de los combos del sistema (Cafe_ComboItems) — fallback solo si no hay reglas MeLi
        var compsCombo = await db.CafeComboItems.AsNoTracking()
            .Where(ci => comboIds.Contains(ci.ComboId))
            .Join(db.CafeProductos.AsNoTracking(),
                ci => ci.ProductoId, p => p.Id,
                (ci, p) => new { ci.ComboId, p.Sku, p.Nombre, ci.Cantidad, p.StockUnidades, p.StockGramos })
            .ToListAsync();
        var compsByCombo = compsCombo.GroupBy(c => c.ComboId).ToDictionary(g => g.Key, g => g.ToList());

        // 4. Productos sueltos
        var productos = await db.CafeProductos.AsNoTracking()
            .Where(p => skuList.Contains(p.Sku!))
            .Select(p => new { p.Id, p.Sku, p.Nombre, p.StockUnidades, p.StockGramos })
            .ToListAsync();
        var prodBySku = productos.ToDictionary(p => p.Sku!, p => p);

        // 5. MeliItemComponentes (LA VERDAD del descuento al vender)
        // IMPORTANTE: filtrar por VariationId del MeliItem para no mezclar variantes hermanas
        var meliCompsRaw = await db.MeliItemComponentes.AsNoTracking()
            .Join(db.MeliItems.AsNoTracking(),
                mc => mc.MeliItemId, mi => mi.MeliItemId,
                (mc, mi) => new { mi.Sku, mi.VariationId, mc.CafeProductoId, mc.Cantidad, mc.Formato, mc.MeliVariationId })
            .Where(x => skuList.Contains(x.Sku!))
            .ToListAsync();
        // Filtrar: si el MeliItem tiene VariationId, solo aceptar componentes con MeliVariationId == ese, o sin MeliVariationId
        // Si el MeliItem NO tiene VariationId, solo aceptar componentes sin MeliVariationId
        meliCompsRaw = meliCompsRaw.Where(x =>
            (x.VariationId != null && (x.MeliVariationId == x.VariationId || string.IsNullOrEmpty(x.MeliVariationId))) ||
            (x.VariationId == null && string.IsNullOrEmpty(x.MeliVariationId))
        ).ToList();
        // Join con productos para sacar SKU + Nombre + StockGramos del componente
        var prodById = await db.CafeProductos.AsNoTracking()
            .Select(p => new { p.Id, p.Sku, p.Nombre, p.StockUnidades, p.StockGramos })
            .ToListAsync();
        var prodByIdDict = prodById.ToDictionary(p => p.Id, p => p);
        // Agrupar por SKU MeLi, deduplicar por (CafeProductoId,Formato) tomando max cantidad
        var meliCompsBySku = meliCompsRaw
            .GroupBy(x => x.Sku!)
            .ToDictionary(g => g.Key, g => g
                .GroupBy(x => new { x.CafeProductoId, x.Formato })
                .Select(gg => new {
                    CafeProductoId = gg.Key.CafeProductoId,
                    Formato = gg.Key.Formato,
                    Cantidad = gg.Max(z => z.Cantidad)
                }).ToList());

        // 6. Armar CSV
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("SKU_MELI;TIPO;NOMBRE_SISTEMA;COMPOSICION;CANT_COMPONENTES;STOCK_ARMABLE_SISTEMA;STOCK_UNIDADES_SISTEMA;STOCK_GRAMOS_SISTEMA;STOCK_MELI_MAX;PUBS_ACTIVAS;PUBS_PAUSADAS;MLA_EJEMPLO;FUENTE_COMPOSICION");

        // Mostrar formato (1KG = 1 kg ⇒ omitir, MEDIO = 0.5kg, CUARTO = 0.25kg)
        static string FormatComp(decimal cant, string sku, string? formato)
        {
            var f = (formato ?? "").ToUpperInvariant();
            string suf = f switch {
                "MEDIO" => " (1/2kg)",
                "CUARTO" => " (1/4kg)",
                "1KG" => "",  // 1kg es la unidad por defecto en cafe, no aclarar
                _ => ""
            };
            return (cant > 1 || cant != Math.Floor(cant) ? $"{(cant == Math.Floor(cant) ? cant.ToString("0") : cant.ToString("0.##"))}x " : "") + sku + suf;
        }

        foreach (var s in skusAgg)
        {
            string tipo, nombre = "", composicion = "", stockArmableStr = "", stockUnidadesStr = "", stockGramosStr = "", fuente = "";
            int cantComp = 0;

            // 1ra prioridad: MeliItemComponentes (la regla REAL que aplica el runtime)
            if (meliCompsBySku.TryGetValue(s.Sku, out var meliComps) && meliComps.Count > 0)
            {
                fuente = "MeliItemComponentes";
                var parts = new List<string>();
                int? stockArm = null;
                decimal? totalGr = 0;
                foreach (var mc in meliComps)
                {
                    if (prodByIdDict.TryGetValue(mc.CafeProductoId, out var pr))
                    {
                        parts.Add(FormatComp(mc.Cantidad, pr.Sku ?? "?", mc.Formato));
                        // Calcular stock armable
                        var gramosPorUnidad = (mc.Formato ?? "").ToUpperInvariant() switch {
                            "1KG" => 1000m, "MEDIO" => 500m, "CUARTO" => 250m, _ => 1m
                        };
                        var necesitaG = mc.Cantidad * gramosPorUnidad;
                        if (necesitaG > 0 && pr.StockGramos > 0)
                        {
                            var puede = (int)Math.Floor(pr.StockGramos / necesitaG);
                            stockArm = stockArm.HasValue ? Math.Min(stockArm.Value, puede) : puede;
                        }
                        else if (necesitaG > 0 && pr.StockUnidades > 0)
                        {
                            // componente en unidades (raro pero posible)
                            var puede = (int)Math.Floor(pr.StockUnidades / mc.Cantidad);
                            stockArm = stockArm.HasValue ? Math.Min(stockArm.Value, puede) : puede;
                        }
                        else
                        {
                            stockArm = 0;
                        }
                    }
                }
                cantComp = meliComps.Count;
                composicion = string.Join(" + ", parts);
                if (stockArm.HasValue) stockArmableStr = stockArm.Value.ToString();
                // tipo según hay combo o producto en sistema
                if (comboBySku.TryGetValue(s.Sku, out var cb1)) { tipo = "COMBO"; nombre = cb1.Nombre ?? ""; }
                else if (prodBySku.TryGetValue(s.Sku, out var prd1)) { tipo = "PRODUCTO"; nombre = prd1.Nombre ?? ""; stockUnidadesStr = prd1.StockUnidades.ToString(); stockGramosStr = prd1.StockGramos > 0 ? prd1.StockGramos.ToString("F0") : ""; }
                else { tipo = "MELI_COMP_SOLO"; nombre = ""; }
            }
            // 2da: combo sistema con Cafe_ComboItems
            else if (comboBySku.TryGetValue(s.Sku, out var cb) && compsByCombo.TryGetValue(cb.Id, out var comps) && comps.Count > 0)
            {
                fuente = "Cafe_ComboItems";
                tipo = "COMBO";
                nombre = cb.Nombre ?? "";
                cantComp = comps.Count;
                composicion = string.Join(" + ", comps.Select(c => c.Cantidad > 1 ? $"{c.Cantidad}x {c.Sku}" : c.Sku ?? ""));
                var armable = comps.Where(c => c.Cantidad > 0)
                    .Select(c => (int)Math.Floor(c.StockUnidades / (double)c.Cantidad))
                    .DefaultIfEmpty(0).Min();
                stockArmableStr = armable.ToString();
            }
            // 3ra: producto suelto
            else if (prodBySku.TryGetValue(s.Sku, out var prd))
            {
                fuente = "Cafe_Productos";
                tipo = "PRODUCTO";
                nombre = prd.Nombre ?? "";
                composicion = prd.Sku ?? "";
                cantComp = 1;
                // Si el producto tiene stock en gramos, mostrar armable como kg
                if (prd.StockGramos > 0)
                {
                    stockArmableStr = ((int)(prd.StockGramos / 1000m)).ToString() + "kg";
                }
                else
                {
                    stockArmableStr = prd.StockUnidades.ToString();
                }
                stockUnidadesStr = prd.StockUnidades.ToString();
                stockGramosStr = prd.StockGramos > 0 ? prd.StockGramos.ToString("F0") : "";
            }
            // 4to: combo sin componentes (rota)
            else if (comboBySku.TryGetValue(s.Sku, out var cb2))
            {
                fuente = "Combo_Vacio";
                tipo = "COMBO";
                nombre = cb2.Nombre ?? "";
                composicion = "";
            }
            else
            {
                fuente = "Nada";
                tipo = "SIN_SISTEMA";
                nombre = "";
                composicion = "";
            }

            // Escape CSV (separador ;, escapar " y ;)
            string Esc(string? v) => v == null ? "" : (v.Contains(';') || v.Contains('"') || v.Contains('\n'))
                ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

            sb.Append(Esc(s.Sku)).Append(';');
            sb.Append(tipo).Append(';');
            sb.Append(Esc(nombre)).Append(';');
            sb.Append(Esc(composicion)).Append(';');
            sb.Append(cantComp).Append(';');
            sb.Append(stockArmableStr).Append(';');
            sb.Append(stockUnidadesStr).Append(';');
            sb.Append(stockGramosStr).Append(';');
            sb.Append(s.StockMeliMax?.ToString() ?? "").Append(';');
            sb.Append(s.CantActivas).Append(';');
            sb.Append(s.CantPausadas).Append(';');
            sb.Append(s.MlasSample ?? "").Append(';');
            sb.Append(fuente);
            sb.AppendLine();
        }

        var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        var fname = $"skus_meli_linkeo_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";
        return File(bytes, "text/csv; charset=utf-8", fname);
    }

    /// <summary>Devuelve las publicaciones MeLi de un SKU puntual.</summary>
    [HttpGet("skus-meli/{sku}/pubs")]
    public async Task<IActionResult> GetSkuMeliPubs(string sku, [FromServices] Api.Data.AppDbContext db)
    {
        var pubs = await db.MeliItems.AsNoTracking()
            .Where(mi => mi.Sku == sku && (mi.Status == "active" || mi.Status == "paused"))
            .OrderByDescending(mi => mi.AvailableQuantity)
            .Select(mi => new SkuMeliPubRow(mi.MeliItemId, mi.Status ?? "", mi.AvailableQuantity, mi.Permalink))
            .ToListAsync();
        return Ok(pubs);
    }

    /// <summary>Linkea automáticamente todas las pubs MeLi de un SKU al combo del sistema con
    /// el mismo SKU. Setea MeliItems.CafeComboId. Idempotente.</summary>
    [HttpPost("skus-meli/{sku}/linkear")]
    public async Task<IActionResult> LinkearSkuMeli(string sku, [FromServices] Api.Data.AppDbContext db)
    {
        var combo = await db.CafeCombos.AsNoTracking().FirstOrDefaultAsync(c => c.Sku == sku);
        if (combo is null) return BadRequest(new { error = $"No existe combo con SKU '{sku}' en el sistema" });

        var pubs = await db.MeliItems
            .Where(mi => mi.Sku == sku && mi.CafeComboId == null && (mi.Status == "active" || mi.Status == "paused"))
            .ToListAsync();
        foreach (var p in pubs) p.CafeComboId = combo.Id;
        await db.SaveChangesAsync();

        return Ok(new { ok = true, comboId = combo.Id, pubsLinkeadas = pubs.Count });
    }

    public record CrearProductoDesdeSkuRequest(string? Nombre, int? OemId, decimal? MultiplicadorOem);
    public record CrearProductoDesdeSkuResult(int ProductoId, string Sku, string Nombre, int PubsLinkeadas);

    /// <summary>2026-05-30 — Pieza 2: crea un Cafe_Producto desde un SKU MeLi y auto-linkea
    /// todas las publicaciones MeLi con ese SKU. Usado por el botón "+ Crear producto" en
    /// /cafe/skus-meli. Si el producto ya existe en sistema, devuelve error.
    ///
    /// Argumentos opcionales: Nombre (default = título de la publicación), OemId (default null),
    /// MultiplicadorOem (default null = ×1). Si tiene OemId, el precio sistema viene del OEM.</summary>
    [HttpPost("skus-meli/{sku}/crear-producto")]
    public async Task<IActionResult> CrearProductoDesdeSku(string sku,
        [FromBody] CrearProductoDesdeSkuRequest req,
        [FromServices] Api.Data.AppDbContext db)
    {
        if (string.IsNullOrWhiteSpace(sku))
            return BadRequest(new { error = "SKU vacío" });
        sku = sku.Trim().ToUpperInvariant();

        // 1) Validar que no exista ya
        var existing = await db.CafeProductos.FirstOrDefaultAsync(p => p.Sku == sku);
        if (existing != null)
            return BadRequest(new { error = $"Ya existe un producto con SKU '{sku}' (Id {existing.Id} — '{existing.Nombre}')" });

        // 2) Buscar título representativo desde MeLi (la publicación más reciente)
        var meliRepresentativo = await db.MeliItems.AsNoTracking()
            .Where(mi => mi.Sku == sku && (mi.Status == "active" || mi.Status == "paused"))
            .OrderByDescending(mi => mi.Id)
            .Select(mi => new { mi.Title })
            .FirstOrDefaultAsync();

        var nombre = string.IsNullOrWhiteSpace(req?.Nombre)
            ? (meliRepresentativo?.Title ?? sku)
            : req!.Nombre!.Trim();
        if (nombre.Length > 200) nombre = nombre.Substring(0, 200);

        // 3) Validar OEM si vino + traer datos para heredar (Marca, IvaPct)
        Api.Models.CafeOem? oem = null;
        if (req?.OemId.HasValue == true)
        {
            oem = await db.CafeOems.FirstOrDefaultAsync(o => o.Id == req.OemId.Value);
            if (oem is null) return BadRequest(new { error = $"OEM Id {req.OemId.Value} no existe" });
        }

        // 4) Crear producto — si tiene OEM, heredar Marca + IvaPct del OEM
        var producto = new Api.Models.CafeProducto
        {
            Sku = sku,
            Nombre = nombre,
            Categoria = "OTROS",
            IsActive = true,
            IsVisibleEnVentas = true,
            IvaPct = oem?.IvaPct ?? 21m,
            // 2026-06-01: heredar marca del OEM si está cargada
            MarcaId = oem?.MarcaId,
            Marca = oem?.Marca,
            OemId = req?.OemId,
            MultiplicadorOem = req?.MultiplicadorOem,
            CreatedAt = DateTime.UtcNow,
        };
        db.CafeProductos.Add(producto);
        await db.SaveChangesAsync();

        // 5) Auto-link: todas las MeliItems con este SKU activas/paused y sin CafeProductoId previo
        var pubs = await db.MeliItems
            .Where(mi => mi.Sku == sku
                && (mi.Status == "active" || mi.Status == "paused")
                && mi.CafeProductoId == null)
            .ToListAsync();
        foreach (var p in pubs) p.CafeProductoId = producto.Id;
        if (pubs.Count > 0) await db.SaveChangesAsync();

        return Ok(new CrearProductoDesdeSkuResult(producto.Id, producto.Sku!, producto.Nombre, pubs.Count));
    }

    /// <summary>Devuelve el valor global de meli.stock_push.reserva_interna (default 1).
    /// La UI lo muestra en la ficha producto para que el usuario sepa qué valor se aplica
    /// cuando el campo StockMinimoMeLi está vacío.</summary>
    [HttpGet("stock-reserva-global")]
    public async Task<IActionResult> GetStockReservaGlobal([FromServices] Api.Data.AppDbContext db)
    {
        var s = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == "meli.stock_push.reserva_interna");
        int valor = 1;
        if (s != null && int.TryParse(s.Value, out var v) && v >= 0) valor = v;
        return Ok(new { reservaGlobal = valor });
    }

    /// <summary>Detalle completo de stock de UN producto para el panel de la ficha:
    /// stock propio (9 de Abril), Full MeLi, total real, reserva aplicada y publicado en MeLi.
    /// 2026-06-01: extendido para productos "shell" linkeados a publicaciones por componentes
    /// (caso Pieza 2 + 3: el producto tiene OEM/precio pero el stock vive en sus componentes).</summary>
    [HttpGet("producto-stock-detail/{cafeProductoId:int}")]
    public async Task<IActionResult> GetProductoStockDetail(int cafeProductoId, [FromServices] Api.Data.AppDbContext db)
    {
        var prod = await db.CafeProductos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == cafeProductoId);
        if (prod is null) return NotFound(new { error = "Producto no encontrado" });

        int stockSistema = prod.StockUnidades;

        // Full
        var depFullId = await db.CafeDepositos.AsNoTracking()
            .Where(d => d.Nombre == "Full MeLi").Select(d => (int?)d.Id).FirstOrDefaultAsync();
        int stockFull = 0;
        if (depFullId.HasValue)
        {
            var spdFull = await db.CafeStockPorDeposito.AsNoTracking()
                .FirstOrDefaultAsync(s => s.ProductoId == cafeProductoId && s.DepositoId == depFullId.Value);
            stockFull = spdFull?.StockUnidades ?? 0;
        }

        // Reserva (regla 2026-05-25, opción A): vacío o 0 = sin reserva. N > 0 = reservar N.
        int reservaAplicada = prod.StockMinimoMeLi ?? 0;
        string reservaSource = reservaAplicada > 0 ? "producto" : "sin_reserva";

        // 2026-06-01: detectar si es producto "shell" (linkeado a MeliItems via CafeProductoId).
        // Si esa publicación tiene componentes (combo), el stock REAL armable viene de los componentes.
        // Calculamos "stockArmable" (cuántos cestos armables hay) + "publicadoMeLi" (lo que MeLi muestra).
        int? stockArmable = null;
        string? armableSource = null;
        int publicadoMeLi;

        var linkedMeliItems = await db.MeliItems.AsNoTracking()
            .Where(mi => mi.CafeProductoId == cafeProductoId && (mi.Status == "active" || mi.Status == "paused"))
            .Select(mi => new { mi.MeliItemId, mi.AvailableQuantity })
            .ToListAsync();

        if (linkedMeliItems.Count > 0)
        {
            // "Publicado en MeLi" = lo que MeLi realmente muestra (max entre publicaciones linkeadas).
            publicadoMeLi = linkedMeliItems.Max(mi => mi.AvailableQuantity);

            // Calcular "stock armable" desde componentes de las publicaciones linkeadas
            var meliIds = linkedMeliItems.Select(mi => mi.MeliItemId).ToList();
            var comps = await db.MeliItemComponentes.AsNoTracking()
                .Where(c => meliIds.Contains(c.MeliItemId))
                .Select(c => new { c.MeliItemId, c.CafeProductoId, c.Cantidad })
                .ToListAsync();
            if (comps.Count > 0)
            {
                var compProductoIds = comps.Select(c => c.CafeProductoId).Distinct().ToList();
                var compStocks = await db.CafeProductos.AsNoTracking()
                    .Where(p => compProductoIds.Contains(p.Id))
                    .Select(p => new { p.Id, p.StockUnidades, p.StockMinimoMeLi })
                    .ToDictionaryAsync(p => p.Id);
                int armableMax = 0;
                foreach (var mid in meliIds)
                {
                    var compsThis = comps.Where(c => c.MeliItemId == mid).ToList();
                    if (compsThis.Count == 0) continue;
                    int armableThis = int.MaxValue;
                    foreach (var c in compsThis)
                    {
                        if (!compStocks.TryGetValue(c.CafeProductoId, out var ps)) { armableThis = 0; break; }
                        int reservaComp = ps.StockMinimoMeLi ?? 0;
                        int disponible = Math.Max(0, ps.StockUnidades - reservaComp);
                        int armableCalc = c.Cantidad > 0 ? (int)(disponible / c.Cantidad) : 0;
                        if (armableCalc < armableThis) armableThis = armableCalc;
                    }
                    if (armableThis != int.MaxValue && armableThis > armableMax) armableMax = armableThis;
                }
                stockArmable = armableMax;
                armableSource = "componentes";
            }
        }
        else
        {
            // Sin publicaciones linkeadas → cálculo viejo (producto físico normal)
            publicadoMeLi = Math.Max(0, stockSistema - reservaAplicada);
        }

        int stockReal = stockSistema + stockFull;

        return Ok(new {
            stockSistema,
            stockFull,
            stockReal,
            reservaAplicada,
            reservaSource,
            publicadoMeLi,
            // 2026-06-01: nuevos campos para shells
            stockArmable,
            armableSource
        });
    }

    /// <summary>Devuelve un diccionario { CafeProductoId → thumbnail_url } con la primera imagen
    /// del primer MeliItem linkeado a cada producto. Para mostrar en el listado /cafe/productos.</summary>
    [HttpGet("product-thumbnails")]
    public async Task<IActionResult> GetProductThumbnails([FromServices] Api.Data.AppDbContext db)
    {
        var map = new Dictionary<int, string>();

        // 1) Legacy: MeliItem.CafeProductoId directo
        var legacy = await db.MeliItems.AsNoTracking()
            .Where(mi => mi.CafeProductoId != null && mi.Thumbnail != null && mi.Status == "active")
            .Select(mi => new { mi.CafeProductoId, mi.Thumbnail, mi.UpdatedAt })
            .ToListAsync();
        foreach (var g in legacy.GroupBy(x => x.CafeProductoId!.Value))
        {
            var best = g.OrderByDescending(x => x.UpdatedAt).First();
            if (!string.IsNullOrEmpty(best.Thumbnail)) map[g.Key] = best.Thumbnail!;
        }

        // 2) Vía componentes: para los que aún no tienen thumb, buscar via MeliItemComponente
        var falta = await db.CafeProductos.AsNoTracking()
            .Where(p => p.IsActive && !map.Keys.Contains(p.Id))
            .Select(p => p.Id).ToListAsync();
        if (falta.Count > 0)
        {
            var viaComp = await (
                from c in db.MeliItemComponentes
                join mi in db.MeliItems on c.MeliItemId equals mi.MeliItemId
                where falta.Contains(c.CafeProductoId)
                   && mi.Thumbnail != null && mi.Status == "active"
                select new { c.CafeProductoId, mi.Thumbnail, mi.UpdatedAt }
            ).ToListAsync();
            foreach (var g in viaComp.GroupBy(x => x.CafeProductoId))
            {
                var best = g.OrderByDescending(x => x.UpdatedAt).First();
                if (!string.IsNullOrEmpty(best.Thumbnail)) map[g.Key] = best.Thumbnail!;
            }
        }

        return Ok(map);
    }

    /// <summary>Sincroniza el stock Full (meli_facility) de MeLi hacia Cafe_StockPorDeposito[Full].
    /// Iteración por todos los UPGs linkeados (~1500). Se llama solo o por job cada 30 min.
    /// Opcionalmente filtrá por un solo producto pasando ?cafeProductoId=N.</summary>
    [HttpPost("full-stock-sync")]
    public async Task<IActionResult> FullStockSync(
        [FromServices] MeliFullStockSyncService svc,
        [FromQuery] int? cafeProductoId = null)
    {
        try
        {
            var r = await svc.SyncAllAsync(cafeProductoId, HttpContext.RequestAborted);
            return Ok(new
            {
                upgsProcesados = r.UpgsProcesados,
                upgsConFull = r.UpgsFull,
                productosActualizados = r.ProductosActualizados,
                errores = r.Errores,
                mensajes = r.Mensajes
            });
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Procesa la cola de productos con StockChangedAt > LastPushedToMeli. Lo mismo
    /// que hace el job de respaldo cada 15 min, pero on-demand.</summary>
    [HttpPost("stock-push/pending")]
    public async Task<IActionResult> PushPendingStock([FromServices] MeliStockPushService pushSvc,
        [FromQuery] int max = 200)
    {
        try
        {
            var r = await pushSvc.PushPendingAsync(max, HttpContext.RequestAborted);
            return Ok(new { procesadas = r.Procesadas, ok = r.Ok, skipped = r.Skipped, errores = r.Errores, mensajes = r.Mensajes });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Push CONSERVADOR masivo: aplica reglas estrictas (no pausa, no activa, solo baja stock).
    /// Recibe lista de MeliItemIds. Por cada uno: skip si paused, skip si subiria/igualaria, skip si daria 0.
    /// Solo se ejecuta el PUT cuando hay una bajada real de stock con la pub activa.
    /// </summary>
    [HttpPost("stock-push/conservative")]
    public async Task<IActionResult> PushStockConservative(
        [FromServices] MeliStockPushService pushSvc,
        [FromBody] PushConservativeRequest req)
    {
        try
        {
            if (req?.MeliItemIds == null || req.MeliItemIds.Count == 0)
                return BadRequest(new { error = "Lista vacia" });
            var r = await pushSvc.PushStockForMeliItemsAsync(req.MeliItemIds, HttpContext.RequestAborted, conservativeMode: true);
            return Ok(new { procesadas = r.Procesadas, ok = r.Ok, skipped = r.Skipped, errores = r.Errores, mensajes = r.Mensajes });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
    public class PushConservativeRequest { public List<string>? MeliItemIds { get; set; } }

    // ============================================================
    // PUSH DE QUIETOS — productos "sin movimiento" desde el corte de Contabilium
    // ============================================================

    public record QuietoPreviewRow(
        int ProductoId,
        string? Sku,
        string Nombre,
        string? Categoria,
        decimal StockSistema,
        decimal? StockContab,
        string MeliItemId,
        string? VariationId,
        string TitulMeLi,
        int StockMeLiActual,
        string StatusMeLi,
        int Reserva,
        int APushear,
        decimal? Cantidad,
        string Diagnostico);

    /// <summary>Preview de productos "quietos" candidatos al push masivo: sin venta MeLi
    /// desde el 24/05, sin cambio de stock sistema desde el 23/05, stock > 0, no café.</summary>
    [HttpGet("quietos-preview")]
    public async Task<IActionResult> QuietosPreview([FromQuery] string fuente = "sistema",
        [FromQuery] bool incluirCafe = false,
        [FromServices] Api.Data.AppDbContext db = null!)
    {
        // 1) Lista de productos "quietos"
        var hoy = DateTime.UtcNow;
        var corteVentas = new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc);
        var corteSistema = new DateTime(2026, 5, 23, 0, 0, 0, DateTimeKind.Utc);
        var fechaSnapshot = new DateTime(2026, 5, 26, 0, 0, 0, DateTimeKind.Utc);

        // Productos vendidos en MeLi desde el corte (los excluimos)
        var productosVendidos = await (
            from mo in db.MeliOrders
            join mic in db.MeliItemComponentes
                on mo.ItemId equals mic.MeliItemId
            where mo.DateClosed >= corteVentas
                && (mo.Status == "paid" || mo.Status == "shipped" || mo.Status == "delivered")
                && (mic.MeliVariationId == null || mo.VariationId == null || mic.MeliVariationId == mo.VariationId)
            select mic.CafeProductoId
        ).Distinct().ToListAsync();

        // Productos candidatos (quietos)
        var query = db.CafeProductos
            .Where(cp => cp.IsActive
                && (cp.StockChangedAt == null || cp.StockChangedAt < corteSistema)
                && cp.StockUnidades > 0
                && !productosVendidos.Contains(cp.Id));
        if (!incluirCafe) query = query.Where(cp => cp.Categoria != "CAFE");

        var productos = await query
            .Select(cp => new
            {
                cp.Id, cp.Sku, cp.Nombre, cp.Categoria, cp.StockUnidades, cp.StockMinimoMeLi
            })
            .ToListAsync();

        if (productos.Count == 0)
            return Ok(new { rows = new List<QuietoPreviewRow>(), total = 0 });

        var prodIds = productos.Select(p => p.Id).ToList();

        // Componentes que apuntan a estos productos
        var componentes = await db.MeliItemComponentes
            .Where(c => prodIds.Contains(c.CafeProductoId))
            .ToListAsync();

        var meliItemIds = componentes.Select(c => c.MeliItemId).Distinct().ToList();
        var meliItems = await db.MeliItems
            .Where(mi => meliItemIds.Contains(mi.MeliItemId) && mi.Status == "active")
            .ToListAsync();

        // Snapshots de Contabilium del 26/05
        var skusList = productos.Where(p => p.Sku != null).Select(p => p.Sku!).Distinct().ToList();
        var snapshots = await db.StockSnapshots
            .Where(s => skusList.Contains(s.Sku) && s.Fecha >= fechaSnapshot && s.Fecha < fechaSnapshot.AddDays(1))
            .ToDictionaryAsync(s => s.Sku, s => s.StockContabilium);

        // Armar filas: una por MeliItem (con su variante si aplica) afectado por este producto
        var rows = new List<QuietoPreviewRow>();
        foreach (var p in productos)
        {
            var compsProd = componentes.Where(c => c.CafeProductoId == p.Id).ToList();
            foreach (var comp in compsProd)
            {
                var mi = meliItems.FirstOrDefault(m => m.MeliItemId == comp.MeliItemId
                    && (comp.MeliVariationId == null || comp.MeliVariationId == m.VariationId
                        || string.IsNullOrEmpty(m.VariationId)));
                if (mi == null) continue;

                int reserva = p.StockMinimoMeLi ?? 0;
                int stockCombo = comp.Cantidad > 0 ? (int)Math.Floor(p.StockUnidades / comp.Cantidad) : 0;
                int aPushear = Math.Max(0, stockCombo - reserva);
                decimal? stkContab = snapshots.TryGetValue(p.Sku ?? "", out var c) ? c : (decimal?)null;

                string diag;
                if (aPushear <= 0) diag = "skip-cero";
                else if (mi.AvailableQuantity > aPushear + 5) diag = "baja-fuerte";
                else if (mi.AvailableQuantity > aPushear) diag = "baja";
                else if (mi.AvailableQuantity < aPushear) diag = "sube";
                else diag = "igual";

                rows.Add(new QuietoPreviewRow(
                    p.Id, p.Sku, p.Nombre, p.Categoria,
                    p.StockUnidades, stkContab,
                    mi.MeliItemId, mi.VariationId,
                    mi.Title ?? "", mi.AvailableQuantity, mi.Status ?? "",
                    reserva, aPushear, comp.Cantidad, diag));
            }
        }

        return Ok(new { rows, total = rows.Count });
    }

    public class QuietosPushRequest
    {
        public List<int> ProductoIds { get; set; } = new();
        public string Fuente { get; set; } = "sistema";
    }

    /// <summary>Pushea un lote de productos quietos en modo safeBulk: no pausa, no reactiva.</summary>
    [HttpPost("quietos-push")]
    public async Task<IActionResult> QuietosPush([FromServices] MeliStockPushService pushSvc,
        [FromServices] Api.Data.AppDbContext db,
        [FromBody] QuietosPushRequest req)
    {
        if (req?.ProductoIds == null || req.ProductoIds.Count == 0)
            return BadRequest(new { error = "Lista vacia" });

        // Productos a tocar
        var productos = await db.CafeProductos
            .Where(p => req.ProductoIds.Contains(p.Id) && p.IsActive)
            .ToListAsync();
        if (productos.Count == 0) return BadRequest(new { error = "Ningun producto valido" });

        // Marcar StockChangedAt para que el push event-driven los procese (y el job de respaldo también)
        var now = DateTime.UtcNow;
        foreach (var p in productos) p.StockChangedAt = now;
        await db.SaveChangesAsync();

        // Encontrar MeLi items afectados
        var prodIds = productos.Select(p => p.Id).ToList();
        var meliItemIds = await db.MeliItemComponentes
            .Where(c => prodIds.Contains(c.CafeProductoId))
            .Select(c => c.MeliItemId).Distinct().ToListAsync();

        if (meliItemIds.Count == 0)
            return Ok(new { procesadas = 0, ok = 0, skipped = 0, errores = 0, mensajes = new[] { "Sin MLAs linkeadas" } });

        // Ejecutar push con safeBulkMode = true
        var r = await pushSvc.PushStockForMeliItemsAsync(meliItemIds, HttpContext.RequestAborted, conservativeMode: false, safeBulkMode: true);

        return Ok(new
        {
            procesadas = r.Procesadas,
            ok = r.Ok,
            skipped = r.Skipped,
            errores = r.Errores,
            mensajes = r.Mensajes
        });
    }


    /// <summary>Configura el callback URL del webhook en la app de MercadoLibre.
    /// Solo admin. La URL configurada se calcula desde la integration (RedirectUrl host) o
    /// se puede pasar explicita por query.
    /// Topics que registramos: orders_v2, items (resto los ignoramos del lado del handler).
    /// </summary>
    [HttpPost("configure-webhook")]
    [Microsoft.AspNetCore.Authorization.Authorize(Roles = "admin")]
    public async Task<IActionResult> ConfigureWebhook([FromServices] Api.Data.AppDbContext db,
        [FromServices] IHttpClientFactory httpFactory,
        [FromServices] MeliAccountService accountSvc,
        [FromQuery] string? callbackUrl = null)
    {
        var integration = await db.Integrations.FirstOrDefaultAsync(i => i.Provider == "mercadolibre");
        if (integration is null || string.IsNullOrEmpty(integration.AppId))
            return BadRequest(new { error = "Integration MercadoLibre no configurada" });

        // Si no nos pasan URL, derivar del RedirectUrl: https://app.palanica.com.ar/integraciones/meli/callback → https://app.palanica.com.ar/api/meli/webhook
        if (string.IsNullOrEmpty(callbackUrl))
        {
            var redirect = integration.RedirectUrl ?? "";
            try
            {
                var uri = new Uri(redirect);
                callbackUrl = $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}/api/meli/webhook";
            }
            catch
            {
                return BadRequest(new { error = "No pude derivar callbackUrl desde la integration. Pasalo por query: ?callbackUrl=https://..." });
            }
        }

        // Necesitamos un access_token valido — usamos el de la primera cuenta conectada.
        var account = await db.MeliAccounts.OrderByDescending(a => a.UpdatedAt).FirstOrDefaultAsync();
        if (account is null)
            return BadRequest(new { error = "No hay cuentas MeLi conectadas" });
        var token = await accountSvc.GetValidTokenAsync(account);
        if (token is null)
            return BadRequest(new { error = "No pude obtener un token valido para configurar el webhook" });

        // POST /applications/{app_id}/notifications
        var http = httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // El endpoint exacto de configuracion varia entre la consola del developer y la API.
        // Lo correcto/oficial es configurarlo en https://developers.mercadolibre.com.ar/devcenter
        // Aca lo intentamos via API (PUT /applications/{app_id}) con notifications_callback_url y topics.
        var payload = new
        {
            callback_url = callbackUrl,
            topics = new[] { "orders_v2", "items" }
        };
        var body = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload),
            System.Text.Encoding.UTF8, "application/json");
        var resp = await http.PutAsync($"https://api.mercadolibre.com/applications/{integration.AppId}", body);
        var respText = await resp.Content.ReadAsStringAsync();

        return Ok(new
        {
            callbackUrl,
            topics = new[] { "orders_v2", "items" },
            apiStatus = (int)resp.StatusCode,
            apiResponse = respText,
            hint = resp.IsSuccessStatusCode
                ? "Configurado. Verificalo en developers.mercadolibre.com.ar → tu app → Notificaciones."
                : "Si el API responde 401/403/404, configura manualmente desde developers.mercadolibre.com.ar → tu app → Notificaciones. Usa la callbackUrl de arriba y los topics orders_v2 + items."
        });
    }


    // --- Publish endpoints ---

    [HttpPost("publish/predict-category")]
    public async Task<IActionResult> PredictCategory([FromBody] PredictCategoryRequest request, [FromQuery] int accountId)
    {
        try
        {
            var result = await _itemService.PredictCategoryAsync(request.Title, accountId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("publish/category-attributes/{categoryId}")]
    public async Task<IActionResult> GetCategoryAttributes(string categoryId)
    {
        try
        {
            var result = await _itemService.GetCategoryAttributesAsync(categoryId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("publish/suggest-attributes")]
    public async Task<IActionResult> SuggestAttributes([FromBody] SuggestAttributesRequest request)
    {
        try
        {
            var result = await _aiService.SuggestAttributesAsync(request);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("items/{id}/create-product")]
    public async Task<IActionResult> CreateProductFromItem(int id)
    {
        try
        {
            var result = await _itemService.BulkCreateProductsAsync(new List<int> { id });
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    public record PushFromProductRequest(bool PushPrice = true, bool PushStock = true, decimal? OverridePrice = null);

    public record AjustePrecioRequest(decimal? AjustePctOverride, decimal? AjustePesosOverride, string? AjusteRedondeoOverride);

    public record PushPrecioAjustadoResult(bool Success, string Message, decimal? PushedPrice, decimal? PrecioBaseSistema);

    // 2026-07-01: ajuste masivo desde /publicaciones. Solo guarda en MeliItem_SyncConfig — NO pushea a MeLi.
    // ItemIds: MeliItem.Id de las filas tildadas. Campos null = no tocar el actual. ModoBorrar=true resetea a 0/null.
    // IncluirPrecioIndependiente=false salta las que tienen la fórmula independiente (PrecioIndependiente=true).
    public record BulkAjusteRequest(
        List<int> ItemIds,
        decimal? AjustePct,
        decimal? AjusteFijo,
        string? AjusteRedondeo,        // null = no tocar; "" = borrar; "99"/"999"/"000" = poner
        bool RedondeoTocado,            // frontend indica explícitamente si el usuario tocó el redondeo
        bool ModoBorrar,
        bool IncluirPrecioIndependiente);

    public record BulkAjusteResponse(int Modificados, int SaltadosPrecioIndependiente, int NoEncontrados);

    /// <summary>2026-05-29: push de stock para UNA publicación MeLi via MeliStockPushService.
    /// Maneja TANTO linkeo legacy CafeProductoId COMO componentes MeliItemComponentes.
    /// Reemplaza el botón 📦 de /publicaciones para que funcione en publis sin linkeo legacy.</summary>
    [HttpPost("items/{id}/push-stock-meliitem")]
    public async Task<IActionResult> PushStockMeliItem(int id,
        [FromServices] Api.Data.AppDbContext db,
        [FromServices] MeliStockPushService pushSvc)
    {
        var item = await db.MeliItems.FindAsync(id);
        if (item is null) return NotFound(new { error = "Item no encontrado" });

        var result = await pushSvc.PushStockForMeliItemsAsync(new List<string> { item.MeliItemId }, HttpContext.RequestAborted);
        var msg = result.Mensajes.Count > 0 ? string.Join(" · ", result.Mensajes) : "";
        bool ok = result.Ok > 0;
        return Ok(new
        {
            success = ok,
            message = ok ? $"✓ Stock pusheado a MeLi. {msg}" : (msg.Length > 0 ? msg : "Nada para pushear"),
            okCount = result.Ok,
            skipped = result.Skipped,
            errores = result.Errores
        });
    }

    /// <summary>2026-05-29: pushea precio a MeLi calculado desde PrecioOtroConIva del sistema +
    /// ajuste (Pct/Fijo/Redondeo) guardado en MeliItem_SyncConfig. Funciona para CUALQUIER
    /// publicación (legacy CafeProductoId o componentes via MeliItemComponentes), sin requerir
    /// linkeo directo. Es el endpoint que usa el boton 💵 de /publicaciones.</summary>
    [HttpPost("items/{id}/push-precio-ajustado")]
    public async Task<IActionResult> PushPrecioAjustado(int id,
        [FromServices] MeliPricePushService pricePushSvc)
    {
        // 2026-05-30: delegar al servicio compartido para evitar duplicar lógica.
        // markAsClaimed=true porque es el push manual desde /publicaciones: al primer push
        // queda "claimed" (SyncPrecio=true) y a partir de ahí auto-propaga cambios futuros
        // del sistema. El servicio respeta el modelo OEM: si el producto tiene OEM cargado,
        // precio = OEM.PvpConIva × MultiplicadorOem; si no, PrecioOtro × IVA; si combo, suma.
        var result = await pricePushSvc.PushPrecioForItemAsync(id, markAsClaimed: true);
        if (!result.Ok)
            return BadRequest(new { error = result.Message });
        return Ok(new PushPrecioAjustadoResult(true, result.Message, result.PushedPrice, result.BasePrice));
    }

    private static decimal AplicarRedondeoUpHelper(decimal valor, string? modo)
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

    /// <summary>2026-05-29: persiste los 3 valores del ajuste de precio (% / $ / redondeo)
    /// en la fila MeliItems. Devuelve 204 si OK, 404 si el item no existe.</summary>
    [HttpPut("items/{id}/ajuste-precio")]
    public async Task<IActionResult> SetAjustePrecio(int id,
        [FromBody] AjustePrecioRequest req,
        [FromServices] Api.Data.AppDbContext db)
    {
        var item = await db.MeliItems.FindAsync(id);
        if (item is null) return NotFound();
        item.AjustePctOverride = req.AjustePctOverride;
        item.AjustePesosOverride = req.AjustePesosOverride;
        item.AjusteRedondeoOverride = string.IsNullOrEmpty(req.AjusteRedondeoOverride) ? null : req.AjusteRedondeoOverride;
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>2026-07-01: aplica un ajuste (%/$/Redondeo) a MÚLTIPLES publicaciones de una vez.
    /// Solo persiste en MeliItem_SyncConfig — NO pushea a MeLi. Semántica de campos:
    /// - Si un campo viene null → no se toca el valor actual de esa publi.
    /// - Si ModoBorrar=true → resetea AjustePct=0, AjusteFijo=0, AjusteRedondeo=null.
    /// - Si RedondeoTocado=false → no se toca el redondeo actual (aunque venga "" o "99").
    /// - Si IncluirPrecioIndependiente=false → publis con PrecioIndependiente=true se saltan.</summary>
    [HttpPost("items/bulk-ajuste-precio")]
    public async Task<IActionResult> BulkAjustePrecio(
        [FromBody] BulkAjusteRequest req,
        [FromServices] Api.Data.AppDbContext db)
    {
        if (req.ItemIds is null || req.ItemIds.Count == 0)
            return BadRequest(new { error = "No hay publicaciones seleccionadas." });

        var items = await db.MeliItems
            .Where(i => req.ItemIds.Contains(i.Id))
            .ToListAsync();

        var meliItemIds = items.Select(i => i.MeliItemId).ToList();
        var configs = await db.MeliItemSyncConfigs
            .Where(c => meliItemIds.Contains(c.MeliItemId))
            .ToDictionaryAsync(c => c.MeliItemId, c => c);

        int modificados = 0;
        int saltadosPI = 0;
        var ahora = DateTime.UtcNow;

        foreach (var item in items)
        {
            if (!configs.TryGetValue(item.MeliItemId, out var cfg))
            {
                cfg = new MeliItemSyncConfig { MeliItemId = item.MeliItemId };
                db.MeliItemSyncConfigs.Add(cfg);
                configs[item.MeliItemId] = cfg;
            }

            if (cfg.PrecioIndependiente && !req.IncluirPrecioIndependiente)
            {
                saltadosPI++;
                continue;
            }

            if (req.ModoBorrar)
            {
                cfg.AjustePct = 0m;
                cfg.AjusteFijo = 0m;
                cfg.AjusteRedondeo = null;
            }
            else
            {
                if (req.AjustePct.HasValue) cfg.AjustePct = req.AjustePct.Value;
                if (req.AjusteFijo.HasValue) cfg.AjusteFijo = req.AjusteFijo.Value;
                if (req.RedondeoTocado)
                    cfg.AjusteRedondeo = string.IsNullOrEmpty(req.AjusteRedondeo) ? null : req.AjusteRedondeo;
            }
            cfg.UpdatedAt = ahora;
            modificados++;
        }

        await db.SaveChangesAsync();
        int noEncontrados = req.ItemIds.Count - items.Count;
        return Ok(new BulkAjusteResponse(modificados, saltadosPI, noEncontrados));
    }

    // 2026-07-01: masivo POR GANANCIA — pide un % de ganancia sobre costo, el sistema calcula
    // el precio necesario POR PUBLI usando SU costo + SU comisión + envío + IVA (misma lógica que
    // el bloque "¿Qué querés ganar?" de la ficha individual, aplicada a las N tildadas).
    public record BulkPrecioPorGananciaRequest(
        List<int> ItemIds,
        decimal GananciaPct,          // ej: 30 = quiero ganar 30% sobre costo
        string? Redondeo,             // null / "" / "99" / "999" / "000"
        bool IncluirPrecioIndependiente,
        bool PublicarEnMeli,
        bool SoloPiso = false,        // 2026-07-11: PISO — solo sube las que están ABAJO de GananciaPct; las que ya están >= no se tocan
        bool DryRun = false);         // 2026-07-11: VISTA PREVIA — calcula todo pero NO guarda ajuste ni pushea

    public record BulkPrecioPorGananciaDetail(
        int ItemId, string MeliItemId, string Titulo,
        decimal? Costo, decimal? PrecioBase, decimal? PrecioActual, decimal? PrecioNuevo,
        decimal? GananciaEstimada, decimal? MargenPct,
        bool Guardado, bool PusheadoOk, string? Mensaje);

    public record BulkPrecioPorGananciaResponse(
        int Total, int Guardados, int Pusheados,
        int SinCosto, int SaltadosPrecioIndep, int Errores,
        List<BulkPrecioPorGananciaDetail> Detalles);

    [HttpPost("items/bulk-precio-por-ganancia")]
    public async Task<IActionResult> BulkPrecioPorGanancia(
        [FromBody] BulkPrecioPorGananciaRequest req,
        [FromServices] Api.Data.AppDbContext db,
        [FromServices] MeliPricePushService pushSvc,
        CancellationToken ct)
    {
        if (req.ItemIds is null || req.ItemIds.Count == 0)
            return BadRequest(new { error = "No hay publicaciones seleccionadas." });

        var items = await db.MeliItems
            .Where(i => req.ItemIds.Contains(i.Id))
            .ToListAsync(ct);

        var meliItemIds = items.Select(i => i.MeliItemId).ToList();
        var configs = await db.MeliItemSyncConfigs
            .Where(c => meliItemIds.Contains(c.MeliItemId))
            .ToDictionaryAsync(c => c.MeliItemId, c => c, ct);

        var detalles = new List<BulkPrecioPorGananciaDetail>();
        int guardados = 0, pusheados = 0, sinCosto = 0, saltadosPI = 0, errores = 0;
        string? redondeo = string.IsNullOrEmpty(req.Redondeo) ? null : req.Redondeo;
        var ahora = DateTime.UtcNow;

        foreach (var it in items)
        {
            if (ct.IsCancellationRequested) break;

            // 2026-07-11: NO crear la config todavía (si es DryRun/preview no queremos escribir nada).
            configs.TryGetValue(it.MeliItemId, out var cfg);

            if (cfg?.PrecioIndependiente == true && !req.IncluirPrecioIndependiente)
            {
                saltadosPI++;
                detalles.Add(new BulkPrecioPorGananciaDetail(it.Id, it.MeliItemId, it.Title,
                    null, null, it.Price, null, null, null, false, false, "Precio independiente — se saltó"));
                continue;
            }

            // 1) Precio base del sistema
            var (precioBase, hasBase) = await pushSvc.CalcularPrecioBaseAsync(it, ct);
            if (!hasBase)
            {
                errores++;
                detalles.Add(new BulkPrecioPorGananciaDetail(it.Id, it.MeliItemId, it.Title,
                    null, null, it.Price, null, null, null, false, false, "Sin precio base del sistema (falta PrecioOtro)"));
                continue;
            }

            // 2) Costo del producto
            var costo = await pushSvc.CalcularCostoTotalAsync(it, ct);
            if (costo is null || costo.Value <= 0)
            {
                sinCosto++;
                detalles.Add(new BulkPrecioPorGananciaDetail(it.Id, it.MeliItemId, it.Title,
                    null, precioBase, it.Price, null, null, null, false, false, "Sin costo cargado en el sistema"));
                continue;
            }

            // 3) Comisión desglosada: parte % (variable con el precio) + cargo FIJO (independiente).
            //    Antes usábamos SaleFeeAmount/Price como % único → aplicaba el fijo como si escalara.
            //    Ahora separado → fórmula exacta (coincide con Integraly al peso).
            decimal pctPart;
            decimal fixedPart;
            if (it.SaleFeePercentageFee.HasValue && it.SaleFeePercentageFee.Value > 0)
            {
                pctPart = it.SaleFeePercentageFee.Value / 100m;
                fixedPart = it.SaleFeeFixedFee ?? 0m;
            }
            else if (it.SaleFeeAmount.HasValue && it.SaleFeeAmount.Value > 0 && it.Price > 0)
            {
                pctPart = it.SaleFeeAmount.Value / it.Price;
                fixedPart = 0m;
            }
            else
            {
                pctPart = 0.30m;
                fixedPart = 0m;
            }

            // 3.5) PISO: si ya está en (o arriba de) el objetivo, NO tocar (solo en modo SoloPiso).
            decimal netoConIvaAct = it.Price * (1m - pctPart) - fixedPart;
            decimal margenActual = costo.Value > 0 ? Math.Round((netoConIvaAct / 1.21m - costo.Value) / costo.Value * 100m, 1) : 0m;
            if (req.SoloPiso && margenActual >= req.GananciaPct)
            {
                detalles.Add(new BulkPrecioPorGananciaDetail(it.Id, it.MeliItemId, it.Title,
                    costo, precioBase, it.Price, it.Price, null, margenActual, false, false, $"Ya en {margenActual.ToString("0.#")}% (≥ piso) — no se toca"));
                continue;
            }

            // 4) Precio necesario para ganar req.GananciaPct sobre costo:
            //    netoConIva(precio) = precio × (1 - pctPart) - fixedPart
            //    ⇒ precio = (netoConIvaNecesario + fixedPart) / (1 - pctPart)
            decimal netoSinIvaNec = costo.Value * (1 + req.GananciaPct / 100m);
            decimal netoConIvaNec = netoSinIvaNec * 1.21m;
            decimal denom = 1m - pctPart;
            if (denom <= 0.05m)
            {
                errores++;
                detalles.Add(new BulkPrecioPorGananciaDetail(it.Id, it.MeliItemId, it.Title,
                    costo, precioBase, it.Price, null, null, null, false, false, "Comisión demasiado alta — no hay precio posible"));
                continue;
            }
            decimal precioNuevo = (netoConIvaNec + fixedPart) / denom;
            if (!string.IsNullOrEmpty(redondeo))
                precioNuevo = AplicarRedondeoUpHelper(precioNuevo, redondeo);
            precioNuevo = Math.Round(precioNuevo, 2);

            // 6) Ganancia y margen estimados con el precio nuevo (misma fórmula desglosada).
            decimal netoConIvaResult = precioNuevo * (1m - pctPart) - fixedPart;
            decimal netoSinIvaResult = netoConIvaResult / 1.21m;
            decimal gananciaEst = Math.Round(netoSinIvaResult - costo.Value, 2);
            decimal margenPct = costo.Value > 0 ? Math.Round(gananciaEst / costo.Value * 100m, 1) : 0m;

            // 2026-07-11: VISTA PREVIA — no guarda ajuste ni pushea, solo muestra el precio propuesto.
            if (req.DryRun)
            {
                detalles.Add(new BulkPrecioPorGananciaDetail(it.Id, it.MeliItemId, it.Title,
                    costo, precioBase, it.Price, precioNuevo, gananciaEst, margenPct, false, false, "Vista previa"));
                continue;
            }

            // 5) Guardar como AjustePct=0, AjusteFijo=(precioNuevo - precioBase), Redondeo.
            //    Al pushear con MeliPricePushService, se recalcula: precioBase + ajusteFijo → redondeo → MeLi.
            if (cfg is null)
            {
                cfg = new MeliItemSyncConfig { MeliItemId = it.MeliItemId };
                db.MeliItemSyncConfigs.Add(cfg);
                configs[it.MeliItemId] = cfg;
            }
            cfg.AjustePct = 0m;
            cfg.AjusteFijo = Math.Round(precioNuevo - precioBase, 2);
            cfg.AjusteRedondeo = redondeo;
            cfg.UpdatedAt = ahora;
            // 2026-07-02: guardar el objetivo de ganancia — se usa despues para verificar
            // si el precio publicado sigue dando esa ganancia (chip verde/ambar en ficha/grilla).
            cfg.GananciaObjetivoPct = req.GananciaPct;
            cfg.GananciaObjetivoAt = ahora;
            guardados++;

            detalles.Add(new BulkPrecioPorGananciaDetail(it.Id, it.MeliItemId, it.Title,
                costo, precioBase, it.Price, precioNuevo, gananciaEst, margenPct, true, false, null));
        }
        await db.SaveChangesAsync(ct);

        // 7) Si el usuario pidió publicar en MeLi, iterar y pushear con throttle.
        if (req.PublicarEnMeli)
        {
            for (int i = 0; i < detalles.Count; i++)
            {
                var d = detalles[i];
                if (!d.Guardado) continue;
                try
                {
                    var r = await pushSvc.PushPrecioForItemAsync(d.ItemId, markAsClaimed: true, ct);
                    if (r.Ok) pusheados++; else errores++;
                    detalles[i] = d with { PusheadoOk = r.Ok, Mensaje = r.Ok ? null : r.Message };
                }
                catch (Exception ex)
                {
                    errores++;
                    detalles[i] = d with { PusheadoOk = false, Mensaje = ex.Message };
                }
                await Task.Delay(200, ct);  // no saturar MeLi
            }
        }

        return Ok(new BulkPrecioPorGananciaResponse(items.Count, guardados, pusheados, sinCosto, saltadosPI, errores, detalles));
    }

    // 2026-07-01: Fase C — push masivo de precios a MeLi. Solo pushea las que tienen ajuste cargado.
    public record BulkPushPrecioRequest(List<int> ItemIds);
    public record BulkPushPrecioDetail(int ItemId, string MeliItemId, bool Ok, string Message, decimal? PushedPrice);
    public record BulkPushPrecioResponse(int Total, int Pusheados, int SinAjuste, int Errores, List<BulkPushPrecioDetail> Detalles);

    /// <summary>2026-07-01: pushea a MeLi los precios de MÚLTIPLES publis en una sola llamada.
    /// Cada publi pushea SU propio ajuste (no comparten). Publis sin ajuste cargado se saltan.
    /// Throttle 200ms entre requests para no saturar MeLi. Reporta al final total/ok/sin/error.</summary>
    [HttpPost("items/bulk-push-precio")]
    public async Task<IActionResult> BulkPushPrecio(
        [FromBody] BulkPushPrecioRequest req,
        [FromServices] Api.Data.AppDbContext db,
        [FromServices] MeliPricePushService pushSvc,
        CancellationToken ct)
    {
        if (req.ItemIds is null || req.ItemIds.Count == 0)
            return BadRequest(new { error = "No hay publicaciones seleccionadas." });

        var items = await db.MeliItems
            .Where(i => req.ItemIds.Contains(i.Id))
            .Select(i => new { i.Id, i.MeliItemId })
            .ToListAsync(ct);

        var meliItemIds = items.Select(i => i.MeliItemId).ToList();
        var configs = await db.MeliItemSyncConfigs
            .Where(c => meliItemIds.Contains(c.MeliItemId))
            .ToDictionaryAsync(c => c.MeliItemId, c => c, ct);

        var detalles = new List<BulkPushPrecioDetail>();
        int pusheados = 0, sinAjuste = 0, errores = 0;

        foreach (var it in items)
        {
            if (ct.IsCancellationRequested) break;

            bool tieneAjuste = configs.TryGetValue(it.MeliItemId, out var cfg)
                && ((cfg.AjustePct != 0m) || (cfg.AjusteFijo != 0m) || !string.IsNullOrEmpty(cfg.AjusteRedondeo));

            if (!tieneAjuste)
            {
                sinAjuste++;
                detalles.Add(new BulkPushPrecioDetail(it.Id, it.MeliItemId, false, "Sin ajuste cargado — se saltó", null));
                continue;
            }

            try
            {
                var r = await pushSvc.PushPrecioForItemAsync(it.Id, markAsClaimed: true, ct);
                if (r.Ok) pusheados++; else errores++;
                detalles.Add(new BulkPushPrecioDetail(it.Id, it.MeliItemId, r.Ok, r.Message, r.PushedPrice));
            }
            catch (Exception ex)
            {
                errores++;
                detalles.Add(new BulkPushPrecioDetail(it.Id, it.MeliItemId, false, ex.Message, null));
            }

            // Throttle 200ms entre requests — evita saturar la API de MeLi.
            await Task.Delay(200, ct);
        }

        return Ok(new BulkPushPrecioResponse(items.Count, pusheados, sinAjuste, errores, detalles));
    }

    /// <summary>2026-06-03: copia el ajuste de precio del item id a TODAS las MLAs que comparten
    /// el mismo FamilyId. Pisa lo que tenian antes. Pensado para el boton "Propagar a la familia"
    /// de /publicaciones cuando hay multiples MLAs bajo una sola familia (catalogo MeLi).
    /// NO hace push a MeLi — solo guarda. El usuario despues puede pushear cada MLA con el boton normal.
    /// Lee/escribe en MeliItem_SyncConfig (la fuente de verdad del ajuste post-refactor 2026-05-29).</summary>
    [HttpPost("items/{id}/propagar-ajuste-a-familia")]
    public async Task<IActionResult> PropagarAjusteAFamilia(int id, [FromServices] Api.Data.AppDbContext db)
    {
        var src = await db.MeliItems.FindAsync(id);
        if (src is null) return NotFound();
        if (string.IsNullOrEmpty(src.FamilyId))
            return BadRequest(new { error = "Este item no pertenece a ninguna familia (FamilyId vacio)." });

        // Leer ajuste del source desde MeliItem_SyncConfig (fuente de verdad).
        var srcCfg = await db.MeliItemSyncConfigs.FindAsync(src.MeliItemId);
        // Si no hay config aun, tomamos defaults (sin ajuste).
        decimal pct = srcCfg?.AjustePct ?? 0m;
        decimal fijo = srcCfg?.AjusteFijo ?? 0m;
        string? redondeo = srcCfg?.AjusteRedondeo;

        // Encontrar los hermanos de la familia (mismo FamilyId, excluyendo el source).
        var hermanos = await db.MeliItems
            .Where(m => m.FamilyId == src.FamilyId && m.Id != src.Id)
            .Select(m => m.MeliItemId)
            .ToListAsync();
        if (hermanos.Count == 0)
            return Ok(new { ok = true, familyId = src.FamilyId, propagados = 0 });

        // Cargar configs existentes y crear las que falten. Pisar los 3 valores con los del source.
        var existingCfgs = await db.MeliItemSyncConfigs
            .Where(c => hermanos.Contains(c.MeliItemId))
            .ToListAsync();
        var existingDict = existingCfgs.ToDictionary(c => c.MeliItemId);

        foreach (var hMli in hermanos)
        {
            if (existingDict.TryGetValue(hMli, out var cfg))
            {
                cfg.AjustePct = pct;
                cfg.AjusteFijo = fijo;
                cfg.AjusteRedondeo = redondeo;
                cfg.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                db.MeliItemSyncConfigs.Add(new Models.MeliItemSyncConfig
                {
                    MeliItemId = hMli,
                    AjustePct = pct,
                    AjusteFijo = fijo,
                    AjusteRedondeo = redondeo,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }
        await db.SaveChangesAsync();
        return Ok(new { ok = true, familyId = src.FamilyId, propagados = hermanos.Count });
    }

    [HttpPost("items/{id}/push-from-product")]
    public async Task<IActionResult> PushFromProduct(int id, [FromBody] PushFromProductRequest? request)
    {
        try
        {
            var req = request ?? new PushFromProductRequest();
            var result = await _itemService.PushFromProductAsync(id, req.PushPrice, req.PushStock, req.OverridePrice);
            if (!result.Success) return BadRequest(new { error = result.Message });
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ==========================================
    // 2026-06-11: Precio independiente por MLA (familias con cuotas distintas)
    // ==========================================

    /// <summary>Marca la MLA como precio independiente y calcula el factor automaticamente.
    /// Recibe MeliItemId (string MLA) en route.</summary>
    [HttpPost("items/mla/{mlaId}/marcar-precio-independiente")]
    public async Task<IActionResult> MarcarPrecioIndependienteByMla(string mlaId, [FromServices] Api.Data.AppDbContext db)
        => await MarcarPrecioIndependienteCore(mlaId, db);

    private async Task<IActionResult> MarcarPrecioIndependienteCore(string mlaId, Api.Data.AppDbContext db)
    {
        var item = await db.MeliItems.FirstOrDefaultAsync(i => i.MeliItemId == mlaId);
        if (item is null) return NotFound(new { error = "Publicacion no encontrada" });

        decimal? precioBase = null;
        if (item.CafeProductoId.HasValue)
        {
            var cafe = await db.CafeProductos.FirstOrDefaultAsync(p => p.Id == item.CafeProductoId.Value);
            precioBase = cafe?.PrecioOtro ?? cafe?.Pvp2 ?? cafe?.PrecioPorKg;
        }
        if (!precioBase.HasValue || precioBase.Value <= 0)
            return BadRequest(new { error = "El producto vinculado no tiene PrecioOtro cargado." });

        var precioMeLi = item.Price;
        if (precioMeLi <= 0) return BadRequest(new { error = "La publicacion no tiene precio en MeLi." });

        var factor = Math.Round(precioMeLi / precioBase.Value, 4);

        var cfg = await db.MeliItemSyncConfigs.FindAsync(item.MeliItemId);
        if (cfg is null)
        {
            cfg = new Api.Models.MeliItemSyncConfig { MeliItemId = item.MeliItemId, CreatedAt = DateTime.UtcNow };
            db.MeliItemSyncConfigs.Add(cfg);
        }
        cfg.PrecioIndependiente = true;
        cfg.PrecioFactor = factor;
        cfg.PrecioBaseRef = precioBase.Value;
        cfg.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(new { ok = true, mlaId = item.MeliItemId, precioBase = precioBase.Value, precioMeLi, factor });
    }

    [HttpPost("items/mla/{mlaId}/desmarcar-precio-independiente")]
    public async Task<IActionResult> DesmarcarPrecioIndependienteByMla(string mlaId, [FromServices] Api.Data.AppDbContext db)
    {
        var item = await db.MeliItems.FirstOrDefaultAsync(i => i.MeliItemId == mlaId);
        if (item is null) return NotFound(new { error = "Publicacion no encontrada" });

        var cfg = await db.MeliItemSyncConfigs.FindAsync(item.MeliItemId);
        if (cfg is null) return Ok(new { ok = true, mensaje = "No estaba marcada" });

        cfg.PrecioIndependiente = false;
        cfg.PrecioFactor = null;
        cfg.PrecioBaseRef = null;
        cfg.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(new { ok = true });
    }

    [HttpPost("items/mla/{mlaId}/recalcular-factor")]
    public async Task<IActionResult> RecalcularFactorByMla(string mlaId, [FromServices] Api.Data.AppDbContext db)
        => await MarcarPrecioIndependienteCore(mlaId, db);

    /// <summary>(Compat) Endpoint legacy por Id interno int.</summary>
    [HttpPost("items/{id:int}/marcar-precio-independiente")]
    public async Task<IActionResult> MarcarPrecioIndependiente(int id, [FromServices] Api.Data.AppDbContext db)
    {
        var item = await db.MeliItems.FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return NotFound(new { error = "Publicacion no encontrada" });
        return await MarcarPrecioIndependienteCore(item.MeliItemId, db);
    }

    [HttpPost("items/{id:int}/desmarcar-precio-independiente")]
    public async Task<IActionResult> DesmarcarPrecioIndependiente(int id, [FromServices] Api.Data.AppDbContext db)
    {
        var item = await db.MeliItems.FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return NotFound(new { error = "Publicacion no encontrada" });
        return await DesmarcarPrecioIndependienteByMla(item.MeliItemId, db);
    }

    [HttpPost("items/{id:int}/recalcular-factor")]
    public async Task<IActionResult> RecalcularFactor(int id, [FromServices] Api.Data.AppDbContext db)
    {
        var item = await db.MeliItems.FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return NotFound(new { error = "Publicacion no encontrada" });
        return await MarcarPrecioIndependienteCore(item.MeliItemId, db);
    }

    [HttpPost("family/{familyId}/marcar-precio-independiente")]
    public async Task<IActionResult> MarcarFamiliaPrecioIndependiente(string familyId, [FromServices] Api.Data.AppDbContext db, [FromQuery] bool marcar = true)
    {
        var items = await db.MeliItems.Where(i => i.FamilyId == familyId).ToListAsync();
        if (items.Count == 0) return NotFound(new { error = "Familia no encontrada" });

        int ok = 0, errores = 0;
        var detalle = new List<object>();
        foreach (var it in items)
        {
            try
            {
                IActionResult r = marcar ? await MarcarPrecioIndependienteCore(it.MeliItemId, db) : await DesmarcarPrecioIndependienteByMla(it.MeliItemId, db);
                if (r is OkObjectResult) { ok++; detalle.Add(new { mla = it.MeliItemId, ok = true }); }
                else { errores++; detalle.Add(new { mla = it.MeliItemId, ok = false }); }
            }
            catch (Exception ex) { errores++; detalle.Add(new { mla = it.MeliItemId, ok = false, error = ex.Message }); }
        }
        return Ok(new { familyId, total = items.Count, ok, errores, detalle });
    }

    /// <summary>Preview de propagacion: dado un nuevo PrecioOtro, devuelve precios sugeridos
    /// para todas las MLAs vinculadas a ese producto.</summary>
    [HttpGet("preview-propagacion/{cafeProductoId:int}")]
    public async Task<IActionResult> PreviewPropagacion(int cafeProductoId, [FromQuery] decimal nuevoPrecioBase, [FromServices] Api.Data.AppDbContext db)
    {
        if (nuevoPrecioBase <= 0) return BadRequest(new { error = "nuevoPrecioBase debe ser > 0" });

        var cafe = await db.CafeProductos.FirstOrDefaultAsync(p => p.Id == cafeProductoId);
        if (cafe is null) return NotFound(new { error = "Producto no encontrado" });

        var precioBaseActual = cafe.PrecioOtro ?? cafe.Pvp2 ?? cafe.PrecioPorKg ?? 0m;

        var mlasVinculadas = await db.MeliItems
            .Where(i => i.CafeProductoId == cafeProductoId)
            .ToListAsync();

        var mlaIds = mlasVinculadas.Select(m => m.MeliItemId).ToList();
        var configs = await db.MeliItemSyncConfigs.Where(c => mlaIds.Contains(c.MeliItemId)).ToListAsync();
        var configByMla = configs.ToDictionary(c => c.MeliItemId);

        var rows = mlasVinculadas.Select(m =>
        {
            configByMla.TryGetValue(m.MeliItemId, out var cfg);
            var esIndependiente = cfg?.PrecioIndependiente ?? false;
            decimal? precioSugerido = null;
            if (esIndependiente && cfg!.PrecioFactor.HasValue)
                precioSugerido = Math.Round(nuevoPrecioBase * cfg.PrecioFactor.Value, 2, MidpointRounding.AwayFromZero);
            return new
            {
                mlaId = m.MeliItemId,
                title = m.Title,
                precioActualMeLi = m.Price,
                esIndependiente,
                factor = cfg?.PrecioFactor,
                listingType = cfg?.ListingType,
                installmentConfig = cfg?.InstallmentConfig,
                freeShipping = cfg?.FreeShipping,
                precioSugerido,
                diferencia = precioSugerido.HasValue ? (precioSugerido.Value - m.Price) : (decimal?)null
            };
        }).ToList();

        return Ok(new { cafeProductoId, sku = cafe.Sku, precioBaseActual, nuevoPrecioBase, mlas = rows });
    }

    /// <summary>2026-06-11: Análisis de margen por familia.
    /// Para cada MLA devuelve: tipo publicación, cuotas, envío gratis, % comisión total, neto.
    /// Permite ver si todas las MLAs de la familia dejan la misma ganancia neta.</summary>
    [HttpGet("family/{familyId}/analisis-margen")]
    public async Task<IActionResult> AnalisisMargenFamilia(
        string familyId,
        [FromServices] Api.Data.AppDbContext db,
        [FromQuery] decimal comisionCategoriaPct = 13m,
        [FromQuery] decimal envioGratisCostoEstimado = 7470m,
        [FromQuery] decimal toleranciaNetoPct = 3m)
    {
        var items = await db.MeliItems.Where(i => i.FamilyId == familyId)
            .OrderBy(i => i.Price)
            .ToListAsync();
        if (items.Count == 0) return NotFound(new { error = "Familia no encontrada" });

        var mlaIds = items.Select(m => m.MeliItemId).ToList();
        var configs = await db.MeliItemSyncConfigs.Where(c => mlaIds.Contains(c.MeliItemId)).ToListAsync();
        var configByMla = configs.ToDictionary(c => c.MeliItemId);

        // SKU del producto base (todos comparten producto)
        var first = items[0];
        string? sku = null;
        decimal precioOtroBase = 0m;
        if (first.CafeProductoId.HasValue)
        {
            var prod = await db.CafeProductos.FirstOrDefaultAsync(p => p.Id == first.CafeProductoId.Value);
            sku = prod?.Sku;
            precioOtroBase = prod?.PrecioOtro ?? prod?.Pvp2 ?? prod?.PrecioPorKg ?? 0m;
        }

        var mlas = items.Select(m =>
        {
            configByMla.TryGetValue(m.MeliItemId, out var cfg);
            // 2026-06-11: si vos seteaste un override en SyncConfig.InstallmentConfig, lo usamos.
            // Sino caemos al InstallmentTag que vino del sync de MeLi.
            // El override sirve para casos donde MeLi nos perdió el tag (ej: MLA1681436129)
            // y queres corregirlo a mano sin afectar el sync.
            var tagEfectivo = !string.IsNullOrWhiteSpace(cfg?.InstallmentConfig)
                ? cfg!.InstallmentConfig
                : m.InstallmentTag;
            return Api.Services.MeliComisionHelper.CalcularMla(
                mlaId: m.MeliItemId,
                listingType: m.ListingTypeId,
                installmentTag: tagEfectivo,
                freeShipping: m.FreeShipping,
                precioMeLi: m.Price,
                comisionCategoriaPct: comisionCategoriaPct,
                precioFactor: cfg?.PrecioFactor,
                precioIndependiente: cfg?.PrecioIndependiente ?? false,
                envioGratisCostoEstimado: envioGratisCostoEstimado);
        }).ToList();

        var netos = mlas.Select(m => m.Neto).Where(n => n > 0).ToList();
        var netoMin = netos.Count > 0 ? netos.Min() : 0m;
        var netoMax = netos.Count > 0 ? netos.Max() : 0m;
        var netoPromedio = netos.Count > 0 ? Math.Round(netos.Average(), 2) : 0m;
        var spread = netoMax - netoMin;
        var spreadPct = netoPromedio > 0 ? Math.Round(spread / netoPromedio * 100m, 2) : 0m;

        // Marcar dentroDeRango = neto está dentro de toleranciaNetoPct% del promedio
        var tolerancia = netoPromedio * toleranciaNetoPct / 100m;
        var dentroDeRangoCount = mlas.Count(m => Math.Abs(m.Neto - netoPromedio) <= tolerancia);
        var fueraDeRangoCount = mlas.Count - dentroDeRangoCount;

        return Ok(new
        {
            familyId,
            sku,
            precioOtroBase,
            comisionCategoriaPct,
            envioGratisCostoEstimado,
            toleranciaNetoPct,
            mlas = mlas.Select(m => new
            {
                m.MeliItemId,
                m.ListingType,
                m.ListingTypeLabel,
                m.InstallmentTag,
                m.InstallmentLabel,
                m.FreeShipping,
                m.PrecioMeLi,
                m.ComisionCategoriaPct,
                m.ComisionFinanciacionPct,
                m.ComisionTotalPct,
                m.ComisionMonto,
                m.CargoFijo,
                m.ShippingCostoEstimado,
                m.Neto,
                m.PrecioFactor,
                m.PrecioIndependiente,
                dentroDeRango = Math.Abs(m.Neto - netoPromedio) <= tolerancia,
                diferenciaVsPromedio = Math.Round(m.Neto - netoPromedio, 2)
            }),
            stats = new
            {
                netoMin,
                netoMax,
                netoPromedio,
                spread,
                spreadPct,
                dentroDeRangoCount,
                fueraDeRangoCount
            }
        });
    }

    /// <summary>2026-06-11: Setear override del tag de cuotas para una MLA.
    /// Útil cuando MeLi nos perdió el tag y querés corregirlo a mano.
    /// Pasar null o "" para limpiar el override (vuelve a usar el de MeLi).
    /// Valores válidos: "no_installments" | "co-funded" | "3x_campaign" | "6x_campaign" | "9x_campaign" | "12x_campaign"</summary>
    [HttpPost("items/mla/{mlaId}/override-cuotas")]
    public async Task<IActionResult> OverrideCuotas(string mlaId, [FromBody] OverrideCuotasRequest req, [FromServices] Api.Data.AppDbContext db)
    {
        var item = await db.MeliItems.FirstOrDefaultAsync(i => i.MeliItemId == mlaId);
        if (item is null) return NotFound(new { error = "Publicacion no encontrada" });

        var cfg = await db.MeliItemSyncConfigs.FindAsync(mlaId);
        if (cfg is null)
        {
            cfg = new Api.Models.MeliItemSyncConfig { MeliItemId = mlaId, CreatedAt = DateTime.UtcNow };
            db.MeliItemSyncConfigs.Add(cfg);
        }
        cfg.InstallmentConfig = string.IsNullOrWhiteSpace(req.Tag) ? null : req.Tag.Trim();
        cfg.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(new
        {
            ok = true,
            mlaId,
            tagAplicado = cfg.InstallmentConfig,
            label = Api.Services.MeliComisionHelper.GetInstallmentLabel(cfg.InstallmentConfig ?? item.InstallmentTag)
        });
    }

    public record OverrideCuotasRequest(string? Tag);

    /// <summary>Push masivo a una familia: dispara PushFromProductAsync para todas las MLAs.</summary>
    [HttpPost("family/{familyId}/push-masivo")]
    public async Task<IActionResult> PushMasivoFamilia(string familyId, [FromServices] Api.Data.AppDbContext db)
    {
        var items = await db.MeliItems.Where(i => i.FamilyId == familyId).ToListAsync();
        if (items.Count == 0) return NotFound(new { error = "Familia no encontrada" });

        int ok = 0, errores = 0;
        var detalle = new List<object>();
        foreach (var it in items)
        {
            try
            {
                var r = await _itemService.PushFromProductAsync(it.Id, pushPrice: true, pushStock: false);
                if (r.Success) { ok++; detalle.Add(new { mla = it.MeliItemId, ok = true }); }
                else { errores++; detalle.Add(new { mla = it.MeliItemId, ok = false, error = r.Message }); }
            }
            catch (Exception ex) { errores++; detalle.Add(new { mla = it.MeliItemId, ok = false, error = ex.Message }); }
        }
        return Ok(new { familyId, total = items.Count, ok, errores, detalle });
    }

        [HttpPost("items/bulk-create-products")]
    public async Task<IActionResult> BulkCreateProducts([FromBody] BulkCreateProductsRequest request)
    {
        if (request.Ids == null || !request.Ids.Any())
            return BadRequest(new { error = "No se proporcionaron IDs" });
        try
        {
            var result = await _itemService.BulkCreateProductsAsync(request.Ids);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

        [HttpPost("publish")]
    public async Task<IActionResult> PublishItem([FromBody] PublishItemRequest request)
    {
        try
        {
            var result = await _itemService.PublishItemAsync(request);
            if (!result.Success)
                return BadRequest(new { error = result.Error });
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("publish/bulk")]
    public async Task<IActionResult> BulkPublish([FromBody] BulkPublishRequest request)
    {
        try
        {
            var result = await _itemService.BulkPublishAsync(request);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // CRUD de MeliItemComponentes — edición inline desde /cafe/skus-meli
    // ═════════════════════════════════════════════════════════════════════════
    // Cada componente dice: "1 venta de tal MLA descuenta X unidades de tal CafeProducto".
    // Cambiar componentes afecta cómo se calcula el stock y cómo se descuenta al vender.
    // Después de cualquier cambio, AUTO-PUSHEAMOS el stock a MeLi para que MeLi vea el cálculo nuevo.

    public record UpsertComponenteRequest(string MeliItemId, int CafeProductoId, decimal Cantidad,
        string? Formato, string? MeliVariationId);
    public record UpdateComponenteRequest(decimal Cantidad, string? Formato);

    /// <summary>Actualiza cantidad/formato de un componente existente. Auto-pushea stock al MeLi.</summary>
    [HttpPut("componente/{id:int}")]
    public async Task<IActionResult> UpdateComponente(int id, [FromBody] UpdateComponenteRequest req,
        [FromServices] Api.Data.AppDbContext _db,
        [FromServices] MeliStockPushService pushSvc)
    {
        if (req.Cantidad <= 0)
            return BadRequest(new { error = "Cantidad debe ser mayor a 0" });

        var comp = await _db.MeliItemComponentes.FirstOrDefaultAsync(c => c.Id == id);
        if (comp is null) return NotFound(new { error = "Componente no encontrado" });

        comp.Cantidad = req.Cantidad;
        if (req.Formato is not null) comp.Formato = string.IsNullOrWhiteSpace(req.Formato) ? null : req.Formato;
        await _db.SaveChangesAsync();

        // Auto-push: stock cambió para esta MLA, mandamos el nuevo a MeLi.
        try
        {
            await pushSvc.PushStockForMeliItemsAsync(new List<string> { comp.MeliItemId });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = true, push = "error", pushError = ex.Message });
        }

        return Ok(new { ok = true, push = "disparado" });
    }

    /// <summary>Crea un componente nuevo (linkea producto X con cantidad Y a una MLA).
    /// Si ya existe para ese (meliItemId + productoId + variationId), devuelve error.</summary>
    [HttpPost("componente")]
    public async Task<IActionResult> CreateComponente([FromBody] UpsertComponenteRequest req,
        [FromServices] Api.Data.AppDbContext _db,
        [FromServices] MeliStockPushService pushSvc)
    {
        if (req.Cantidad <= 0) return BadRequest(new { error = "Cantidad debe ser mayor a 0" });
        if (string.IsNullOrWhiteSpace(req.MeliItemId)) return BadRequest(new { error = "MeliItemId requerido" });

        // Verificar que el MeliItem existe
        var meliItemExists = await _db.MeliItems.AnyAsync(mi => mi.MeliItemId == req.MeliItemId);
        if (!meliItemExists) return NotFound(new { error = $"MeliItem {req.MeliItemId} no encontrado" });

        // Verificar que el producto existe
        var prod = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == req.CafeProductoId);
        if (prod is null) return NotFound(new { error = $"Producto id={req.CafeProductoId} no encontrado" });

        // Anti-duplicado: no permitir 2 componentes iguales (misma MLA+producto+variation)
        var dup = await _db.MeliItemComponentes.AnyAsync(c =>
            c.MeliItemId == req.MeliItemId &&
            c.CafeProductoId == req.CafeProductoId &&
            ((req.MeliVariationId == null && c.MeliVariationId == null) || c.MeliVariationId == req.MeliVariationId));
        if (dup) return Conflict(new { error = "Ya existe un componente con ese producto/variación. Edítalo en vez de duplicar." });

        var comp = new Api.Models.MeliItemComponente
        {
            MeliItemId = req.MeliItemId,
            CafeProductoId = req.CafeProductoId,
            Cantidad = req.Cantidad,
            Formato = string.IsNullOrWhiteSpace(req.Formato) ? null : req.Formato,
            MeliVariationId = string.IsNullOrWhiteSpace(req.MeliVariationId) ? null : req.MeliVariationId,
            Source = "manual_ui",
            CreatedAt = DateTime.UtcNow
        };
        _db.MeliItemComponentes.Add(comp);
        await _db.SaveChangesAsync();

        // Auto-push
        try { await pushSvc.PushStockForMeliItemsAsync(new List<string> { req.MeliItemId }); }
        catch (Exception ex) { return Ok(new { ok = true, id = comp.Id, push = "error", pushError = ex.Message }); }

        return Ok(new { ok = true, id = comp.Id, push = "disparado" });
    }

    /// <summary>Elimina un componente. Si era el último de la MLA, la publicación queda
    /// sin descontar nada al venderse (advertencia visual en la UI).</summary>
    [HttpDelete("componente/{id:int}")]
    public async Task<IActionResult> DeleteComponente(int id,
        [FromServices] Api.Data.AppDbContext _db,
        [FromServices] MeliStockPushService pushSvc)
    {
        var comp = await _db.MeliItemComponentes.FirstOrDefaultAsync(c => c.Id == id);
        if (comp is null) return NotFound(new { error = "Componente no encontrado" });

        var meliItemId = comp.MeliItemId;
        _db.MeliItemComponentes.Remove(comp);
        await _db.SaveChangesAsync();

        // Auto-push (con el cálculo nuevo — si quedó sin componentes el cálculo será 0 o legacy)
        try { await pushSvc.PushStockForMeliItemsAsync(new List<string> { meliItemId }); }
        catch (Exception ex) { return Ok(new { ok = true, push = "error", pushError = ex.Message }); }

        return Ok(new { ok = true, push = "disparado" });
    }
}

public class BulkDeleteRequest
{
    public List<int> Ids { get; set; } = new();
}

public class CreateTestUserRequest
{
    public string SiteId { get; set; } = string.Empty;
}

