using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
        string Estado,                       // "ok" / "sin_link" / "sin_combo" / "producto_suelto"
        int? ComboId,                        // Cafe_Combos.Id si existe
        string? ComboNombre,
        int StockArmableCombo,               // stock que se puede armar segun min(componentes/cantidad)
        List<SkuMeliComponenteRow> Componentes,
        int? ProductoSueltoId,
        string? ProductoSueltoNombre,
        int? ProductoSueltoStock
    );

    public record SkuMeliComponenteRow(int ProductoId, string Sku, string Nombre, int Cantidad, int StockDisponible);
    public record SkuMeliPubRow(string MeliItemId, string Status, int AvailableQuantity, string? Permalink);

    /// <summary>Devuelve el listado de SKUs únicos de MeliItems con su árbol genealógico:
    /// publicaciones que lo usan, combo del sistema (si existe), componentes, stock armable.
    /// Soporta filtros: estado (ok/sin_link/sin_combo/producto_suelto) + búsqueda por SKU.</summary>
    [HttpGet("skus-meli")]
    public async Task<IActionResult> GetSkusMeli(
        [FromServices] Api.Data.AppDbContext db,
        [FromQuery] string? buscar = null,
        [FromQuery] string? estado = null,
        [FromQuery] int limit = 1000)
    {
        // 1) SKUs únicos de MeliItems (excluye cerradas, incluye paused y active)
        var skusBaseQ = db.MeliItems.AsNoTracking()
            .Where(mi => mi.Sku != null && mi.Sku != "" && (mi.Status == "active" || mi.Status == "paused"));
        if (!string.IsNullOrWhiteSpace(buscar))
        {
            var t = buscar.Trim().ToUpper();
            skusBaseQ = skusBaseQ.Where(mi => mi.Sku!.ToUpper().Contains(t));
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
                    componentes = comps.Select(c => new SkuMeliComponenteRow(
                        c.ProductoId, c.Sku ?? "", c.Nombre, c.Cantidad, c.StockUnidades
                    )).ToList();
                    // stock armable = MIN(stock_componente / cantidad) entre todos los componentes
                    if (componentes.Count > 0)
                    {
                        stockArmable = componentes
                            .Where(c => c.Cantidad > 0)
                            .Select(c => (int)Math.Floor(c.StockDisponible / (double)c.Cantidad))
                            .DefaultIfEmpty(0).Min();
                    }
                }
                estadoCalc = s.HayLinkeado > 0 ? "ok" : "sin_link";
            }
            else if (prodBySku.TryGetValue(s.Sku, out var prd))
            {
                prodId = prd.Id; prodNombre = prd.Nombre; prodStock = prd.StockUnidades;
                stockArmable = prd.StockUnidades;
                estadoCalc = s.HayLinkeado > 0 ? "ok" : "producto_suelto";
            }
            else
            {
                estadoCalc = "sin_combo";
            }

            // Filtro por estado
            if (!string.IsNullOrEmpty(estado) && estado != estadoCalc) continue;

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
            if (result.Count >= limit) break;
        }

        // Stats globales (sin paginar)
        var totalSkus = skusAgg.Count;
        return Ok(new {
            total = totalSkus,
            mostrados = result.Count,
            items = result
        });
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
    /// stock propio (9 de Abril), Full MeLi, total real, reserva aplicada y publicado en MeLi.</summary>
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

        int publicadoSimple = Math.Max(0, stockSistema - reservaAplicada);
        int stockReal = stockSistema + stockFull;

        return Ok(new {
            stockSistema,
            stockFull,
            stockReal,
            reservaAplicada,
            reservaSource,
            publicadoMeLi = publicadoSimple
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

    public record PushFromProductRequest(bool PushPrice = true, bool PushStock = true);

    [HttpPost("items/{id}/push-from-product")]
    public async Task<IActionResult> PushFromProduct(int id, [FromBody] PushFromProductRequest? request)
    {
        try
        {
            var req = request ?? new PushFromProductRequest();
            var result = await _itemService.PushFromProductAsync(id, req.PushPrice, req.PushStock);
            if (!result.Success) return BadRequest(new { error = result.Message });
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
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
}

public class BulkDeleteRequest
{
    public List<int> Ids { get; set; } = new();
}

public class CreateTestUserRequest
{
    public string SiteId { get; set; } = string.Empty;
}

