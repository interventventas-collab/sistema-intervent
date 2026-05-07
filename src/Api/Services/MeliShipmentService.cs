using System.Net.Http.Headers;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Sincroniza envios de MeLi (Flex y otros) para alimentar el modulo de mapeo.
/// Para cada cuenta conectada, trae las ordenes pagas recientes y descarga el shipment de cada una.
/// </summary>
public class MeliShipmentService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;

    public MeliShipmentService(AppDbContext db, IHttpClientFactory httpFactory, MeliAccountService accountService)
    {
        _db = db; _httpFactory = httpFactory; _accountService = accountService;
    }

    public async Task<MeliShipmentSyncResult> SyncFlexAsync(int daysBack = 7, int maxOrdersPerAccount = 200)
    {
        var accounts = await _accountService.GetAllAccountEntitiesAsync();
        int totalSynced = 0, totalFlex = 0, totalErrors = 0;
        var errors = new List<string>();
        foreach (var account in accounts)
        {
            try
            {
                var token = await _accountService.GetValidTokenAsync(account);
                if (token is null) { errors.Add($"Token expirado: {account.Nickname}"); totalErrors++; continue; }
                var (s, f) = await SyncForAccountAsync(account, token, daysBack, maxOrdersPerAccount);
                totalSynced += s; totalFlex += f;
            }
            catch (Exception ex) { errors.Add($"{account.Nickname}: {ex.Message}"); totalErrors++; }
        }
        return new MeliShipmentSyncResult(totalSynced, totalFlex, totalErrors, errors);
    }

    private async Task<(int synced, int flex)> SyncForAccountAsync(MeliAccount account, string token, int daysBack, int maxOrders)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var fromIso = DateTime.UtcNow.AddDays(-daysBack).ToString("yyyy-MM-ddTHH:mm:ss.000-00:00");
        var toIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.000-00:00");

        int offset = 0; int limit = 50; int loaded = 0; int synced = 0; int flex = 0;
        var seenShipmentIds = new HashSet<long>();

        while (loaded < maxOrders)
        {
            var url = $"https://api.mercadolibre.com/orders/search" +
                $"?seller={account.MeliUserId}&order.status=paid&sort=date_desc" +
                $"&order.date_created.from={fromIso}&order.date_created.to={toIso}" +
                $"&offset={offset}&limit={limit}";
            var resp = await http.GetAsync(url);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var newTok = await _accountService.GetValidTokenAsync(account, forceRefresh: true);
                if (newTok is not null)
                {
                    token = newTok;
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    resp = await http.GetAsync(url);
                }
            }
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                throw new Exception($"orders/search ({(int)resp.StatusCode}): {body[..Math.Min(body.Length, 200)]}");
            }
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            var results = doc.GetProperty("results");
            int batchCount = 0;
            foreach (var order in results.EnumerateArray())
            {
                batchCount++; loaded++;
                long? shipId = null;
                if (order.TryGetProperty("shipping", out var sh) && sh.ValueKind == JsonValueKind.Object
                    && sh.TryGetProperty("id", out var sidEl) && sidEl.ValueKind == JsonValueKind.Number)
                    shipId = sidEl.GetInt64();
                if (!shipId.HasValue || !seenShipmentIds.Add(shipId.Value)) continue;

                long orderId = order.TryGetProperty("id", out var oid) && oid.ValueKind == JsonValueKind.Number ? oid.GetInt64() : 0;
                decimal? orderTotal = order.TryGetProperty("total_amount", out var ta) && ta.ValueKind == JsonValueKind.Number ? ta.GetDecimal() : null;
                string? buyerNickname = null;
                if (order.TryGetProperty("buyer", out var by) && by.ValueKind == JsonValueKind.Object
                    && by.TryGetProperty("nickname", out var bn) && bn.ValueKind == JsonValueKind.String)
                    buyerNickname = bn.GetString();

                // Resumen breve de items: "2x 1Kg Cafe ... + 1x ..."
                string? itemsSummary = null;
                if (order.TryGetProperty("order_items", out var oiArr) && oiArr.ValueKind == JsonValueKind.Array)
                {
                    var parts = new List<string>();
                    foreach (var oi in oiArr.EnumerateArray())
                    {
                        var qty = oi.TryGetProperty("quantity", out var q) ? q.GetInt32() : 0;
                        string? title = oi.TryGetProperty("item", out var it) && it.TryGetProperty("title", out var tt) ? tt.GetString() : null;
                        if (!string.IsNullOrEmpty(title)) parts.Add($"{qty}x {title}");
                    }
                    if (parts.Count > 0) itemsSummary = string.Join(" + ", parts);
                    if (itemsSummary is not null && itemsSummary.Length > 480) itemsSummary = itemsSummary[..480];
                }

                // Traer el shipment
                JsonElement shipDoc;
                try
                {
                    var sUrl = $"https://api.mercadolibre.com/shipments/{shipId.Value}";
                    var sResp = await http.GetAsync(sUrl);
                    if (!sResp.IsSuccessStatusCode) continue;
                    shipDoc = JsonDocument.Parse(await sResp.Content.ReadAsStringAsync()).RootElement;
                }
                catch { continue; }

                bool isFlex = shipDoc.TryGetProperty("logistic_type", out var lt) && lt.ValueKind == JsonValueKind.String && lt.GetString() == "self_service";
                if (!isFlex) continue;

                await UpsertShipmentAsync(account.Id, orderId, orderTotal, itemsSummary, buyerNickname, shipDoc);
                synced++; flex++;
            }
            await _db.SaveChangesAsync();
            if (batchCount < limit) break;
            offset += limit;
        }
        return (synced, flex);
    }

    private async Task UpsertShipmentAsync(int accountId, long orderId, decimal? orderTotal, string? itemsSummary, string? buyerNickname, JsonElement sh)
    {
        long sid = sh.GetProperty("id").GetInt64();
        var existing = await _db.MeliShipments.FirstOrDefaultAsync(x => x.MeliShipmentId == sid);
        bool isNew = existing is null;
        existing ??= new MeliShipment { MeliShipmentId = sid, MeliAccountId = accountId };
        existing.MeliOrderId = orderId == 0 ? null : orderId;
        if (orderTotal.HasValue) existing.OrderTotal = orderTotal;
        if (itemsSummary is not null) existing.ItemsSummary = itemsSummary;
        if (!string.IsNullOrEmpty(buyerNickname)) existing.BuyerNickname = buyerNickname;

        existing.Status = sh.TryGetProperty("status", out var st) ? st.GetString() : null;
        existing.Substatus = sh.TryGetProperty("substatus", out var sst) ? sst.GetString() : null;
        existing.LogisticType = sh.TryGetProperty("logistic_type", out var lt) ? lt.GetString() : null;
        existing.TrackingNumber = sh.TryGetProperty("tracking_number", out var tn) ? tn.GetString() : null;

        if (sh.TryGetProperty("receiver_address", out var ra) && ra.ValueKind == JsonValueKind.Object)
        {
            existing.ReceiverName = StrProp(ra, "receiver_name", 200);
            existing.ReceiverPhone = StrProp(ra, "receiver_phone", 50);
            existing.AddressLine = StrProp(ra, "address_line", 300);
            existing.StreetName = StrProp(ra, "street_name", 200);
            existing.StreetNumber = StrProp(ra, "street_number", 20);
            existing.Comment = StrProp(ra, "comment", 500);
            existing.ZipCode = StrProp(ra, "zip_code", 20);
            existing.GeolocationType = StrProp(ra, "geolocation_type", 50);
            existing.Latitude = DecProp(ra, "latitude");
            existing.Longitude = DecProp(ra, "longitude");
            if (ra.TryGetProperty("city", out var ci) && ci.TryGetProperty("name", out var cn)) existing.City = cn.GetString();
            if (ra.TryGetProperty("state", out var stt) && stt.TryGetProperty("name", out var stn)) existing.State = stn.GetString();
            if (ra.TryGetProperty("neighborhood", out var nh) && nh.TryGetProperty("name", out var nhn)) existing.Neighborhood = nhn.GetString();
        }

        if (sh.TryGetProperty("date_created", out var dc) && dc.ValueKind == JsonValueKind.String)
            existing.DateCreated = ParseUtc(dc.GetString());
        if (sh.TryGetProperty("status_history", out var hist) && hist.ValueKind == JsonValueKind.Object)
        {
            existing.DateReadyToShip = ParseHistDate(hist, "date_ready_to_ship");
            existing.DateShipped = ParseHistDate(hist, "date_shipped");
            existing.DateDelivered = ParseHistDate(hist, "date_delivered");
        }
        if (sh.TryGetProperty("shipping_option", out var so) && so.ValueKind == JsonValueKind.Object)
        {
            if (so.TryGetProperty("estimated_delivery_final", out var edf) && edf.ValueKind == JsonValueKind.Object
                && edf.TryGetProperty("date", out var edfd) && edfd.ValueKind == JsonValueKind.String)
                existing.EstimatedDeliveryFinal = ParseUtc(edfd.GetString());
            if (so.TryGetProperty("estimated_delivery_limit", out var edl) && edl.ValueKind == JsonValueKind.Object
                && edl.TryGetProperty("date", out var edld) && edld.ValueKind == JsonValueKind.String)
                existing.EstimatedDeliveryLimit = ParseUtc(edld.GetString());
        }

        existing.LastSyncedAt = DateTime.UtcNow;
        if (isNew) _db.MeliShipments.Add(existing);
    }

    private static string? StrProp(JsonElement el, string name, int maxLen)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return null;
        var s = p.GetString();
        if (s is not null && s.Length > maxLen) s = s[..maxLen];
        return s;
    }
    private static decimal? DecProp(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? (decimal?)p.GetDecimal() : null;
    private static DateTime? ParseUtc(string? s)
        => string.IsNullOrEmpty(s) ? null : DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
    private static DateTime? ParseHistDate(JsonElement hist, string name)
        => hist.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? ParseUtc(p.GetString()) : null;
}

public record MeliShipmentSyncResult(int TotalSynced, int TotalFlex, int TotalErrors, List<string> Errors);
