using System.Net.Http.Headers;
using System.Text.Json;
using Api.Data;
using Api.Services;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Controllers;

/// <summary>
/// Controlador para el modulo "me1" del sidebar.
/// Maneja los envios manuales (mode='me1' en MeLi): listar, sincronizar y marcar estado.
/// El estado se cambia llamando al endpoint POST /shipments/{id}/seller_notifications de MeLi.
/// </summary>
[ApiController]
[Route("api/meli/me1")]
[Authorize]
public class MeliMe1Controller : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly MeliShipmentService _service;
    private readonly AuditLogService _audit;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly IMemoryCache _cache;

    public MeliMe1Controller(AppDbContext db, MeliShipmentService service, AuditLogService audit,
        IHttpClientFactory httpFactory, MeliAccountService accountService, IMemoryCache cache)
    {
        _db = db; _service = service; _audit = audit;
        _httpFactory = httpFactory; _accountService = accountService; _cache = cache;
    }

    /// <summary>Lista los envios ME1 cargados localmente, mas recientes primero.</summary>
    [HttpGet("shipments")]
    public async Task<IActionResult> ListShipments(
        [FromQuery] string? filter = "todos",
        [FromQuery] int take = 500)
    {
        // filter: todos | pendientes | entregados | no_entregados
        var q = _db.MeliShipments
            .Include(s => s.MeliAccount)
            .Where(s => s.Mode == "me1");

        switch ((filter ?? "todos").ToLowerInvariant())
        {
            case "pendientes":
                q = q.Where(s => s.Status != "delivered" && s.Status != "not_delivered" && s.Status != "cancelled");
                break;
            case "entregados":
                q = q.Where(s => s.Status == "delivered");
                break;
            case "no_entregados":
                q = q.Where(s => s.Status == "not_delivered");
                break;
            case "todos":
            default:
                break;
        }

        var list = await q
            .OrderByDescending(s => s.DateCreated ?? s.LastSyncedAt)
            .Take(take)
            .ToListAsync();

        // 2026-06-17: traer nombres de repartidores asignados / que entregaron en un solo lookup.
        var repartidorIds = list
            .SelectMany(s => new[] { s.RepartidorAsignadoId, s.EntregadoPorRepartidorId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var repartidores = repartidorIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.CafeRepartidores
                .Where(r => repartidorIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.Nombre);

        return Ok(list.Select(s => new
        {
            id = s.Id,
            meliShipmentId = s.MeliShipmentId,
            meliOrderId = s.MeliOrderId,
            cuenta = s.MeliAccount != null ? s.MeliAccount.Nickname : null,
            status = s.Status,
            substatus = s.Substatus,
            mode = s.Mode,
            trackingNumber = s.TrackingNumber,
            receiverName = s.ReceiverName,
            receiverPhone = s.ReceiverPhone,
            buyerNickname = s.BuyerNickname,
            addressLine = s.AddressLine,
            neighborhood = s.Neighborhood,
            city = s.City,
            state = s.State,
            zipCode = s.ZipCode,
            comment = s.Comment,
            itemsSummary = s.ItemsSummary,
            orderTotal = s.OrderTotal,
            dateCreated = s.DateCreated,
            dateShipped = s.DateShipped,
            dateDelivered = s.DateDelivered,
            estimatedDeliveryFinal = s.EstimatedDeliveryFinal,
            estimatedDeliveryLimit = s.EstimatedDeliveryLimit,
            lastSyncedAt = s.LastSyncedAt,
            // 2026-06-17: campos nuevos para asignacion y registro de entrega por repartidor.
            repartidorAsignadoId = s.RepartidorAsignadoId,
            repartidorAsignadoNombre = s.RepartidorAsignadoId.HasValue && repartidores.TryGetValue(s.RepartidorAsignadoId.Value, out var nA) ? nA : null,
            entregadoPorRepartidorId = s.EntregadoPorRepartidorId,
            entregadoPorRepartidorNombre = s.EntregadoPorRepartidorId.HasValue && repartidores.TryGetValue(s.EntregadoPorRepartidorId.Value, out var nE) ? nE : null,
            entregadoPorRepartidorAt = s.EntregadoPorRepartidorAt
        }));
    }

    public record SyncMe1Request(int Days = 45, int MaxOrders = 300);

    /// <summary>Trae los envios ME1 y los guarda localmente.
    /// 2026-07-08: usa SyncMe1FromOrdersAsync (basado en la tabla local de ordenes), que revisa
    /// TODAS las ventas ME1 del rango sin el viejo tope de 300 ventas escaneadas. Con ~90-100
    /// ventas/dia, el metodo viejo (SyncMe1Async) solo cubria ~3 dias reales.</summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromBody] SyncMe1Request? req)
    {
        var r = await _service.SyncMe1FromOrdersAsync(req?.Days ?? 45);
        return Ok(new { totalSynced = r.TotalSynced, totalMe1 = r.TotalFlex, totalErrors = r.TotalErrors, errores = r.Errors });
    }

    public record ImportByOrderIdRequest(string OrderId);

    /// <summary>2026-06-08: traer un envio puntual a partir del numero de ORDEN MeLi.
    /// Útil cuando el sync masivo no la trajo por algún filtro. Itera las cuentas hasta encontrarla.</summary>
    [HttpPost("import-by-order")]
    public async Task<IActionResult> ImportByOrder([FromBody] ImportByOrderIdRequest req)
    {
        var (ok, mensaje, shipmentId) = await _service.ImportByOrderIdAsync(req?.OrderId ?? "");
        if (!ok) return BadRequest(new { error = mensaje, shipmentId });
        return Ok(new { mensaje, shipmentId });
    }

    public record SetStatusRequest(string Status, string? Substatus, string? TrackingNumber, string? TrackingUrl, string? Comment);

    /// <summary>
    /// Variante de SetStatus que recibe el MeliShipmentId (numero largo que devuelve MeLi) en vez del Id interno.
    /// Si el envio no esta en la base local, lo sincroniza desde MeLi primero. Util cuando el usuario quiere
    /// marcar como entregado un envio desde la pantalla de Ordenes (que no necesariamente esta en MeliShipments).
    /// </summary>
    [HttpPost("by-meli-id/{meliShipmentId:long}/status")]
    public async Task<IActionResult> SetStatusByMeliId(long meliShipmentId, [FromBody] SetStatusRequest req)
    {
        var ship = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == meliShipmentId);
        if (ship is null)
        {
            // No esta local — sincronizar desde MeLi
            var synced = await _service.SyncSingleShipmentAsync(meliShipmentId);
            if (!synced) return BadRequest(new { error = "No se pudo sincronizar el envio desde MeLi" });
            ship = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == meliShipmentId);
            if (ship is null) return BadRequest(new { error = "Envio no encontrado tras sincronizar" });
        }
        return await SetStatus(ship.Id, req);
    }

    /// <summary>
    /// Cambia el estado del envio ME1 en MeLi.
    /// Status validos:
    ///   - shipped + substatus=null            → Despachado (reversible)
    ///   - shipped + substatus=out_for_delivery → Salio a entregar (reversible)
    ///   - delivered + substatus=null          → Entregado al comprador (FINAL)
    ///   - not_delivered + substatus=returning_to_sender → No entregado (FINAL)
    /// </summary>
    [HttpPost("shipments/{id:int}/status")]
    public async Task<IActionResult> SetStatus(int id, [FromBody] SetStatusRequest req)
    {
        // Validacion: solo aceptamos las 4 combinaciones documentadas por MeLi
        var status = (req.Status ?? "").Trim().ToLowerInvariant();
        var substatus = string.IsNullOrWhiteSpace(req.Substatus) || req.Substatus == "null" ? null : req.Substatus.Trim().ToLowerInvariant();

        bool valid =
            (status == "shipped" && substatus is null) ||
            (status == "shipped" && substatus == "out_for_delivery") ||
            (status == "delivered" && substatus is null) ||
            (status == "not_delivered" && substatus == "returning_to_sender");

        if (!valid)
            return BadRequest(new { error = "Combinacion status/substatus no soportada por MeLi" });

        var ship = await _db.MeliShipments.FirstOrDefaultAsync(s => s.Id == id);
        if (ship is null) return NotFound(new { error = "Envio no encontrado" });

        var prevStatus = ship.Status;
        var prevSubstatus = ship.Substatus;

        var (ok, error) = await _service.SetMe1StatusAsync(id, status, substatus, req.TrackingNumber, req.TrackingUrl, req.Comment);
        if (!ok) return BadRequest(new { error });

        // Log de auditoria: quien cambio que estado en que envio
        var changes = $"de '{prevStatus}/{prevSubstatus ?? "null"}' a '{status}/{substatus ?? "null"}'";
        await _audit.LogAsync("MeliShipment.ME1", ship.MeliShipmentId.ToString(), "set_status", changes);

        return Ok(new { ok = true });
    }

    // ============================================================
    // 2026-06-17: ASIGNACION DE REPARTIDOR a envios ME1
    // Para que el repartidor vea las ME1 que le tocan en su celu (/mis-pedidos).
    // ============================================================

    public record AsignarRepartidorRequest(int? RepartidorId);

    /// <summary>Asigna (o desasigna con null) un repartidor a un envio ME1.</summary>
    [HttpPost("shipments/{id:int}/asignar-repartidor")]
    public async Task<IActionResult> AsignarRepartidor(int id, [FromBody] AsignarRepartidorRequest req)
    {
        var ship = await _db.MeliShipments.FirstOrDefaultAsync(s => s.Id == id);
        if (ship is null) return NotFound(new { error = "Envio no encontrado" });

        if (req.RepartidorId.HasValue)
        {
            var repExiste = await _db.CafeRepartidores.AnyAsync(r => r.Id == req.RepartidorId.Value && r.IsActive);
            if (!repExiste) return BadRequest(new { error = "Repartidor no encontrado o inactivo" });
        }

        var prev = ship.RepartidorAsignadoId;
        ship.RepartidorAsignadoId = req.RepartidorId;
        await _db.SaveChangesAsync();

        await _audit.LogAsync("MeliShipment.ME1", ship.MeliShipmentId.ToString(),
            "asignar_repartidor", $"de '{prev?.ToString() ?? "null"}' a '{req.RepartidorId?.ToString() ?? "null"}'");

        return Ok(new { ok = true, repartidorId = req.RepartidorId });
    }

    // ============================================================
    // GESTION DE PUBLICACIONES ME1 — pantalla /meli/me1/publicaciones
    // ============================================================

    /// <summary>
    /// Lista todas las publicaciones ACTIVAS con shipping_mode=me1 de todas las cuentas.
    /// Trae IDs desde MeLi (paginado) y los enriquece con info local de MeliItems si la tenemos.
    /// Cachea 5 min para no spamear MeLi.
    /// </summary>
    [HttpGet("publicaciones")]
    public async Task<IActionResult> ListPublicaciones([FromQuery] bool refrescar = false)
    {
        const string cacheKey = "me1:publicaciones:listado:v3_pesoFix";
        if (!refrescar && _cache.TryGetValue(cacheKey, out var cached))
            return Ok(cached);

        var accounts = await _accountService.GetAllAccountEntitiesAsync();
        var resultado = new List<object>();
        var http = _httpFactory.CreateClient();

        foreach (var account in accounts)
        {
            var token = await _accountService.GetValidTokenAsync(account);
            if (token is null) continue;

            // 1) Bajar todos los MLAs activos con shipping_mode=me1 de esta cuenta (paginado de 50 en 50)
            var mlaIds = new List<string>();
            int offset = 0; int total = -1;
            while (offset < 5000)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.mercadolibre.com/users/{account.MeliUserId}/items/search?status=active&shipping_mode=me1&limit=50&offset={offset}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) break;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("paging", out var pg) && pg.TryGetProperty("total", out var t))
                    total = t.GetInt32();
                if (doc.RootElement.TryGetProperty("results", out var results))
                {
                    foreach (var r in results.EnumerateArray()) mlaIds.Add(r.GetString() ?? "");
                }
                offset += 50;
                if (total > 0 && offset >= total) break;
            }

            // 2) Cruzar con MeliItems para enriquecer (foto, precio, stock, sku, title)
            var ids = mlaIds.Where(s => !string.IsNullOrEmpty(s)).ToList();
            var locales = await _db.MeliItems
                .Where(mi => ids.Contains(mi.MeliItemId))
                .Select(mi => new { mi.MeliItemId, mi.Title, mi.Sku, mi.Price, mi.AvailableQuantity, mi.SoldQuantity, mi.Thumbnail, mi.Permalink, mi.Status })
                .ToDictionaryAsync(x => x.MeliItemId);

            // 3) Multi-get a MeLi para obtener el PESO (SELLER_PACKAGE_WEIGHT) en gramos
            // Batches de 20. La base no tiene este dato, lo trae solo MeLi.
            var pesos = new Dictionary<string, int?>(); // mla -> gramos
            for (int i = 0; i < ids.Count; i += 20)
            {
                var batch = ids.Skip(i).Take(20).ToList();
                var url = $"https://api.mercadolibre.com/items?ids={string.Join(",", batch)}&attributes=id,attributes";
                using var req2 = new HttpRequestMessage(HttpMethod.Get, url);
                req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp2 = await http.SendAsync(req2);
                if (!resp2.IsSuccessStatusCode) continue;
                using var doc2 = JsonDocument.Parse(await resp2.Content.ReadAsStringAsync());
                foreach (var el in doc2.RootElement.EnumerateArray())
                {
                    if (!el.TryGetProperty("body", out var body)) continue;
                    if (!body.TryGetProperty("id", out var idEl)) continue;
                    var mla = idEl.GetString() ?? "";
                    int? peso = null;
                    if (body.TryGetProperty("attributes", out var attrs))
                    {
                        foreach (var a in attrs.EnumerateArray())
                        {
                            if (!a.TryGetProperty("id", out var aid) || aid.GetString() != "SELLER_PACKAGE_WEIGHT")
                                continue;

                            // El struct puede venir en value_struct (item viejo) o en values[0].struct (item nuevo)
                            JsonElement structEl = default; bool tieneStruct = false;
                            if (a.TryGetProperty("value_struct", out var vs1) && vs1.ValueKind == JsonValueKind.Object)
                            { structEl = vs1; tieneStruct = true; }
                            else if (a.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array && vals.GetArrayLength() > 0)
                            {
                                var first = vals[0];
                                if (first.TryGetProperty("struct", out var vs2) && vs2.ValueKind == JsonValueKind.Object)
                                { structEl = vs2; tieneStruct = true; }
                            }
                            if (tieneStruct && structEl.TryGetProperty("number", out var n))
                            {
                                var num = n.GetDouble();
                                var unit = structEl.TryGetProperty("unit", out var u) ? (u.GetString() ?? "g") : "g";
                                peso = unit.ToLowerInvariant() switch {
                                    "kg" => (int)(num * 1000),
                                    "g" => (int)num,
                                    _ => (int)num
                                };
                            }
                            else if (a.TryGetProperty("value_name", out var vn) && vn.ValueKind == JsonValueKind.String)
                            {
                                // Fallback: parsear "300000 g" o "10 kg"
                                var txt = vn.GetString() ?? "";
                                var m = System.Text.RegularExpressions.Regex.Match(txt, @"([\d.,]+)\s*(g|kg)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (m.Success && double.TryParse(m.Groups[1].Value.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var num))
                                {
                                    var unit = m.Groups[2].Success ? m.Groups[2].Value.ToLowerInvariant() : "g";
                                    peso = unit == "kg" ? (int)(num * 1000) : (int)num;
                                }
                            }
                            break;
                        }
                    }
                    pesos[mla] = peso;
                }
            }

            foreach (var mla in ids)
            {
                pesos.TryGetValue(mla, out var pesoGr);
                if (locales.TryGetValue(mla, out var l))
                {
                    resultado.Add(new {
                        mla = mla,
                        cuenta = account.Nickname,
                        title = l.Title,
                        sku = l.Sku,
                        price = l.Price,
                        stock = l.AvailableQuantity,
                        sold = l.SoldQuantity,
                        thumbnail = l.Thumbnail,
                        permalink = l.Permalink,
                        status = l.Status,
                        pesoGr = pesoGr,
                        enBase = true
                    });
                }
                else
                {
                    resultado.Add(new { mla = mla, cuenta = account.Nickname, pesoGr = pesoGr, enBase = false });
                }
            }
        }

        var payload = new { total = resultado.Count, items = resultado };
        _cache.Set(cacheKey, payload, TimeSpan.FromMinutes(5));
        return Ok(payload);
    }

    public record EditarPesoRequest(double Kg);

    /// <summary>
    /// 2026-06-09: cambia el peso (SELLER_PACKAGE_WEIGHT) de una publicación.
    /// El formato funcional descubierto en pruebas: values[{name, struct{number,unit}}].
    /// MeLi a veces ignora value_struct/value_name a nivel raíz — values[] es lo que
    /// efectivamente persiste. Verificado con MLA3402774212.
    /// </summary>
    [HttpPut("publicaciones/{mla}/peso")]
    public async Task<IActionResult> EditarPeso(string mla, [FromBody] EditarPesoRequest req)
    {
        if (string.IsNullOrWhiteSpace(mla)) return BadRequest(new { error = "MLA vacío" });
        if (req.Kg <= 0 || req.Kg > 9999) return BadRequest(new { error = "Peso fuera de rango (0-9999 kg)" });

        // Convertir a gramos (MeLi guarda en g)
        var gramos = (int)Math.Round(req.Kg * 1000);

        // Buscar a qué cuenta pertenece para usar su token
        var item = await _db.MeliItems.FirstOrDefaultAsync(mi => mi.MeliItemId == mla);
        var accounts = item != null
            ? (await _accountService.GetAllAccountEntitiesAsync()).Where(a => a.Id == item.MeliAccountId).ToList()
            : await _accountService.GetAllAccountEntitiesAsync();

        string? token = null;
        foreach (var a in accounts) { token = await _accountService.GetValidTokenAsync(a); if (token is not null) break; }
        if (token is null) return BadRequest(new { error = "Sin token MeLi" });

        var http = _httpFactory.CreateClient();
        var payload = $"{{\"attributes\":[{{\"id\":\"SELLER_PACKAGE_WEIGHT\",\"values\":[{{\"name\":\"{gramos} g\",\"struct\":{{\"number\":{gramos},\"unit\":\"g\"}}}}]}}]}}";

        using var httpReq = new HttpRequestMessage(HttpMethod.Put, $"https://api.mercadolibre.com/items/{mla}")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        };
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(httpReq);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            return BadRequest(new { error = $"MeLi HTTP {(int)resp.StatusCode}", detalle = body.Substring(0, Math.Min(400, body.Length)) });

        // Esperar un toque y verificar releyendo el peso
        await Task.Delay(2000);
        using var verReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.mercadolibre.com/items/{mla}?attributes=attributes");
        verReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var verResp = await http.SendAsync(verReq);
        int? pesoConfirmado = null;
        if (verResp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await verResp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("attributes", out var attrs))
            {
                foreach (var a in attrs.EnumerateArray())
                {
                    if (!a.TryGetProperty("id", out var aid) || aid.GetString() != "SELLER_PACKAGE_WEIGHT") continue;
                    if (a.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array && vals.GetArrayLength() > 0)
                    {
                        var first = vals[0];
                        if (first.TryGetProperty("struct", out var st) && st.TryGetProperty("number", out var n))
                            pesoConfirmado = (int)n.GetDouble();
                    }
                    break;
                }
            }
        }

        // Invalidar cache de /publicaciones para que el siguiente Refrescar muestre el nuevo peso
        _cache.Remove("me1:publicaciones:listado:v3_pesoFix");

        await _audit.LogAsync("MeliItem.ME1", mla, "set_peso", $"a {gramos} g (pedido: {req.Kg} kg) — confirmado: {pesoConfirmado ?? -1} g");

        return Ok(new {
            ok = true,
            mla,
            pesoSolicitadoGr = gramos,
            pesoConfirmadoGr = pesoConfirmado,
            persistido = pesoConfirmado == gramos
        });
    }

    public record CotizarRequest(string Mla, string Cp);

    /// <summary>
    /// Cotiza el envio de una publicacion a un CP. Llama a MeLi en vivo.
    /// Util para testear cuanto cobra MeLi a un CP especifico despues de cambios en la tabla Axado.
    /// </summary>
    [HttpPost("cotizar")]
    public async Task<IActionResult> Cotizar([FromBody] CotizarRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Mla) || string.IsNullOrWhiteSpace(req.Cp))
            return BadRequest(new { error = "Falta MLA o CP" });

        // Buscar a que cuenta pertenece la publicacion
        var item = await _db.MeliItems.FirstOrDefaultAsync(mi => mi.MeliItemId == req.Mla);
        var accountId = item?.MeliAccountId;
        var accounts = accountId.HasValue
            ? (await _accountService.GetAllAccountEntitiesAsync()).Where(a => a.Id == accountId).ToList()
            : await _accountService.GetAllAccountEntitiesAsync();

        var http = _httpFactory.CreateClient();
        foreach (var account in accounts)
        {
            var token = await _accountService.GetValidTokenAsync(account);
            if (token is null) continue;

            using var httpReq = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.mercadolibre.com/items/{req.Mla}/shipping_options?zip_code={req.Cp}");
            httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(httpReq);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                return Ok(new { ok = false, http = (int)resp.StatusCode, raw = body });
            }
            using var doc = JsonDocument.Parse(body);
            var options = new List<object>();
            if (doc.RootElement.TryGetProperty("options", out var opts))
            {
                foreach (var o in opts.EnumerateArray())
                {
                    options.Add(new {
                        name = o.TryGetProperty("name", out var n) ? n.GetString() : null,
                        cost = o.TryGetProperty("cost", out var c) ? c.GetDecimal() : 0m,
                        shippingMethodId = o.TryGetProperty("shipping_method_id", out var sm) ? sm.GetInt64() : 0,
                        shippingMethodType = o.TryGetProperty("shipping_method_type", out var smt) ? smt.GetString() : null,
                    });
                }
            }
            return Ok(new { ok = true, mla = req.Mla, cp = req.Cp, cuenta = account.Nickname, options });
        }
        return BadRequest(new { error = "No hay cuenta con token valido" });
    }

    public record CotizarMasivoRequest(string Cp, List<string>? Mlas);

    /// <summary>
    /// Cotiza envio a UN cp para MUCHOS MLAs en paralelo. Si Mlas viene vacio, cotiza
    /// las 476 publicaciones ME1 activas (las saca del cache de /publicaciones si esta caliente).
    /// Devuelve [{mla, cost, error}]. Paralelismo 10 para no saturar MeLi.
    /// </summary>
    [HttpPost("cotizar-masivo")]
    public async Task<IActionResult> CotizarMasivo([FromBody] CotizarMasivoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Cp)) return BadRequest(new { error = "Falta CP" });

        // Si no me pasaron MLAs, los saco del cache (o los re-bajo)
        List<string> mlas = req.Mlas ?? new List<string>();
        if (mlas.Count == 0)
        {
            if (_cache.TryGetValue("me1:publicaciones:listado:v3_pesoFix", out var cached) && cached is not null)
            {
                // El cache es un { total, items }. Extraigo items.mla via reflection-light.
                var json = JsonSerializer.Serialize(cached);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("items", out var items))
                {
                    foreach (var it in items.EnumerateArray())
                    {
                        if (it.TryGetProperty("mla", out var m)) mlas.Add(m.GetString() ?? "");
                    }
                }
            }
            // Si sigue vacio, bajo de MeLi en vivo (solo los IDs)
            if (mlas.Count == 0)
            {
                var accounts0 = await _accountService.GetAllAccountEntitiesAsync();
                var http0 = _httpFactory.CreateClient();
                foreach (var acc0 in accounts0)
                {
                    var tok0 = await _accountService.GetValidTokenAsync(acc0);
                    if (tok0 is null) continue;
                    int offset0 = 0;
                    while (offset0 < 5000)
                    {
                        using var rq0 = new HttpRequestMessage(HttpMethod.Get,
                            $"https://api.mercadolibre.com/users/{acc0.MeliUserId}/items/search?status=active&shipping_mode=me1&limit=50&offset={offset0}");
                        rq0.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tok0);
                        using var rp0 = await http0.SendAsync(rq0);
                        if (!rp0.IsSuccessStatusCode) break;
                        using var d0 = JsonDocument.Parse(await rp0.Content.ReadAsStringAsync());
                        int total0 = -1;
                        if (d0.RootElement.TryGetProperty("paging", out var pg) && pg.TryGetProperty("total", out var tot))
                            total0 = tot.GetInt32();
                        if (d0.RootElement.TryGetProperty("results", out var rs))
                            foreach (var r in rs.EnumerateArray()) mlas.Add(r.GetString() ?? "");
                        offset0 += 50;
                        if (total0 > 0 && offset0 >= total0) break;
                    }
                }
            }
        }
        mlas = mlas.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();

        // Necesito un token. Para cotizar sirve el de cualquier cuenta activa (los items son públicos).
        var accounts = await _accountService.GetAllAccountEntitiesAsync();
        string? token = null;
        foreach (var a in accounts) { token = await _accountService.GetValidTokenAsync(a); if (token is not null) break; }
        if (token is null) return BadRequest(new { error = "Sin token MeLi" });

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        var resultados = new System.Collections.Concurrent.ConcurrentBag<object>();

        // Paralelismo controlado con SemaphoreSlim
        using var sem = new SemaphoreSlim(10);
        var tareas = mlas.Select(async mla =>
        {
            await sem.WaitAsync();
            try
            {
                using var rq = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.mercadolibre.com/items/{mla}/shipping_options?zip_code={req.Cp}");
                rq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var rp = await http.SendAsync(rq);
                if (!rp.IsSuccessStatusCode)
                {
                    resultados.Add(new { mla, cost = (decimal?)null, error = $"HTTP {(int)rp.StatusCode}" });
                    return;
                }
                using var d = JsonDocument.Parse(await rp.Content.ReadAsStringAsync());
                decimal? cost = null; string? metodo = null;
                if (d.RootElement.TryGetProperty("options", out var opts))
                {
                    foreach (var o in opts.EnumerateArray())
                    {
                        if (o.TryGetProperty("cost", out var c)) cost = c.GetDecimal();
                        if (o.TryGetProperty("name", out var n)) metodo = n.GetString();
                        break;
                    }
                }
                resultados.Add(new { mla, cost, metodo, error = (string?)null });
            }
            catch (Exception ex) { resultados.Add(new { mla, cost = (decimal?)null, error = ex.Message }); }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tareas);

        return Ok(new { cp = req.Cp, total = resultados.Count, items = resultados });
    }

    /// <summary>
    /// Genera el Excel para subir como "Tabla de Contingencia / Axado" en el panel
    /// de MeLi. Una fila por CP cubriendo los rangos acordados con el usuario el 2026-06-24:
    ///   1001-1499 CABA $10.000
    ///   1500-1599 GBA cercano $12.000
    ///   1600-1699 GBA medio $14.000
    ///   1700-1838 GBA cercano $12.000
    ///   1839 TU ZONA $8.000
    ///   1840-1899 GBA cercano $12.000
    ///   1900-1999 La Plata zona $18.000
    ///   2800-2899 Zarate / Campana / San Antonio de Areco $18.000
    /// FUERA DE COBERTURA (sacados 24/06 por venta a San Nicolas de los Arroyos que Axado no llega):
    ///   2000-2299 Rosario y sur Santa Fe
    ///   2300-2399 Centro Santa Fe
    ///   2400-2699 Cordoba pampeana (San Francisco, Bell Ville, Rio Cuarto, Marcos Juarez)
    ///   2700-2799 Pergamino, Rojas, Salto
    ///   2900-2999 San Nicolas, San Pedro, Ramallo
    /// Peso 0-999 kg sin recargo por kg extra => tarifa FIJA por zona, no importa el peso.
    /// Plazo 1 dia habil.
    /// </summary>
    [HttpGet("tabla-axado.xlsx")]
    public IActionResult DescargarTablaAxado()
    {
        // Definicion de zonas (rango_inicio, rango_fin, precio)
        var rangos = new (int from, int to, decimal precio)[]
        {
            (1001, 1499, 10000m),  // CABA
            (1500, 1599, 12000m),  // GBA cercano
            (1600, 1699, 14000m),  // GBA medio (Tigre, San Isidro, V. Lopez, Pilar, Escobar...)
            (1700, 1838, 12000m),  // GBA cercano (Moron, Ituzaingo, Merlo, Moreno, La Matanza...)
            (1839, 1839,  8000m),  // TU ZONA: Esteban Echeverria
            (1840, 1899, 12000m),  // GBA cercano (Lomas, Quilmes, Banfield, Burzaco...)
            (1900, 1999, 18000m),  // La Plata zona (Berisso, Ensenada, Brandsen, Chascomus, Magdalena, Punta Indio)
            (2800, 2899, 18000m),  // Norte BA cercano (Zarate, Campana, San Antonio de Areco)
        };

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("MercadoLibre");

        // Fila 1: titulo
        ws.Cell(1, 1).Value = "MERCADO LIBRE";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Range(1, 1, 1, 7).Merge();

        // Fila 3: headers
        int hdr = 3;
        ws.Cell(hdr, 1).Value = "CP Inicio";
        ws.Cell(hdr, 2).Value = "CP Fin";
        ws.Cell(hdr, 3).Value = "Peso Mínimo (kg)";
        ws.Cell(hdr, 4).Value = "Peso Máximo (kg)";
        ws.Cell(hdr, 5).Value = "Valor Flete Peso ($)";
        ws.Cell(hdr, 6).Value = "Valor p/ kg excedente ($)";
        ws.Cell(hdr, 7).Value = "Plazo (días hábiles)";
        var hdrRange = ws.Range(hdr, 1, hdr, 7);
        hdrRange.Style.Font.Bold = true;
        hdrRange.Style.Fill.BackgroundColor = XLColor.LightGray;

        // Datos: una fila por CP. Total ~2000 filas para cubrir 1001-2999 (con rangos contiguos).
        int row = 4;
        foreach (var (from, to, precio) in rangos)
        {
            for (int cp = from; cp <= to; cp++)
            {
                ws.Cell(row, 1).Value = cp;
                ws.Cell(row, 2).Value = cp;
                ws.Cell(row, 3).Value = 0;
                ws.Cell(row, 4).Value = 999;  // tope alto -> nunca aplica recargo por kg extra
                ws.Cell(row, 5).Value = precio;
                ws.Cell(row, 6).Value = 0;    // $0 por kg extra -> tarifa fija
                ws.Cell(row, 7).Value = 1;    // 1 dia habil
                row++;
            }
        }

        ws.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        var bytes = stream.ToArray();
        var fileName = $"tabla-axado-me1-{DateTime.Now:yyyyMMdd-HHmm}.xlsx";
        return File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    /// <summary>
    /// Devuelve la tabla de ZONAS acordadas con el usuario el 2026-06-24.
    /// Por ahora hardcodeada. Pendiente: pasar a una tabla DB editable con ABM en /meli/me1/zonas.
    /// </summary>
    [HttpGet("zonas")]
    public IActionResult ListZonas()
    {
        var zonas = new[] {
            new { id = "tu_zona",    nombre = "TU ZONA (Esteban Echeverría + cercanías)", cpDesde = 1830, cpHasta = 1850, precio = 8000,  color = "#16a34a" },
            new { id = "caba",       nombre = "CABA",                                      cpDesde = 1001, cpHasta = 1499, precio = 10000, color = "#1d4ed8" },
            new { id = "gba_cercano", nombre = "GBA cercano (Avellaneda, Lanús, Quilmes, La Matanza)", cpDesde = 1700, cpHasta = 1899, precio = 12000, color = "#7c3aed" },
            new { id = "gba_medio",  nombre = "GBA medio (Tigre, San Isidro, V. López, F. Varela, Morón)", cpDesde = 1600, cpHasta = 1699, precio = 14000, color = "#ea580c" },
            new { id = "gba_lejano", nombre = "GBA lejano (Pilar, Escobar, Luján, Marcos Paz, Cañuelas)", cpDesde = 1620, cpHasta = 1670, precio = 16000, color = "#dc2626" },
            new { id = "la_plata",   nombre = "La Plata zona (Berisso, Ensenada, Brandsen, Chascomús, Magdalena)", cpDesde = 1900, cpHasta = 1999, precio = 18000, color = "#a16207" },
            new { id = "norte_ba",   nombre = "Norte BA cercano (Zárate, Campana, San Antonio de Areco)", cpDesde = 2800, cpHasta = 2899, precio = 18000, color = "#a16207" },
        };
        return Ok(zonas);
    }

    /// <summary>
    /// Lista TODOS los CPs activos (los que están en la tabla Axado) con su localidad,
    /// provincia, zona (id+nombre+color) y precio. Usado por la pantalla
    /// /meli/me1/codigos-postales para que el usuario vea en formato tabla qué precio
    /// le toca a cada CP.
    /// </summary>
    [HttpGet("codigos-postales-activos")]
    public IActionResult ListCodigosPostalesActivos()
    {
        // Rangos de tarifas (mismos que DescargarTablaAxado, pero con zonaId)
        var tarifas = new (int cpFrom, int cpTo, decimal precio, string zonaId)[]
        {
            (1001, 1499, 10000m, "caba"),
            (1500, 1599, 12000m, "gba_cercano"),
            (1600, 1699, 14000m, "gba_medio"),
            (1700, 1838, 12000m, "gba_cercano"),
            (1839, 1839,  8000m, "tu_zona"),
            (1840, 1899, 12000m, "gba_cercano"),
            (1900, 1999, 18000m, "la_plata"),
            (2800, 2899, 18000m, "norte_ba"),
        };

        // Metadata de cada zona (color para badge visual)
        var zonasMeta = new Dictionary<string, (string Nombre, string Color)>
        {
            ["tu_zona"]    = ("TU ZONA",       "#16a34a"),
            ["caba"]       = ("CABA",          "#1d4ed8"),
            ["gba_cercano"]= ("GBA cercano",   "#7c3aed"),
            ["gba_medio"]  = ("GBA medio",     "#ea580c"),
            ["gba_lejano"] = ("GBA lejano",    "#dc2626"),
            ["la_plata"]   = ("La Plata zona", "#a16207"),
            ["norte_ba"]   = ("Norte BA",      "#a16207"),
        };

        var resultado = new List<object>();
        foreach (var (cpFrom, cpTo, precio, zonaId) in tarifas)
        {
            var meta = zonasMeta[zonaId];
            for (int cp = cpFrom; cp <= cpTo; cp++)
            {
                var (provincia, localidad) = LookupCp(cp);
                resultado.Add(new
                {
                    cp,
                    provincia,
                    localidad,
                    zonaId,
                    zonaNombre = meta.Nombre,
                    zonaColor = meta.Color,
                    precio
                });
            }
        }

        return Ok(resultado);
    }

    // ============================================================================
    // Lookup hardcodeado de CPs argentinos → Localidad/Barrio.
    // Cubre los 1099 CPs activos para tarifas Axado. Si un CP cae en varios
    // rangos, gana el MÁS ESPECÍFICO (el último listado).
    // Fuentes: barrios CABA por sistema CPA8 + GBA y Buenos Aires por conocimiento
    // general del territorio AMBA.
    // ============================================================================
    private static readonly (int CpFrom, int CpTo, string Provincia, string Localidad)[] RANGOS_LOCALIDADES = new[]
    {
        (1001, 1010, "CABA", "Monserrat"),
        (1011, 1014, "CABA", "Retiro"),
        (1015, 1019, "CABA", "San Nicolás"),
        (1020, 1029, "CABA", "San Nicolás"),
        (1030, 1039, "CABA", "Balvanera"),
        (1040, 1049, "CABA", "Constitución"),
        (1050, 1059, "CABA", "Retiro"),
        (1060, 1069, "CABA", "San Telmo"),
        (1070, 1079, "CABA", "Constitución"),
        (1080, 1089, "CABA", "Balvanera"),
        (1090, 1099, "CABA", "San Nicolás"),
        (1100, 1109, "CABA", "Retiro"),
        (1110, 1119, "CABA", "Retiro"),
        (1120, 1129, "CABA", "Recoleta"),
        (1130, 1139, "CABA", "Recoleta"),
        (1140, 1149, "CABA", "Recoleta"),
        (1150, 1159, "CABA", "Almagro"),
        (1160, 1169, "CABA", "Recoleta"),
        (1170, 1179, "CABA", "Almagro"),
        (1180, 1189, "CABA", "Almagro"),
        (1190, 1199, "CABA", "Balvanera"),
        (1200, 1209, "CABA", "San Cristóbal"),
        (1210, 1219, "CABA", "Balvanera"),
        (1220, 1229, "CABA", "Constitución"),
        (1230, 1239, "CABA", "Constitución"),
        (1240, 1249, "CABA", "San Cristóbal"),
        (1250, 1259, "CABA", "Parque Patricios"),
        (1260, 1269, "CABA", "Barracas"),
        (1270, 1279, "CABA", "Barracas"),
        (1280, 1289, "CABA", "Barracas"),
        (1290, 1299, "CABA", "Barracas"),
        (1300, 1309, "CABA", "La Boca"),
        (1310, 1319, "CABA", "Boedo"),
        (1320, 1329, "CABA", "Boedo"),
        (1330, 1339, "CABA", "Boedo"),
        (1340, 1349, "CABA", "Parque Patricios"),
        (1350, 1359, "CABA", "Nueva Pompeya"),
        (1360, 1369, "CABA", "Nueva Pompeya"),
        (1370, 1379, "CABA", "Villa Soldati"),
        (1380, 1389, "CABA", "Villa Lugano"),
        (1390, 1399, "CABA", "Villa Riachuelo"),
        (1400, 1404, "CABA", "Parque Chacabuco"),
        (1405, 1409, "CABA", "Caballito"),
        (1410, 1413, "CABA", "Caballito"),
        (1414, 1414, "CABA", "Palermo"),
        (1415, 1419, "CABA", "Villa Crespo"),
        (1420, 1424, "CABA", "Flores"),
        (1425, 1429, "CABA", "Palermo"),
        (1430, 1434, "CABA", "Belgrano"),
        (1435, 1439, "CABA", "Villa Devoto"),
        (1440, 1444, "CABA", "Villa Luro"),
        (1445, 1449, "CABA", "Mataderos"),
        (1450, 1454, "CABA", "Versalles"),
        (1455, 1459, "CABA", "Liniers"),
        (1460, 1464, "CABA", "Mataderos"),
        (1465, 1469, "CABA", "Villa Lugano"),
        (1470, 1474, "CABA", "Vélez Sársfield"),
        (1475, 1479, "CABA", "Floresta"),
        (1480, 1484, "CABA", "Villa Luro"),
        (1485, 1489, "CABA", "Monte Castro"),
        (1490, 1494, "CABA", "Villa Real"),
        (1495, 1499, "CABA", "Villa Devoto"),
        (1500, 1500, "Buenos Aires", "Carapachay"),
        (1501, 1501, "Buenos Aires", "Villa Adelina"),
        (1502, 1502, "Buenos Aires", "Villa Adelina"),
        (1503, 1509, "Buenos Aires", "San Isidro"),
        (1510, 1519, "Buenos Aires", "Beccar"),
        (1520, 1529, "Buenos Aires", "Acassuso"),
        (1530, 1539, "Buenos Aires", "San Isidro"),
        (1540, 1549, "Buenos Aires", "Martínez"),
        (1550, 1559, "Buenos Aires", "Munro"),
        (1560, 1569, "Buenos Aires", "Olivos"),
        (1570, 1579, "Buenos Aires", "Vicente López"),
        (1580, 1589, "Buenos Aires", "Villa Martelli"),
        (1590, 1599, "Buenos Aires", "Florida"),
        (1600, 1602, "Buenos Aires", "Florida"),
        (1603, 1604, "Buenos Aires", "Villa Martelli"),
        (1605, 1605, "Buenos Aires", "Munro"),
        (1606, 1606, "Buenos Aires", "Carapachay"),
        (1607, 1608, "Buenos Aires", "Villa Adelina"),
        (1609, 1610, "Buenos Aires", "Boulogne"),
        (1611, 1612, "Buenos Aires", "Don Torcuato"),
        (1613, 1614, "Buenos Aires", "Los Polvorines"),
        (1615, 1618, "Buenos Aires", "Grand Bourg"),
        (1619, 1620, "Buenos Aires", "Garín"),
        (1621, 1623, "Buenos Aires", "Benavídez"),
        (1624, 1625, "Buenos Aires", "Maquinista Savio"),
        (1626, 1628, "Buenos Aires", "Belén de Escobar"),
        (1629, 1631, "Buenos Aires", "Pilar"),
        (1632, 1634, "Buenos Aires", "Del Viso"),
        (1635, 1638, "Buenos Aires", "Pacheco"),
        (1639, 1641, "Buenos Aires", "Martínez"),
        (1642, 1645, "Buenos Aires", "San Isidro"),
        (1646, 1650, "Buenos Aires", "San Fernando"),
        (1651, 1652, "Buenos Aires", "San Andrés"),
        (1653, 1655, "Buenos Aires", "José León Suárez"),
        (1656, 1657, "Buenos Aires", "Loma Hermosa"),
        (1658, 1659, "Buenos Aires", "San Martín"),
        (1660, 1660, "Buenos Aires", "San Miguel"),
        (1661, 1662, "Buenos Aires", "Bella Vista"),
        (1663, 1663, "Buenos Aires", "San Miguel"),
        (1664, 1665, "Buenos Aires", "Muñiz"),
        (1666, 1667, "Buenos Aires", "Tortuguitas"),
        (1668, 1670, "Buenos Aires", "Pilar"),
        (1671, 1672, "Buenos Aires", "Pacheco"),
        (1673, 1674, "Buenos Aires", "Caseros"),
        (1675, 1676, "Buenos Aires", "Sáenz Peña"),
        (1677, 1678, "Buenos Aires", "Tres de Febrero"),
        (1679, 1681, "Buenos Aires", "Hurlingham"),
        (1682, 1684, "Buenos Aires", "William Morris"),
        (1685, 1686, "Buenos Aires", "Villa Tesei"),
        (1687, 1688, "Buenos Aires", "Hurlingham"),
        (1689, 1690, "Buenos Aires", "Pablo Podestá"),
        (1691, 1692, "Buenos Aires", "Ciudadela"),
        (1693, 1694, "Buenos Aires", "Villa Bosch"),
        (1695, 1696, "Buenos Aires", "Loma Hermosa"),
        (1697, 1699, "Buenos Aires", "San Martín"),
        (1700, 1700, "Buenos Aires", "Haedo"),
        (1701, 1701, "Buenos Aires", "Ramos Mejía"),
        (1702, 1702, "Buenos Aires", "Ciudadela"),
        (1703, 1704, "Buenos Aires", "Ramos Mejía"),
        (1705, 1706, "Buenos Aires", "Haedo"),
        (1707, 1707, "Buenos Aires", "Villa Sarmiento"),
        (1708, 1708, "Buenos Aires", "Morón"),
        (1709, 1711, "Buenos Aires", "Castelar"),
        (1712, 1712, "Buenos Aires", "Castelar"),
        (1713, 1713, "Buenos Aires", "Ituzaingó"),
        (1714, 1714, "Buenos Aires", "Ituzaingó"),
        (1715, 1716, "Buenos Aires", "Padua"),
        (1717, 1717, "Buenos Aires", "San Antonio de Padua"),
        (1718, 1718, "Buenos Aires", "Merlo"),
        (1719, 1720, "Buenos Aires", "Merlo"),
        (1721, 1721, "Buenos Aires", "Pontevedra"),
        (1722, 1722, "Buenos Aires", "Merlo"),
        (1723, 1724, "Buenos Aires", "Mariano Acosta"),
        (1725, 1725, "Buenos Aires", "Marcos Paz"),
        (1726, 1727, "Buenos Aires", "Marcos Paz"),
        (1728, 1729, "Buenos Aires", "Las Heras"),
        (1730, 1730, "Buenos Aires", "Cañuelas"),
        (1731, 1733, "Buenos Aires", "Cañuelas"),
        (1734, 1735, "Buenos Aires", "Las Heras"),
        (1736, 1737, "Buenos Aires", "Lobos"),
        (1738, 1740, "Buenos Aires", "Moreno"),
        (1741, 1743, "Buenos Aires", "Moreno"),
        (1744, 1744, "Buenos Aires", "Moreno"),
        (1745, 1746, "Buenos Aires", "Paso del Rey"),
        (1747, 1750, "Buenos Aires", "General Rodríguez"),
        (1751, 1753, "Buenos Aires", "La Reja"),
        (1754, 1755, "Buenos Aires", "San Justo"),
        (1756, 1758, "Buenos Aires", "San Justo"),
        (1759, 1761, "Buenos Aires", "Isidro Casanova"),
        (1762, 1764, "Buenos Aires", "González Catán"),
        (1765, 1767, "Buenos Aires", "La Tablada"),
        (1768, 1770, "Buenos Aires", "Tapiales"),
        (1771, 1773, "Buenos Aires", "Aldo Bonzi"),
        (1774, 1776, "Buenos Aires", "Villa Madero"),
        (1777, 1779, "Buenos Aires", "Tablada"),
        (1780, 1781, "Buenos Aires", "Lomas del Mirador"),
        (1782, 1784, "Buenos Aires", "Ramos Mejía"),
        (1785, 1788, "Buenos Aires", "Gregorio de Laferrere"),
        (1789, 1791, "Buenos Aires", "Virrey del Pino"),
        (1792, 1795, "Buenos Aires", "Rafael Castillo"),
        (1796, 1798, "Buenos Aires", "Ciudad Evita"),
        (1799, 1801, "Buenos Aires", "El Pino"),
        (1802, 1804, "Buenos Aires", "Guernica"),
        (1805, 1807, "Buenos Aires", "Tristán Suárez"),
        (1808, 1810, "Buenos Aires", "Ezeiza"),
        (1811, 1813, "Buenos Aires", "Aeropuerto Ezeiza"),
        (1814, 1816, "Buenos Aires", "Canning"),
        (1817, 1820, "Buenos Aires", "Monte Grande"),
        (1821, 1823, "Buenos Aires", "Monte Grande"),
        (1824, 1826, "Buenos Aires", "Lanús"),
        (1827, 1829, "Buenos Aires", "Banfield"),
        (1830, 1832, "Buenos Aires", "Llavallol"),
        (1833, 1834, "Buenos Aires", "Turdera"),
        (1835, 1836, "Buenos Aires", "Temperley"),
        (1837, 1838, "Buenos Aires", "Adrogué"),
        (1839, 1839, "Buenos Aires", "Luis Guillón"),
        (1840, 1841, "Buenos Aires", "Luis Guillón"),
        (1842, 1843, "Buenos Aires", "Monte Grande"),
        (1844, 1846, "Buenos Aires", "Burzaco"),
        (1847, 1849, "Buenos Aires", "Longchamps"),
        (1850, 1852, "Buenos Aires", "Burzaco"),
        (1853, 1855, "Buenos Aires", "Glew"),
        (1856, 1858, "Buenos Aires", "Glew"),
        (1859, 1861, "Buenos Aires", "San Vicente"),
        (1862, 1864, "Buenos Aires", "Alejandro Korn"),
        (1865, 1867, "Buenos Aires", "José Mármol"),
        (1868, 1869, "Buenos Aires", "Rafael Calzada"),
        (1870, 1871, "Buenos Aires", "Don Bosco"),
        (1872, 1873, "Buenos Aires", "Sarandí"),
        (1874, 1875, "Buenos Aires", "Villa Domínico"),
        (1876, 1877, "Buenos Aires", "Bernal"),
        (1878, 1879, "Buenos Aires", "Quilmes"),
        (1880, 1881, "Buenos Aires", "Quilmes Oeste"),
        (1882, 1883, "Buenos Aires", "Wilde"),
        (1884, 1885, "Buenos Aires", "Bernal Oeste"),
        (1886, 1887, "Buenos Aires", "Ranelagh"),
        (1888, 1889, "Buenos Aires", "Florencio Varela"),
        (1890, 1892, "Buenos Aires", "Florencio Varela"),
        (1893, 1895, "Buenos Aires", "Hudson"),
        (1896, 1899, "Buenos Aires", "City Bell"),
        (1900, 1903, "Buenos Aires", "La Plata"),
        (1904, 1907, "Buenos Aires", "La Plata - Los Hornos"),
        (1908, 1911, "Buenos Aires", "La Plata - Tolosa"),
        (1912, 1915, "Buenos Aires", "La Plata - Ringuelet"),
        (1916, 1919, "Buenos Aires", "La Plata - Gonnet"),
        (1920, 1922, "Buenos Aires", "La Plata - City Bell"),
        (1923, 1925, "Buenos Aires", "Berisso"),
        (1925, 1927, "Buenos Aires", "Ensenada"),
        (1928, 1931, "Buenos Aires", "Punta Lara"),
        (1932, 1935, "Buenos Aires", "Magdalena"),
        (1936, 1939, "Buenos Aires", "Atalaya"),
        (1940, 1943, "Buenos Aires", "Verónica"),
        (1944, 1947, "Buenos Aires", "Punta Indio"),
        (1948, 1951, "Buenos Aires", "Pipinas"),
        (1952, 1955, "Buenos Aires", "Bavio"),
        (1956, 1959, "Buenos Aires", "Pereyra"),
        (1960, 1962, "Buenos Aires", "Brandsen"),
        (1963, 1965, "Buenos Aires", "Brandsen"),
        (1966, 1968, "Buenos Aires", "Jeppener"),
        (1969, 1971, "Buenos Aires", "Ranchos"),
        (1972, 1974, "Buenos Aires", "General Paz"),
        (1975, 1977, "Buenos Aires", "Chascomús"),
        (1978, 1981, "Buenos Aires", "Chascomús"),
        (1982, 1984, "Buenos Aires", "Lezama"),
        (1985, 1988, "Buenos Aires", "Pila"),
        (1989, 1992, "Buenos Aires", "Castelli"),
        (1993, 1996, "Buenos Aires", "Dolores"),
        (1997, 1999, "Buenos Aires", "Dolores"),
        (2800, 2803, "Buenos Aires", "Zárate"),
        (2804, 2806, "Buenos Aires", "Campana"),
        (2807, 2809, "Buenos Aires", "Alsina"),
        (2810, 2812, "Buenos Aires", "Lima"),
        (2813, 2815, "Buenos Aires", "Otamendi"),
        (2816, 2818, "Buenos Aires", "Río Luján"),
        (2819, 2821, "Buenos Aires", "Pilar Norte"),
        (2822, 2824, "Buenos Aires", "Capilla del Señor"),
        (2825, 2827, "Buenos Aires", "Los Cardales"),
        (2828, 2830, "Buenos Aires", "Exaltación de la Cruz"),
        (2831, 2833, "Buenos Aires", "Parada Robles"),
        (2834, 2836, "Buenos Aires", "Capilla del Señor"),
        (2837, 2839, "Buenos Aires", "Solís"),
        (2840, 2842, "Buenos Aires", "Pavón"),
        (2843, 2845, "Buenos Aires", "Carmen de Areco"),
        (2846, 2848, "Buenos Aires", "Tres Sargentos"),
        (2849, 2851, "Buenos Aires", "Gowland"),
        (2852, 2854, "Buenos Aires", "Mercedes"),
        (2855, 2857, "Buenos Aires", "Suipacha"),
        (2858, 2860, "Buenos Aires", "Chivilcoy"),
        (2861, 2863, "Buenos Aires", "Bragado"),
        (2864, 2866, "Buenos Aires", "Henderson"),
        (2867, 2869, "Buenos Aires", "9 de Julio"),
        (2870, 2872, "Buenos Aires", "Chacabuco"),
        (2873, 2875, "Buenos Aires", "Junín"),
        (2876, 2878, "Buenos Aires", "Lincoln"),
        (2879, 2881, "Buenos Aires", "Pehuajó"),
        (2882, 2884, "Buenos Aires", "Carlos Casares"),
        (2885, 2887, "Buenos Aires", "Salliqueló"),
        (2888, 2890, "Buenos Aires", "Trenque Lauquen"),
        (2891, 2893, "Buenos Aires", "América"),
        (2894, 2896, "Buenos Aires", "Rivadavia"),
        (2897, 2899, "Buenos Aires", "San Antonio de Areco"),
    };

    private static (string? Provincia, string? Localidad) LookupCp(int cp)
    {
        string? prov = null;
        string? loc = null;
        foreach (var (cpFrom, cpTo, p, l) in RANGOS_LOCALIDADES)
        {
            if (cp >= cpFrom && cp <= cpTo) { prov = p; loc = l; }
        }
        return (prov, loc);
    }
}
