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

