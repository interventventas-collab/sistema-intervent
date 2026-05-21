using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

