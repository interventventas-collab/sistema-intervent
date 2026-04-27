using System.Net.Http.Headers;
using System.Text.Json;
using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class MeliOrderService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly AuditLogService _auditLog;

    public MeliOrderService(AppDbContext db, IHttpClientFactory httpFactory, MeliAccountService accountService, AuditLogService auditLog)
    {
        _db = db;
        _httpFactory = httpFactory;
        _accountService = accountService;
        _auditLog = auditLog;
    }

    public async Task<MeliOrdersResponse> GetOrdersAsync(DateTime from, DateTime to, int? meliAccountId = null)
    {
        var query = _db.MeliOrders
            .Include(o => o.MeliAccount)
            .Where(o => o.DateCreated >= from && o.DateCreated <= to);

        if (meliAccountId.HasValue)
            query = query.Where(o => o.MeliAccountId == meliAccountId.Value);

        var total = await query.CountAsync();
        var orders = await query
            .OrderByDescending(o => o.DateCreated)
            .GroupJoin(
                _db.MeliItems,
                o => o.ItemId,
                i => i.MeliItemId,
                (o, items) => new { Order = o, Items = items })
            .SelectMany(
                x => x.Items.DefaultIfEmpty(),
                (x, item) => new MeliOrderDto(
                    x.Order.Id, x.Order.MeliOrderId, x.Order.MeliAccountId,
                    x.Order.MeliAccount != null ? x.Order.MeliAccount.Nickname : "Desconocida",
                    x.Order.Status, x.Order.DateCreated, x.Order.DateClosed,
                    x.Order.TotalAmount, x.Order.CurrencyId,
                    x.Order.BuyerId, x.Order.BuyerNickname,
                    x.Order.ItemId, x.Order.ItemTitle, x.Order.Quantity, x.Order.UnitPrice, x.Order.FullUnitPrice,
                    x.Order.ShippingId, x.Order.PackId,
                    item != null ? item.Thumbnail : null,
                    x.Order.ShippingStatus,
                    x.Order.ShippingSubstatus))
            .ToListAsync();

        return new MeliOrdersResponse(orders, total);
    }

    public async Task<MeliOrderSyncResult> SyncOrdersAsync(DateTime from, DateTime to)
    {
        var accounts = await _accountService.GetAllAccountEntitiesAsync();
        int totalSynced = 0;
        int totalErrors = 0;
        var errors = new List<string>();

        foreach (var account in accounts)
        {
            try
            {
                var token = await _accountService.GetValidTokenAsync(account);
                if (token is null)
                {
                    errors.Add($"Token expirado para {account.Nickname}");
                    totalErrors++;
                    continue;
                }

                var synced = await SyncOrdersForAccountAsync(account, token, from, to);
                totalSynced += synced;
            }
            catch (Exception ex)
            {
                errors.Add($"Error en {account.Nickname}: {ex.Message}");
                totalErrors++;
            }
        }

        // Audit log for sync - include full error details as JSON
        var auditData = new
        {
            resumen = $"Sincronizados {totalSynced} ordenes, {totalErrors} errores",
            totalSincronizados = totalSynced,
            totalErrores = totalErrors,
            errores = errors
        };
        var syncJson = System.Text.Json.JsonSerializer.Serialize(auditData);
        await _auditLog.LogAsync("Sync", "orders", "SYNC", syncJson);

        return new MeliOrderSyncResult(totalSynced, totalErrors, errors);
    }

    private async Task<int> SyncOrdersForAccountAsync(MeliAccount account, string token, DateTime from, DateTime to)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        int offset = 0;
        int limit = 50;
        int synced = 0;
        bool hasMore = true;

        var fromStr = from.ToString("yyyy-MM-ddTHH:mm:ss.000-00:00");
        var toStr = to.ToString("yyyy-MM-ddTHH:mm:ss.000-00:00");

        while (hasMore)
        {
            var url = $"https://api.mercadolibre.com/orders/search" +
                $"?seller={account.MeliUserId}" +
                $"&sort=date_desc" +
                $"&order.date_created.from={fromStr}" +
                $"&order.date_created.to={toStr}" +
                $"&offset={offset}&limit={limit}";

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
            var paging = doc.GetProperty("paging");
            var total = paging.GetProperty("total").GetInt32();

            foreach (var order in results.EnumerateArray())
            {
                long? packId = order.TryGetProperty("pack_id", out var pid)
                    && pid.ValueKind != JsonValueKind.Null
                    ? pid.GetInt64() : null;
                synced += await UpsertOrderAsync(account.Id, order, packId, http);
            }

            await _db.SaveChangesAsync();

            offset += limit;
            hasMore = offset < total;
        }

        return synced;
    }

    public async Task<MeliOrderDetailResponse> GetOrderDetailAsync(long meliOrderId)
    {
        // Find the order in our DB to get the account
        var dbOrder = await _db.MeliOrders
            .Include(o => o.MeliAccount)
            .FirstOrDefaultAsync(o => o.MeliOrderId == meliOrderId);

        if (dbOrder is null)
            throw new Exception("Orden no encontrada");

        var token = await _accountService.GetValidTokenAsync(dbOrder.MeliAccount!);
        if (token is null)
        {
            // Forzar refresh si el token normal falla
            token = await _accountService.GetValidTokenAsync(dbOrder.MeliAccount!, forceRefresh: true);
            if (token is null)
                throw new Exception("Token expirado para la cuenta. Reconecta la cuenta de MercadoLibre.");
        }

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // If part of a pack, find all order IDs in this pack
        var orderIds = new List<long> { meliOrderId };
        if (dbOrder.PackId.HasValue)
        {
            var packOrderIds = await _db.MeliOrders
                .Where(o => o.PackId == dbOrder.PackId.Value)
                .Select(o => o.MeliOrderId)
                .Distinct()
                .ToListAsync();
            orderIds = packOrderIds;
        }

        var result = new MeliOrderDetailResponse();

        foreach (var orderId in orderIds)
        {
            var detail = await FetchOrderDetailFromMeli(http, orderId);
            if (detail is not null)
                result.Orders.Add(detail);
        }

        return result;
    }

    public async Task<MeliOrderDetailResponse> GetPackDetailAsync(long packId)
    {
        // Find all orders in this pack
        var dbOrders = await _db.MeliOrders
            .Include(o => o.MeliAccount)
            .Where(o => o.PackId == packId)
            .ToListAsync();

        if (!dbOrders.Any())
            throw new Exception("Pack no encontrado");

        var account = dbOrders.First().MeliAccount!;
        var token = await _accountService.GetValidTokenAsync(account);
        if (token is null)
        {
            token = await _accountService.GetValidTokenAsync(account, forceRefresh: true);
            if (token is null)
                throw new Exception("Token expirado para la cuenta. Reconecta la cuenta de MercadoLibre.");
        }

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var uniqueOrderIds = dbOrders.Select(o => o.MeliOrderId).Distinct().ToList();
        var result = new MeliOrderDetailResponse();

        foreach (var orderId in uniqueOrderIds)
        {
            var detail = await FetchOrderDetailFromMeli(http, orderId);
            if (detail is not null)
                result.Orders.Add(detail);
        }

        return result;
    }

    private async Task<MeliOrderDetailDto?> FetchOrderDetailFromMeli(HttpClient http, long meliOrderId)
    {
        var response = await http.GetAsync($"https://api.mercadolibre.com/orders/{meliOrderId}");
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync();
        var order = JsonDocument.Parse(json).RootElement;

        var detail = new MeliOrderDetailDto
        {
            MeliOrderId = meliOrderId,
            Status = order.GetProperty("status").GetString() ?? "unknown",
            DateCreated = order.GetProperty("date_created").GetDateTime(),
            DateClosed = order.TryGetProperty("date_closed", out var dc) && dc.ValueKind != JsonValueKind.Null
                ? dc.GetDateTime() : null,
            TotalAmount = order.GetProperty("total_amount").GetDecimal(),
            CurrencyId = order.GetProperty("currency_id").GetString() ?? "ARS",
            PackId = order.TryGetProperty("pack_id", out var pid) && pid.ValueKind != JsonValueKind.Null
                ? pid.GetInt64() : null,
            BuyerId = order.GetProperty("buyer").GetProperty("id").GetInt64(),
            BuyerNickname = order.GetProperty("buyer").GetProperty("nickname").GetString() ?? "",
            BuyerFirstName = order.GetProperty("buyer").TryGetProperty("first_name", out var fn) && fn.ValueKind != JsonValueKind.Null
                ? fn.GetString() : null,
            BuyerLastName = order.GetProperty("buyer").TryGetProperty("last_name", out var ln) && ln.ValueKind != JsonValueKind.Null
                ? ln.GetString() : null,
        };

        // Parse taxes at order level
        if (order.TryGetProperty("taxes", out var taxes) && taxes.ValueKind != JsonValueKind.Null)
        {
            if (taxes.TryGetProperty("amount", out var taxAmt) && taxAmt.ValueKind != JsonValueKind.Null)
                detail.TaxesAmount = taxAmt.GetDecimal();
        }

        // Parse items
        decimal totalSaleFee = 0;
        var itemIds = new List<string>();
        foreach (var item in order.GetProperty("order_items").EnumerateArray())
        {
            var itemId = item.GetProperty("item").GetProperty("id").GetString() ?? "";
            var saleFee = item.TryGetProperty("sale_fee", out var sf) && sf.ValueKind != JsonValueKind.Null
                ? sf.GetDecimal() : (decimal?)null;
            if (saleFee.HasValue) totalSaleFee += saleFee.Value;

            detail.Items.Add(new MeliOrderItemDetail
            {
                ItemId = itemId,
                Title = item.GetProperty("item").GetProperty("title").GetString() ?? "",
                Quantity = item.GetProperty("quantity").GetInt32(),
                UnitPrice = item.GetProperty("unit_price").GetDecimal(),
                SaleFee = saleFee
            });
            itemIds.Add(itemId);
        }
        detail.TotalSaleFee = totalSaleFee;

        // Fetch thumbnails for items (batch)
        if (itemIds.Any())
        {
            try
            {
                var idsParam = string.Join(",", itemIds);
                var itemsResponse = await http.GetAsync($"https://api.mercadolibre.com/items?ids={idsParam}");
                if (itemsResponse.IsSuccessStatusCode)
                {
                    var itemsJson = await itemsResponse.Content.ReadAsStringAsync();
                    var itemsDoc = JsonDocument.Parse(itemsJson).RootElement;
                    foreach (var itemResult in itemsDoc.EnumerateArray())
                    {
                        if (itemResult.TryGetProperty("body", out var body) && body.ValueKind != JsonValueKind.Null)
                        {
                            var id = body.GetProperty("id").GetString() ?? "";
                            var thumb = body.TryGetProperty("thumbnail", out var th) && th.ValueKind != JsonValueKind.Null
                                ? th.GetString() : null;
                            // Replace http with https for thumbnails
                            if (thumb != null && thumb.StartsWith("http://"))
                                thumb = "https://" + thumb[7..];
                            var detailItem = detail.Items.FirstOrDefault(i => i.ItemId == id);
                            if (detailItem is not null)
                                detailItem.ThumbnailUrl = thumb;
                        }
                    }
                }
            }
            catch { /* thumbnails are optional, don't fail the whole request */ }
        }

        // Parse payments
        decimal totalShippingCost = 0;
        if (order.TryGetProperty("payments", out var payments) && payments.ValueKind != JsonValueKind.Null)
        {
            foreach (var payment in payments.EnumerateArray())
            {
                var shCost = payment.TryGetProperty("shipping_cost", out var sc) && sc.ValueKind != JsonValueKind.Null
                    ? sc.GetDecimal() : (decimal?)null;
                if (shCost.HasValue) totalShippingCost += shCost.Value;

                detail.Payments.Add(new MeliPaymentDetail
                {
                    PaymentId = payment.GetProperty("id").GetInt64(),
                    Status = payment.GetProperty("status").GetString() ?? "",
                    PaymentType = payment.TryGetProperty("payment_type", out var pt) && pt.ValueKind != JsonValueKind.Null
                        ? pt.GetString() : null,
                    PaymentMethodId = payment.TryGetProperty("payment_method_id", out var pm) && pm.ValueKind != JsonValueKind.Null
                        ? pm.GetString() : null,
                    TransactionAmount = payment.TryGetProperty("transaction_amount", out var ta) && ta.ValueKind != JsonValueKind.Null
                        ? ta.GetDecimal() : 0,
                    TotalPaidAmount = payment.TryGetProperty("total_paid_amount", out var tpa) && tpa.ValueKind != JsonValueKind.Null
                        ? tpa.GetDecimal() : 0,
                    ShippingCost = shCost,
                    TaxesAmount = payment.TryGetProperty("taxes_amount", out var taxA) && taxA.ValueKind != JsonValueKind.Null
                        ? taxA.GetDecimal() : null,
                    DateApproved = payment.TryGetProperty("date_approved", out var da) && da.ValueKind != JsonValueKind.Null
                        ? da.GetDateTime() : null,
                    Installments = payment.TryGetProperty("installments", out var inst) && inst.ValueKind != JsonValueKind.Null
                        ? inst.GetInt32() : null
                });
            }
        }
        detail.ShippingCost = totalShippingCost;

        return detail;
    }

    private async Task<int> UpsertOrderAsync(int accountId, JsonElement order, long? packId, HttpClient http)
    {
        var meliOrderId = order.GetProperty("id").GetInt64();
        var status = order.GetProperty("status").GetString() ?? "unknown";
        var dateCreated = order.GetProperty("date_created").GetDateTime();
        var dateClosed = order.TryGetProperty("date_closed", out var dc)
            && dc.ValueKind != JsonValueKind.Null
            ? dc.GetDateTime() : (DateTime?)null;
        var totalAmount = order.GetProperty("total_amount").GetDecimal();
        var currencyId = order.GetProperty("currency_id").GetString() ?? "ARS";
        var buyerId = order.GetProperty("buyer").GetProperty("id").GetInt64();
        var buyerNickname = order.GetProperty("buyer")
            .GetProperty("nickname").GetString() ?? "Desconocido";
        var shippingId = order.TryGetProperty("shipping", out var sh)
            && sh.TryGetProperty("id", out var shId)
            && shId.ValueKind != JsonValueKind.Null
            ? shId.GetInt64() : (long?)null;

        // Fetch shipping status + substatus from shipments API
        string? shippingStatus = null;
        string? shippingSubstatus = null;
        if (shippingId.HasValue)
        {
            try
            {
                var shipResp = await http.GetAsync($"https://api.mercadolibre.com/shipments/{shippingId.Value}");
                if (shipResp.IsSuccessStatusCode)
                {
                    var shipJson = await shipResp.Content.ReadAsStringAsync();
                    var shipDoc = JsonDocument.Parse(shipJson).RootElement;
                    if (shipDoc.TryGetProperty("status", out var shipSt) && shipSt.ValueKind != JsonValueKind.Null)
                        shippingStatus = shipSt.GetString();
                    if (shipDoc.TryGetProperty("substatus", out var shipSub) && shipSub.ValueKind != JsonValueKind.Null)
                        shippingSubstatus = shipSub.GetString();
                }
            }
            catch { /* ignore shipping fetch errors */ }
        }

        var items = order.GetProperty("order_items");
        int count = 0;

        foreach (var item in items.EnumerateArray())
        {
            var itemId = item.GetProperty("item").GetProperty("id").GetString() ?? "UNKNOWN";
            var itemTitle = item.GetProperty("item").GetProperty("title").GetString() ?? "Sin titulo";
            var quantity = item.GetProperty("quantity").GetInt32();
            var unitPrice = item.GetProperty("unit_price").GetDecimal();
            decimal? fullUnitPrice = item.TryGetProperty("full_unit_price", out var fup)
                && fup.ValueKind != JsonValueKind.Null
                ? fup.GetDecimal() : null;

            var existing = await _db.MeliOrders.FirstOrDefaultAsync(
                o => o.MeliOrderId == meliOrderId && o.ItemId == itemId);

            if (existing is not null)
            {
                existing.Status = status;
                existing.DateClosed = dateClosed;
                existing.TotalAmount = totalAmount;
                existing.BuyerNickname = buyerNickname;
                existing.Quantity = quantity;
                existing.UnitPrice = unitPrice;
                existing.FullUnitPrice = fullUnitPrice;
                existing.ShippingId = shippingId;
                existing.PackId = packId;
                existing.ShippingStatus = shippingStatus;
                existing.ShippingSubstatus = shippingSubstatus;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                _db.MeliOrders.Add(new MeliOrder
                {
                    MeliOrderId = meliOrderId,
                    MeliAccountId = accountId,
                    Status = status,
                    DateCreated = dateCreated,
                    DateClosed = dateClosed,
                    TotalAmount = totalAmount,
                    CurrencyId = currencyId,
                    BuyerId = buyerId,
                    BuyerNickname = buyerNickname,
                    ItemId = itemId,
                    ItemTitle = itemTitle,
                    Quantity = quantity,
                    UnitPrice = unitPrice,
                    FullUnitPrice = fullUnitPrice,
                    ShippingId = shippingId,
                    PackId = packId,
                    ShippingStatus = shippingStatus,
                    ShippingSubstatus = shippingSubstatus
                });
            }
            count++;
        }

        return count;
    }
}
