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
                var (s, f) = await SyncForAccountAsync(account, token, daysBack, maxOrdersPerAccount, modeFilter: "flex");
                totalSynced += s; totalFlex += f;

                // ======= REFRESH de pendientes =======
                // Volver a consultar a MeLi los shipments que tenemos en nuestra base con
                // estado NO terminal (no delivered/cancelled). Si MeLi ya los marcó como
                // delivered (porque el chofer los entregó por la app oficial de Flex), acá
                // se refleja sin esperar al siguiente sync de orders.
                try
                {
                    var refreshed = await RefreshPendingForAccountAsync(account, token);
                    totalSynced += refreshed;
                }
                catch (Exception exR) { errors.Add($"{account.Nickname} (refresh): {exR.Message}"); }
            }
            catch (Exception ex) { errors.Add($"{account.Nickname}: {ex.Message}"); totalErrors++; }
        }
        return new MeliShipmentSyncResult(totalSynced, totalFlex, totalErrors, errors);
    }

    /// <summary>
    /// Sincroniza envios ME1 (mode='me1') de las ultimas N dias. Igual flujo que SyncFlexAsync pero filtrando por mode en vez de logistic_type.
    /// </summary>
    public async Task<MeliShipmentSyncResult> SyncMe1Async(int daysBack = 30, int maxOrdersPerAccount = 300)
    {
        var accounts = await _accountService.GetAllAccountEntitiesAsync();
        int totalSynced = 0, totalMe1 = 0, totalErrors = 0;
        var errors = new List<string>();
        foreach (var account in accounts)
        {
            try
            {
                var token = await _accountService.GetValidTokenAsync(account);
                if (token is null) { errors.Add($"Token expirado: {account.Nickname}"); totalErrors++; continue; }
                var (s, f) = await SyncForAccountAsync(account, token, daysBack, maxOrdersPerAccount, modeFilter: "me1");
                totalSynced += s; totalMe1 += f;

                // Refresh estados de ME1 no-finales
                try
                {
                    var refreshed = await RefreshPendingForAccountAsync(account, token, me1Only: true);
                    totalSynced += refreshed;
                }
                catch (Exception exR) { errors.Add($"{account.Nickname} (refresh ME1): {exR.Message}"); }
            }
            catch (Exception ex) { errors.Add($"{account.Nickname}: {ex.Message}"); totalErrors++; }
        }
        return new MeliShipmentSyncResult(totalSynced, totalMe1, totalErrors, errors);
    }

    /// <summary>
    /// Recorre los shipments locales con estado no terminal y los re-consulta uno por uno a MeLi.
    /// Sirve para captar transiciones tipo "shipped → delivered" que pasaron en MeLi después del último sync.
    /// </summary>
    private async Task<int> RefreshPendingForAccountAsync(MeliAccount account, string token, bool me1Only = false)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var pendingQ = _db.MeliShipments
            .Where(s => s.MeliAccountId == account.Id
                     && s.Status != "delivered" && s.Status != "not_delivered"
                     && s.Status != "cancelled" && s.Status != null);
        if (me1Only) pendingQ = pendingQ.Where(s => s.Mode == "me1");
        else pendingQ = pendingQ.Where(s => s.LogisticType == "self_service");

        var pending = await pendingQ
            .Select(s => new { s.Id, s.MeliShipmentId, s.MeliOrderId, s.OrderTotal, s.ItemsSummary, s.BuyerNickname })
            .ToListAsync();

        int updated = 0;
        foreach (var p in pending)
        {
            try
            {
                var resp = await http.GetAsync($"https://api.mercadolibre.com/shipments/{p.MeliShipmentId}");
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    var newTok = await _accountService.GetValidTokenAsync(account, forceRefresh: true);
                    if (newTok is null) continue;
                    token = newTok;
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    resp = await http.GetAsync($"https://api.mercadolibre.com/shipments/{p.MeliShipmentId}");
                }
                if (!resp.IsSuccessStatusCode) continue;
                var body = await resp.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(body).RootElement;
                await UpsertShipmentAsync(account.Id, p.MeliOrderId ?? 0, p.OrderTotal, p.ItemsSummary, p.BuyerNickname, doc);
                updated++;
            }
            catch { /* tolerar */ }
        }
        await _db.SaveChangesAsync();
        return updated;
    }

    private async Task<(int synced, int flex)> SyncForAccountAsync(MeliAccount account, string token, int daysBack, int maxOrders, string modeFilter = "flex")
    {
        // modeFilter:
        //   "flex" → solo guarda logistic_type=self_service (envios Flex, el flujo original)
        //   "me1"  → solo guarda mode=me1 (envios manuales del vendedor)
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
                bool isMe1 = shipDoc.TryGetProperty("mode", out var mdEl) && mdEl.ValueKind == JsonValueKind.String && mdEl.GetString() == "me1";
                bool match = modeFilter == "me1" ? isMe1 : isFlex;
                if (!match) continue;

                await UpsertShipmentAsync(account.Id, orderId, orderTotal, itemsSummary, buyerNickname, shipDoc);
                synced++; flex++;
            }
            await _db.SaveChangesAsync();
            if (batchCount < limit) break;
            offset += limit;
        }

        // 2026-06-16: Para envios Flex, postear el link de Google Maps como nota interna en cada
        // orden de MeLi. Asi el repartidor abre la app de MeLi y tiene el link a Maps al toque.
        // Solo procesa shipments del rango (daysBack) con MapsNoteSentAt=null.
        if (modeFilter == "flex")
        {
            try { await PostPendingMapsNotesAsync(account, http, daysBack); }
            catch (Exception ex) { Console.WriteLine($"[MapsNote] error general account {account.Nickname}: {ex.Message}"); }
        }

        return (synced, flex);
    }

    /// <summary>2026-06-16: postea nota interna con link de Google Maps en cada orden Flex sincronizada.
    /// Filtra por: cuenta, logistic_type=self_service, MapsNoteSentAt null, hay MeliOrderId, DateCreated dentro de daysBack.</summary>
    private async Task PostPendingMapsNotesAsync(MeliAccount account, HttpClient http, int daysBack)
    {
        var cutoff = DateTime.UtcNow.AddDays(-daysBack);
        var pendientes = await _db.MeliShipments
            .Where(s => s.MeliAccountId == account.Id
                        && s.LogisticType == "self_service"
                        && s.MapsNoteSentAt == null
                        && s.MeliOrderId != null
                        && s.DateCreated >= cutoff)
            .ToListAsync();

        foreach (var sh in pendientes)
        {
            try
            {
                var noteText = BuildMapsNote(sh);
                if (string.IsNullOrEmpty(noteText)) continue;
                var body = JsonSerializer.Serialize(new { note = noteText });
                using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                var resp = await http.PostAsync($"https://api.mercadolibre.com/orders/{sh.MeliOrderId}/notes", content);
                if (resp.IsSuccessStatusCode)
                {
                    sh.MapsNoteSentAt = DateTime.UtcNow;
                }
                else
                {
                    var errBody = await resp.Content.ReadAsStringAsync();
                    Console.WriteLine($"[MapsNote] order {sh.MeliOrderId} {(int)resp.StatusCode}: {errBody[..Math.Min(errBody.Length, 200)]}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MapsNote] order {sh.MeliOrderId} exception: {ex.Message}");
            }
        }
        await _db.SaveChangesAsync();
    }

    /// <summary>2026-06-16 v2: arma el TEXTO de la nota — prefiere lat/lng (el repartidor copia,
    /// pega en el buscador de Google Maps y va al pin exacto). Si no hay coords, manda la
    /// direccion como texto (address_line + city). NO mandamos la URL completa porque al pegarla en
    /// el buscador de Maps, Maps la trata como query literal y falla. Coords es directo.</summary>
    private static string? BuildMapsNote(MeliShipment sh)
    {
        if (sh.Latitude.HasValue && sh.Longitude.HasValue
            && sh.Latitude.Value != 0m && sh.Longitude.Value != 0m)
        {
            var lat = sh.Latitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lng = sh.Longitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return $"Maps: {lat},{lng}";
        }

        var addr = sh.AddressLine;
        if (string.IsNullOrWhiteSpace(addr)) return null;
        var query = addr;
        if (!string.IsNullOrWhiteSpace(sh.City)) query += " " + sh.City;
        // El campo nota de MeLi tiene 120 chars. Recortamos para asegurar que entre con el "Maps: " prefijo.
        if (query.Length > 110) query = query.Substring(0, 110);
        return $"Maps: {query}";
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
        existing.Mode = sh.TryGetProperty("mode", out var md) ? md.GetString() : null;
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

    /// <summary>
    /// Sincroniza UN solo envio desde MeLi por su MeliShipmentId. Util cuando el usuario quiere operar sobre
    /// un envio que todavia no esta en la base local (ej: marcar entregado desde la pantalla de Ordenes
    /// sobre un envio que la sync masiva no alcanzo a traer).
    /// </summary>
    public async Task<bool> SyncSingleShipmentAsync(long meliShipmentId)
    {
        // Necesito la cuenta MeLi para autenticarme. Si el envio ya existe local, uso esa cuenta;
        // si no, busco si la orden esta en MeliOrders para inferir la cuenta. Si tampoco, uso la primera cuenta.
        MeliAccount? account = null;
        var existing = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == meliShipmentId);
        if (existing is not null)
            account = await _db.MeliAccounts.FirstOrDefaultAsync(a => a.Id == existing.MeliAccountId);

        if (account is null)
        {
            // Buscar por orden
            var ord = await _db.MeliOrders.FirstOrDefaultAsync(o => o.ShippingId == meliShipmentId);
            if (ord is not null)
                account = await _db.MeliAccounts.FirstOrDefaultAsync(a => a.Id == ord.MeliAccountId);
        }
        if (account is null)
            account = (await _accountService.GetAllAccountEntitiesAsync()).FirstOrDefault();
        if (account is null) return false;

        var token = await _accountService.GetValidTokenAsync(account);
        if (token is null) return false;

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await http.GetAsync($"https://api.mercadolibre.com/shipments/{meliShipmentId}");
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            var newTok = await _accountService.GetValidTokenAsync(account, forceRefresh: true);
            if (newTok is null) return false;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newTok);
            resp = await http.GetAsync($"https://api.mercadolibre.com/shipments/{meliShipmentId}");
        }
        if (!resp.IsSuccessStatusCode) return false;

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        // Buscar info de la orden asociada (para items summary, total, buyer)
        long? orderId = existing?.MeliOrderId;
        decimal? orderTotal = existing?.OrderTotal;
        string? itemsSummary = existing?.ItemsSummary;
        string? buyerNickname = existing?.BuyerNickname;
        if (orderId is null)
        {
            var ord2 = await _db.MeliOrders.FirstOrDefaultAsync(o => o.ShippingId == meliShipmentId);
            if (ord2 is not null)
            {
                orderId = ord2.MeliOrderId;
                orderTotal = ord2.TotalAmount;
                itemsSummary = $"{ord2.Quantity}x {ord2.ItemTitle}";
                buyerNickname = ord2.BuyerNickname;
            }
        }

        await UpsertShipmentAsync(account.Id, orderId ?? 0, orderTotal, itemsSummary, buyerNickname, doc);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>2026-06-08: importar manualmente un envío me1 desde el número de ORDEN MeLi.
    /// Recibe el MeliOrderId (string, ej "2000016778633314"), itera todas las cuentas MeLi
    /// activas hasta encontrar la que tiene esa orden, extrae el shipping.id y llama a
    /// SyncSingleShipmentAsync para traerla. Útil cuando el sync regular no la trae por filtros.
    /// Devuelve (ok, mensaje) para mostrar al usuario.</summary>
    public async Task<(bool ok, string mensaje, long? shipmentId)> ImportByOrderIdAsync(string orderIdStr)
    {
        if (string.IsNullOrWhiteSpace(orderIdStr)) return (false, "Ingresá un número de orden", null);
        orderIdStr = orderIdStr.Trim();
        if (!long.TryParse(orderIdStr, out var orderIdLong))
            return (false, "El número de orden debe ser numérico", null);

        // Primero: ¿está en MeliOrders? Si sí, ya sé qué cuenta usar y su ShippingId.
        var localOrder = await _db.MeliOrders.FirstOrDefaultAsync(o => o.MeliOrderId == orderIdLong);
        long? shippingIdToImport = localOrder?.ShippingId;
        int? accountIdHint = localOrder?.MeliAccountId;

        // Si no está local o no tiene ShippingId guardado, consulto la API MeLi
        if (!shippingIdToImport.HasValue)
        {
            var accounts = await _accountService.GetAllAccountEntitiesAsync();
            if (accounts.Count == 0) return (false, "No hay cuentas MeLi conectadas", null);

            // Si hay un hint de cuenta (del MeliOrders), arranco por esa
            if (accountIdHint.HasValue)
            {
                var hinted = accounts.FirstOrDefault(a => a.Id == accountIdHint.Value);
                if (hinted is not null) accounts = new List<MeliAccount> { hinted }.Concat(accounts.Where(a => a.Id != hinted.Id)).ToList();
            }

            foreach (var acc in accounts)
            {
                var tok = await _accountService.GetValidTokenAsync(acc);
                if (tok is null) continue;
                var http = _httpFactory.CreateClient();
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok);
                var orderResp = await http.GetAsync($"https://api.mercadolibre.com/orders/{orderIdLong}");
                if (orderResp.StatusCode == System.Net.HttpStatusCode.Unauthorized || orderResp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    var freshTok = await _accountService.GetValidTokenAsync(acc, forceRefresh: true);
                    if (freshTok is null) continue;
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", freshTok);
                    orderResp = await http.GetAsync($"https://api.mercadolibre.com/orders/{orderIdLong}");
                }
                if (!orderResp.IsSuccessStatusCode) continue;   // no es de esta cuenta, probar próxima
                var orderDoc = JsonDocument.Parse(await orderResp.Content.ReadAsStringAsync()).RootElement;
                if (orderDoc.TryGetProperty("shipping", out var shp) && shp.ValueKind == JsonValueKind.Object
                    && shp.TryGetProperty("id", out var sid) && sid.ValueKind == JsonValueKind.Number)
                {
                    shippingIdToImport = sid.GetInt64();
                    break;
                }
                return (false, $"La orden {orderIdStr} existe en MeLi pero no tiene shipping asociado (puede ser retiro en persona).", null);
            }

            if (!shippingIdToImport.HasValue)
                return (false, $"No encontré la orden {orderIdStr} en ninguna de tus cuentas MeLi. Verificá el número.", null);
        }

        // Listo el shippingId — uso el sync de 1 que ya existe
        var ok = await SyncSingleShipmentAsync(shippingIdToImport.Value);
        if (!ok) return (false, $"Encontré la orden pero falló al traer el envío (shipping_id={shippingIdToImport}).", shippingIdToImport);
        return (true, $"✅ Envío importado (shipping_id={shippingIdToImport}). Refrescá la lista.", shippingIdToImport);
    }

    /// <summary>
    /// Service IDs por sitio para identificar el envio ME1 en MeLi al marcar el tracking.
    /// Tabla copiada de la doc oficial de developers.mercadolibre.com.ar.
    /// </summary>
    private static int? ServiceIdForSite(string? siteId) => siteId?.ToUpperInvariant() switch
    {
        "MLA" => 154,
        "MLB" => 11,
        "MLM" => 231876,
        "MLC" => 282578,
        "MCO" => 282579,
        "MLU" => 282604,
        "MPE" => 361180,
        _ => null
    };

    /// <summary>
    /// Marca un envio ME1 con el estado pedido por el vendedor.
    /// Endpoint MeLi: POST /shipments/{id}/seller_notifications
    /// Combinaciones validas (status, substatus):
    ///   shipped/null         → Despachado
    ///   shipped/out_for_delivery → Salio a entregar
    ///   delivered/null       → Entregado al comprador (FINAL, no reversible)
    ///   not_delivered/returning_to_sender → No entregado (FINAL, no reversible)
    /// Retorna (ok, errorMessage). En caso de exito, refresca el shipment local.
    /// </summary>
    public async Task<(bool ok, string? error)> SetMe1StatusAsync(int shipmentId, string status, string? substatus, string? trackingNumber, string? trackingUrl, string? comment)
    {
        var ship = await _db.MeliShipments.FirstOrDefaultAsync(s => s.Id == shipmentId);
        if (ship is null) return (false, "Envio no encontrado");
        if (ship.Mode != "me1") return (false, "Este envio no es ME1, no se puede operar desde aca");

        var account = await _db.MeliAccounts.FirstOrDefaultAsync(a => a.Id == ship.MeliAccountId);
        if (account is null) return (false, "Cuenta MeLi del envio no encontrada");
        var token = await _accountService.GetValidTokenAsync(account);
        if (token is null) return (false, "No se pudo obtener token de MeLi");

        // Site ID: lo derivamos del MeliUserId via cuenta. Por simplicidad, usamos MLA (Argentina) por default;
        // si la app maneja multipais, leerlo de account.SiteId u otro campo.
        var siteId = "MLA";
        var serviceId = ServiceIdForSite(siteId);

        // Construir payload
        var payload = new Dictionary<string, object?>
        {
            ["payload"] = new Dictionary<string, object?>
            {
                ["service_id"] = serviceId,
                ["comment"] = string.IsNullOrWhiteSpace(comment) ? status : comment,
                ["date"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz")
            },
            ["status"] = status,
            ["substatus"] = string.IsNullOrEmpty(substatus) ? "null" : substatus
        };
        if (!string.IsNullOrWhiteSpace(trackingNumber)) payload["tracking_number"] = trackingNumber;
        if (!string.IsNullOrWhiteSpace(trackingUrl)) payload["tracking_url"] = trackingUrl;

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var url = $"https://api.mercadolibre.com/shipments/{ship.MeliShipmentId}/seller_notifications";
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var resp = await http.PostAsync(url, content);

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            var newTok = await _accountService.GetValidTokenAsync(account, forceRefresh: true);
            if (newTok is not null)
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newTok);
                using var content2 = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                resp = await http.PostAsync(url, content2);
            }
        }

        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            return (false, $"MeLi rechazo el cambio ({(int)resp.StatusCode}): {body[..Math.Min(body.Length, 400)]}");
        }

        // 2026-06-17: MeLi acepto el cambio. Actualizamos AHORA mismo el ship local con el nuevo estado
        // para que el listado de /meli/me1/entregas lo refleje al instante. Antes intentabamos refrescar
        // leyendo MeLi de nuevo, pero por la eventual consistency de su API el GET podia devolver el estado
        // VIEJO (1-2 segundos despues del POST) y se sobrescribia el cambio. Ahora el seteo es autoritativo.
        ship.Status = status;
        ship.Substatus = substatus;
        ship.LastSyncedAt = DateTime.UtcNow;
        if (status == "delivered") ship.DateDelivered ??= DateTime.UtcNow;
        if (status == "shipped" && substatus is null) ship.DateShipped ??= DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(trackingNumber)) ship.TrackingNumber = trackingNumber;
        await _db.SaveChangesAsync();

        // Best-effort: refrescar desde MeLi para traer fechas/data adicional. Si MeLi ya tiene el nuevo
        // estado, hacemos el upsert completo (que traera DateDelivered exacto, EstimatedDelivery, etc).
        // Si MeLi todavia tiene el estado viejo (eventual consistency), NO sobrescribimos lo que ya seteamos.
        try
        {
            var sUrl = $"https://api.mercadolibre.com/shipments/{ship.MeliShipmentId}";
            var sResp = await http.GetAsync(sUrl);
            if (sResp.IsSuccessStatusCode)
            {
                var doc = JsonDocument.Parse(await sResp.Content.ReadAsStringAsync()).RootElement;
                var meliStatus = doc.TryGetProperty("status", out var ms) ? ms.GetString() : null;
                if (string.Equals(meliStatus, status, StringComparison.OrdinalIgnoreCase))
                {
                    await UpsertShipmentAsync(account.Id, ship.MeliOrderId ?? 0, ship.OrderTotal, ship.ItemsSummary, ship.BuyerNickname, doc);
                    await _db.SaveChangesAsync();
                }
            }
        }
        catch { /* el cambio en MeLi ya quedo y el local ya esta actualizado, no fallar por error de refresh */ }

        return (true, null);
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
