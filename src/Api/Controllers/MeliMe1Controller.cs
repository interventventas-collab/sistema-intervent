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
    public async Task<IActionResult> DescargarTablaAxado()
    {
        // 2026-10-05: los CPs sacados desde /meli/me1/codigos-postales no van en la tabla.
        var excluidos = (await _db.Me1CpExcluidos.Select(e => e.Cp).ToListAsync()).ToHashSet();

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
        foreach (var (from, to, precio, _) in TARIFAS)
        {
            for (int cp = from; cp <= to; cp++)
            {
                if (excluidos.Contains(cp)) continue;
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

    // Definicion de zonas acordadas el 2026-06-24 (rango_inicio, rango_fin, precio, zona).
    // La usan el Excel manual, la lista de /meli/me1/codigos-postales y el envio por API a MeLi.
    internal static readonly (int CpFrom, int CpTo, decimal Precio, string ZonaId)[] TARIFAS =
    {
        (1001, 1499, 10000m, "caba"),        // CABA
        (1500, 1599, 12000m, "gba_cercano"), // GBA cercano
        (1600, 1699, 14000m, "gba_medio"),   // GBA medio (Tigre, San Isidro, V. Lopez, Pilar, Escobar...)
        (1700, 1838, 12000m, "gba_cercano"), // GBA cercano (Moron, Ituzaingo, Merlo, Moreno, La Matanza...)
        (1839, 1839,  8000m, "tu_zona"),     // TU ZONA: Esteban Echeverria
        (1840, 1899, 12000m, "gba_cercano"), // GBA cercano (Lomas, Quilmes, Banfield, Burzaco...)
        (1900, 1999, 18000m, "la_plata"),    // La Plata zona (Berisso, Ensenada, Brandsen, Chascomus, Magdalena, Punta Indio)
        (2800, 2899, 18000m, "norte_ba"),    // Norte BA cercano (Zarate, Campana, San Antonio de Areco)
    };

    // Metadata de cada zona (color para badge visual)
    private static readonly Dictionary<string, (string Nombre, string Color)> ZONAS_META = new()
    {
        ["tu_zona"]    = ("TU ZONA",       "#16a34a"),
        ["caba"]       = ("CABA",          "#1d4ed8"),
        ["gba_cercano"]= ("GBA cercano",   "#7c3aed"),
        ["gba_medio"]  = ("GBA medio",     "#ea580c"),
        ["gba_lejano"] = ("GBA lejano",    "#dc2626"),
        ["la_plata"]   = ("La Plata zona", "#a16207"),
        ["norte_ba"]   = ("Norte BA",      "#a16207"),
    };

    /// <summary>
    /// Lista TODOS los CPs de la tabla Axado con su localidad, provincia, zona (id+nombre+color),
    /// precio, si se ofrece o no (2026-10-05: se pueden sacar desde la pantalla) y la ubicación
    /// de su localidad para el mapa (null si todavía no se buscó). Usado por /meli/me1/codigos-postales.
    /// </summary>
    [HttpGet("codigos-postales-activos")]
    public async Task<IActionResult> ListCodigosPostalesActivos()
    {
        var excluidos = (await _db.Me1CpExcluidos.Select(e => e.Cp).ToListAsync()).ToHashSet();
        var ubicaciones = await _db.Me1LocalidadUbicaciones.Where(u => u.Encontrado)
            .ToDictionaryAsync(u => u.Clave, u => (u.Lat, u.Lng));

        var resultado = new List<object>();
        foreach (var (cpFrom, cpTo, precio, zonaId) in TARIFAS)
        {
            var meta = ZONAS_META[zonaId];
            for (int cp = cpFrom; cp <= cpTo; cp++)
            {
                var (provincia, localidad) = LookupCp(cp);
                ubicaciones.TryGetValue(ClaveLocalidad(provincia, localidad), out var ub);
                resultado.Add(new
                {
                    cp,
                    provincia,
                    localidad,
                    zonaId,
                    zonaNombre = meta.Nombre,
                    zonaColor = meta.Color,
                    precio,
                    ofrecido = !excluidos.Contains(cp),
                    lat = ub.Lat,
                    lng = ub.Lng
                });
            }
        }

        return Ok(resultado);
    }

    internal static string ClaveLocalidad(string? provincia, string? localidad) => $"{provincia}|{localidad}";

    // ============================================================================
    // Código postal → Provincia/Localidad de los CPs de la tabla Axado.
    // 2026-10-05: rehecho con la base de Correo Argentino (gist lucsh/localidades.csv, misma
    // base que ftenreyrogit/C-digos-postales-Argentina). La lista de junio estaba inventada en
    // partes (ej. 2873-2875 decía "Junín", cuyo CP es 6000). Fuera de CABA se tomó la localidad
    // principal de cada CP; en CABA (donde el CP va por calle) el barrio sale de geocodificar
    // calles de ese CP. Los CPs de la tabla que no están acá NO existen para el Correo.
    // Ojo: 2820-2854 son de Entre Ríos (Gualeguaychú, Gualeguay...) y 2875 de Córdoba.
    // ============================================================================
    private static readonly Dictionary<int, (string Provincia, string Localidad)> LOCALIDADES_CP = new()
    {
        [1001] = ("CABA", "Retiro"),
        [1002] = ("CABA", "San Nicolás"),
        [1003] = ("CABA", "San Nicolás"),
        [1004] = ("CABA", "San Nicolás"),
        [1005] = ("CABA", "San Nicolás"),
        [1006] = ("CABA", "Retiro"),
        [1007] = ("CABA", "Retiro"),
        [1008] = ("CABA", "San Nicolás"),
        [1009] = ("CABA", "San Nicolás"),
        [1010] = ("CABA", "San Nicolás"),
        [1011] = ("CABA", "Retiro"),
        [1012] = ("CABA", "San Nicolás"),
        [1013] = ("CABA", "San Nicolás"),
        [1014] = ("CABA", "Barrio Norte"),
        [1015] = ("CABA", "San Nicolás"),
        [1016] = ("CABA", "Barrio Norte"),
        [1017] = ("CABA", "San Nicolás"),
        [1018] = ("CABA", "Barrio Norte"),
        [1019] = ("CABA", "San Nicolás"),
        [1020] = ("CABA", "San Nicolás"),
        [1021] = ("CABA", "Barrio Norte"),
        [1022] = ("CABA", "San Nicolás"),
        [1023] = ("CABA", "Barrio Norte"),
        [1024] = ("CABA", "Barrio Norte"),
        [1025] = ("CABA", "Balvanera"),
        [1026] = ("CABA", "Balvanera"),
        [1027] = ("CABA", "San Nicolás"),
        [1028] = ("CABA", "Balvanera"),
        [1029] = ("CABA", "Balvanera"),
        [1030] = ("CABA", "Balvanera"),
        [1031] = ("CABA", "Balvanera"),
        [1032] = ("CABA", "Balvanera"),
        [1033] = ("CABA", "San Nicolás"),
        [1034] = ("CABA", "Balvanera"),
        [1035] = ("CABA", "San Nicolás"),
        [1036] = ("CABA", "Balvanera"),
        [1037] = ("CABA", "San Nicolás"),
        [1038] = ("CABA", "Balvanera"),
        [1039] = ("CABA", "Balvanera"),
        [1040] = ("CABA", "Constitución"),
        [1041] = ("CABA", "San Nicolás"),
        [1042] = ("CABA", "San Nicolás"),
        [1043] = ("CABA", "San Nicolás"),
        [1044] = ("CABA", "Balvanera"),
        [1045] = ("CABA", "Balvanera"),
        [1046] = ("CABA", "Balvanera"),
        [1047] = ("CABA", "San Nicolás"),
        [1048] = ("CABA", "San Nicolás"),
        [1049] = ("CABA", "San Nicolás"),
        [1050] = ("CABA", "Retiro"),
        [1051] = ("CABA", "Balvanera"),
        [1052] = ("CABA", "Balvanera"),
        [1053] = ("CABA", "San Nicolás"),
        [1054] = ("CABA", "San Nicolás"),
        [1055] = ("CABA", "San Nicolás"),
        [1056] = ("CABA", "Balvanera"),
        [1057] = ("CABA", "Retiro"),
        [1058] = ("CABA", "Barrio Norte"),
        [1059] = ("CABA", "Retiro"),
        [1060] = ("CABA", "Barrio Norte"),
        [1061] = ("CABA", "Barrio Norte"),
        [1062] = ("CABA", "Barrio Norte"),
        [1063] = ("CABA", "La Boca"),
        [1064] = ("CABA", "Monserrat"),
        [1065] = ("CABA", "Monserrat"),
        [1066] = ("CABA", "Monserrat"),
        [1067] = ("CABA", "Monserrat"),
        [1068] = ("CABA", "San Telmo"),
        [1069] = ("CABA", "Monserrat"),
        [1070] = ("CABA", "Monserrat"),
        [1071] = ("CABA", "Monserrat"),
        [1072] = ("CABA", "Versalles"),
        [1073] = ("CABA", "San Nicolás"),
        [1074] = ("CABA", "Monserrat"),
        [1075] = ("CABA", "Monserrat"),
        [1076] = ("CABA", "Monserrat"),
        [1077] = ("CABA", "Constitución"),
        [1078] = ("CABA", "Monserrat"),
        [1079] = ("CABA", "Balvanera"),
        [1080] = ("CABA", "Balvanera"),
        [1081] = ("CABA", "Balvanera"),
        [1082] = ("CABA", "Balvanera"),
        [1083] = ("CABA", "Balvanera"),
        [1084] = ("CABA", "San Nicolás"),
        [1085] = ("CABA", "Villa Soldati"),
        [1086] = ("CABA", "Monserrat"),
        [1087] = ("CABA", "Monserrat"),
        [1088] = ("CABA", "Balvanera"),
        [1089] = ("CABA", "Balvanera"),
        [1090] = ("CABA", "Balvanera"),
        [1091] = ("CABA", "Monserrat"),
        [1092] = ("CABA", "Monserrat"),
        [1093] = ("CABA", "Monserrat"),
        [1094] = ("CABA", "Balvanera"),
        [1095] = ("CABA", "Monserrat"),
        [1096] = ("CABA", "Balvanera"),
        [1097] = ("CABA", "Monserrat"),
        [1098] = ("CABA", "Monserrat"),
        [1099] = ("CABA", "San Telmo"),
        [1100] = ("CABA", "Monserrat"),
        [1101] = ("CABA", "San Telmo"),
        [1102] = ("CABA", "San Telmo"),
        [1103] = ("CABA", "San Telmo"),
        [1104] = ("CABA", "Retiro"),
        [1105] = ("CABA", "Retiro"),
        [1106] = ("CABA", "Puerto Madero"),
        [1107] = ("CABA", "Puerto Madero"),
        [1108] = ("CABA", "Monserrat"),
        [1109] = ("CABA", "Monserrat"),
        [1110] = ("CABA", "Retiro"),
        [1111] = ("CABA", "Barrio Norte"),
        [1112] = ("CABA", "Barrio Norte"),
        [1113] = ("CABA", "Barrio Norte"),
        [1114] = ("CABA", "Retiro"),
        [1115] = ("CABA", "Barrio Norte"),
        [1116] = ("CABA", "Barrio Norte"),
        [1117] = ("CABA", "Barrio Norte"),
        [1118] = ("CABA", "Barrio Norte"),
        [1119] = ("CABA", "Barrio Norte"),
        [1120] = ("CABA", "Balvanera"),
        [1121] = ("CABA", "Barrio Norte"),
        [1122] = ("CABA", "Barrio Norte"),
        [1123] = ("CABA", "Barrio Norte"),
        [1124] = ("CABA", "Barrio Norte"),
        [1125] = ("CABA", "Barrio Norte"),
        [1126] = ("CABA", "Barrio Norte"),
        [1127] = ("CABA", "Recoleta"),
        [1128] = ("CABA", "Barrio Norte"),
        [1129] = ("CABA", "Barrio Norte"),
        [1130] = ("CABA", "Constitución"),
        [1133] = ("CABA", "Constitución"),
        [1134] = ("CABA", "Constitución"),
        [1135] = ("CABA", "Constitución"),
        [1136] = ("CABA", "Constitución"),
        [1137] = ("CABA", "Constitución"),
        [1138] = ("CABA", "Constitución"),
        [1139] = ("CABA", "Constitución"),
        [1140] = ("CABA", "San Telmo"),
        [1141] = ("CABA", "San Telmo"),
        [1143] = ("CABA", "Barracas"),
        [1147] = ("CABA", "San Telmo"),
        [1148] = ("CABA", "Constitución"),
        [1150] = ("CABA", "San Telmo"),
        [1151] = ("CABA", "Constitución"),
        [1152] = ("CABA", "Barracas"),
        [1153] = ("CABA", "San Telmo"),
        [1154] = ("CABA", "Constitución"),
        [1155] = ("CABA", "La Boca"),
        [1156] = ("CABA", "La Boca"),
        [1157] = ("CABA", "La Boca"),
        [1158] = ("CABA", "La Boca"),
        [1159] = ("CABA", "La Boca"),
        [1160] = ("CABA", "La Boca"),
        [1161] = ("CABA", "La Boca"),
        [1162] = ("CABA", "La Boca"),
        [1163] = ("CABA", "La Boca"),
        [1164] = ("CABA", "La Boca"),
        [1165] = ("CABA", "La Boca"),
        [1166] = ("CABA", "Versalles"),
        [1167] = ("CABA", "La Boca"),
        [1168] = ("CABA", "La Boca"),
        [1169] = ("CABA", "Nueva Pompeya"),
        [1170] = ("CABA", "Balvanera"),
        [1171] = ("CABA", "Balvanera"),
        [1172] = ("CABA", "Almagro"),
        [1173] = ("CABA", "Balvanera"),
        [1174] = ("CABA", "Almagro"),
        [1175] = ("CABA", "Almagro"),
        [1176] = ("CABA", "Almagro"),
        [1177] = ("CABA", "Almagro"),
        [1178] = ("CABA", "Almagro"),
        [1179] = ("CABA", "Almagro"),
        [1180] = ("CABA", "Palermo"),
        [1181] = ("CABA", "Almagro"),
        [1182] = ("CABA", "Almagro"),
        [1183] = ("CABA", "Almagro"),
        [1184] = ("CABA", "Almagro"),
        [1185] = ("CABA", "Almagro"),
        [1186] = ("CABA", "Palermo"),
        [1187] = ("CABA", "Balvanera"),
        [1188] = ("CABA", "Almagro"),
        [1189] = ("CABA", "Balvanera"),
        [1190] = ("CABA", "Almagro"),
        [1191] = ("CABA", "Balvanera"),
        [1192] = ("CABA", "Almagro"),
        [1193] = ("CABA", "Balvanera"),
        [1194] = ("CABA", "Almagro"),
        [1195] = ("CABA", "Almagro"),
        [1196] = ("CABA", "Balvanera"),
        [1197] = ("CABA", "Almagro"),
        [1198] = ("CABA", "Balvanera"),
        [1199] = ("CABA", "Almagro"),
        [1200] = ("CABA", "Almagro"),
        [1201] = ("CABA", "Almagro"),
        [1202] = ("CABA", "Almagro"),
        [1203] = ("CABA", "San Nicolás"),
        [1204] = ("CABA", "Almagro"),
        [1205] = ("CABA", "Almagro"),
        [1206] = ("CABA", "Almagro"),
        [1207] = ("CABA", "Balvanera"),
        [1208] = ("CABA", "Almagro"),
        [1209] = ("CABA", "Balvanera"),
        [1210] = ("CABA", "Almagro"),
        [1211] = ("CABA", "Almagro"),
        [1212] = ("CABA", "Almagro"),
        [1213] = ("CABA", "Balvanera"),
        [1214] = ("CABA", "Balvanera"),
        [1215] = ("CABA", "Balvanera"),
        [1216] = ("CABA", "Almagro"),
        [1217] = ("CABA", "Almagro"),
        [1218] = ("CABA", "Almagro"),
        [1219] = ("CABA", "San Cristóbal"),
        [1220] = ("CABA", "Liniers"),
        [1221] = ("CABA", "Balvanera"),
        [1222] = ("CABA", "Balvanera"),
        [1223] = ("CABA", "San Cristóbal"),
        [1224] = ("CABA", "San Cristóbal"),
        [1225] = ("CABA", "San Cristóbal"),
        [1226] = ("CABA", "Boedo"),
        [1227] = ("CABA", "San Cristóbal"),
        [1228] = ("CABA", "Boedo"),
        [1229] = ("CABA", "San Cristóbal"),
        [1230] = ("CABA", "San Cristóbal"),
        [1231] = ("CABA", "San Cristóbal"),
        [1232] = ("CABA", "San Cristóbal"),
        [1233] = ("CABA", "Boedo"),
        [1234] = ("CABA", "Almagro"),
        [1235] = ("CABA", "Boedo"),
        [1236] = ("CABA", "Almagro"),
        [1237] = ("CABA", "Boedo"),
        [1238] = ("CABA", "Boedo"),
        [1239] = ("CABA", "Boedo"),
        [1240] = ("CABA", "Boedo"),
        [1241] = ("CABA", "Boedo"),
        [1242] = ("CABA", "Parque Patricios"),
        [1243] = ("CABA", "Parque Patricios"),
        [1244] = ("CABA", "Parque Patricios"),
        [1245] = ("CABA", "Parque Patricios"),
        [1246] = ("CABA", "Parque Patricios"),
        [1247] = ("CABA", "Parque Patricios"),
        [1248] = ("CABA", "San Cristóbal"),
        [1249] = ("CABA", "Parque Patricios"),
        [1250] = ("CABA", "Parque Patricios"),
        [1251] = ("CABA", "San Cristóbal"),
        [1252] = ("CABA", "San Cristóbal"),
        [1253] = ("CABA", "Boedo"),
        [1254] = ("CABA", "San Cristóbal"),
        [1255] = ("CABA", "Boedo"),
        [1256] = ("CABA", "Parque Patricios"),
        [1257] = ("CABA", "Boedo"),
        [1258] = ("CABA", "Parque Patricios"),
        [1259] = ("CABA", "Parque Patricios"),
        [1260] = ("CABA", "Parque Patricios"),
        [1261] = ("CABA", "Parque Patricios"),
        [1262] = ("CABA", "Boedo"),
        [1263] = ("CABA", "Boedo"),
        [1264] = ("CABA", "Parque Patricios"),
        [1265] = ("CABA", "Barracas"),
        [1266] = ("CABA", "Barracas"),
        [1267] = ("CABA", "Barracas"),
        [1268] = ("CABA", "Barracas"),
        [1269] = ("CABA", "Barracas"),
        [1270] = ("CABA", "Barracas"),
        [1271] = ("CABA", "Barracas"),
        [1272] = ("CABA", "Caballito"),
        [1273] = ("CABA", "Barracas"),
        [1274] = ("CABA", "Barracas"),
        [1275] = ("CABA", "Barracas"),
        [1276] = ("CABA", "Barracas"),
        [1277] = ("CABA", "Barracas"),
        [1278] = ("CABA", "Barracas"),
        [1279] = ("CABA", "Barracas"),
        [1280] = ("CABA", "Barracas"),
        [1281] = ("CABA", "Barracas"),
        [1282] = ("CABA", "Parque Patricios"),
        [1283] = ("CABA", "Barracas"),
        [1284] = ("CABA", "Parque Patricios"),
        [1285] = ("CABA", "Barracas"),
        [1286] = ("CABA", "Barracas"),
        [1287] = ("CABA", "Barracas"),
        [1288] = ("CABA", "Barracas"),
        [1289] = ("CABA", "Barracas"),
        [1290] = ("CABA", "Barracas"),
        [1291] = ("CABA", "Barracas"),
        [1292] = ("CABA", "Barracas"),
        [1293] = ("CABA", "Barracas"),
        [1294] = ("CABA", "Barracas"),
        [1295] = ("CABA", "Barracas"),
        [1296] = ("CABA", "Barracas"),
        [1405] = ("CABA", "Caballito"),
        [1406] = ("CABA", "Parque Chacabuco"),
        [1407] = ("CABA", "Parque Avellaneda"),
        [1408] = ("CABA", "Liniers"),
        [1414] = ("CABA", "Villa Crespo"),
        [1416] = ("CABA", "Paternal"),
        [1417] = ("CABA", "Agronomía"),
        [1419] = ("CABA", "Villa Pueyrredón"),
        [1424] = ("CABA", "Parque Chacabuco"),
        [1425] = ("CABA", "Barrio Norte"),
        [1426] = ("CABA", "Palermo"),
        [1427] = ("CABA", "Villa Ortúzar"),
        [1428] = ("CABA", "Belgrano"),
        [1429] = ("CABA", "Núñez"),
        [1430] = ("CABA", "Villa Urquiza"),
        [1431] = ("CABA", "Villa Urquiza"),
        [1437] = ("CABA", "Villa Soldati"),
        [1439] = ("CABA", "Villa Lugano"),
        [1440] = ("CABA", "Mataderos"),
        [1601] = ("Buenos Aires", "Isla Martín García"),
        [1602] = ("Buenos Aires", "Florida"),
        [1603] = ("Buenos Aires", "Villa Martelli"),
        [1605] = ("Buenos Aires", "Munro"),
        [1607] = ("Buenos Aires", "Villa Adelina"),
        [1609] = ("Buenos Aires", "Boulogne"),
        [1611] = ("Buenos Aires", "Don Torcuato"),
        [1612] = ("Buenos Aires", "Adolfo Sourdeaux"),
        [1613] = ("Buenos Aires", "Los Polvorines"),
        [1615] = ("Buenos Aires", "Grand Bourg"),
        [1617] = ("Buenos Aires", "General Pacheco"),
        [1619] = ("Buenos Aires", "Garín"),
        [1621] = ("Buenos Aires", "Benavídez"),
        [1623] = ("Buenos Aires", "Ingeniero Maschwitz"),
        [1625] = ("Buenos Aires", "Escobar"),
        [1627] = ("Buenos Aires", "Matheu"),
        [1628] = ("Buenos Aires", "Villa Albertina"),
        [1629] = ("Buenos Aires", "Pilar"),
        [1631] = ("Buenos Aires", "Villa Rosa"),
        [1633] = ("Buenos Aires", "Fátima"),
        [1635] = ("Buenos Aires", "Presidente Derqui"),
        [1636] = ("Buenos Aires", "Olivos"),
        [1638] = ("Buenos Aires", "Vicente López"),
        [1640] = ("Buenos Aires", "Martínez"),
        [1642] = ("Buenos Aires", "San Isidro"),
        [1643] = ("Buenos Aires", "Beccar"),
        [1644] = ("Buenos Aires", "Victoria"),
        [1646] = ("Buenos Aires", "San Fernando"),
        [1647] = ("Buenos Aires", "San Fernando"),
        [1648] = ("Buenos Aires", "Tigre"),
        [1649] = ("Buenos Aires", "Tigre"),
        [1650] = ("Buenos Aires", "San Martín"),
        [1651] = ("Buenos Aires", "San Andrés"),
        [1653] = ("Buenos Aires", "Villa Ballester"),
        [1655] = ("Buenos Aires", "José León Suárez"),
        [1657] = ("Buenos Aires", "Pablo Podesta"),
        [1659] = ("Buenos Aires", "Campo de Mayo"),
        [1661] = ("Buenos Aires", "Bella Vista"),
        [1663] = ("Buenos Aires", "San Miguel"),
        [1664] = ("Buenos Aires", "Trujui"),
        [1665] = ("Buenos Aires", "José C. Paz"),
        [1667] = ("Buenos Aires", "Tortuguitas"),
        [1669] = ("Buenos Aires", "Del Viso"),
        [1672] = ("Buenos Aires", "Villa Lynch"),
        [1674] = ("Buenos Aires", "Villa Saenz Peña"),
        [1676] = ("Buenos Aires", "Santos Lugares"),
        [1678] = ("Buenos Aires", "Caseros"),
        [1682] = ("Buenos Aires", "Villa Martin Coronado"),
        [1684] = ("Buenos Aires", "El Palomar"),
        [1686] = ("Buenos Aires", "Hurlingham"),
        [1688] = ("Buenos Aires", "Tesei"),
        [1702] = ("Buenos Aires", "Ciudadela"),
        [1704] = ("Buenos Aires", "Ramos Mejía"),
        [1706] = ("Buenos Aires", "Haedo"),
        [1708] = ("Buenos Aires", "Morón"),
        [1712] = ("Buenos Aires", "Castelar"),
        [1713] = ("Buenos Aires", "Villa Gobernador Udaondo"),
        [1714] = ("Buenos Aires", "Ituzaingó"),
        [1716] = ("Buenos Aires", "Libertad"),
        [1718] = ("Buenos Aires", "San Antonio de Padua"),
        [1722] = ("Buenos Aires", "Merlo"),
        [1723] = ("Buenos Aires", "Mariano Acosta"),
        [1727] = ("Buenos Aires", "Marcos Paz"),
        [1731] = ("Buenos Aires", "Villars"),
        [1733] = ("Buenos Aires", "Plomer"),
        [1735] = ("Buenos Aires", "El Durazno"),
        [1737] = ("Buenos Aires", "La Choza"),
        [1739] = ("Buenos Aires", "General Hornos"),
        [1741] = ("Buenos Aires", "General Las Heras"),
        [1742] = ("Buenos Aires", "Paso del Rey"),
        [1744] = ("Buenos Aires", "Moreno"),
        [1746] = ("Buenos Aires", "Francisco Alvarez"),
        [1748] = ("Buenos Aires", "General Rodríguez"),
        [1752] = ("Buenos Aires", "Lomas del Mirador"),
        [1754] = ("Buenos Aires", "San Justo"),
        [1755] = ("Buenos Aires", "Rafael Castillo"),
        [1757] = ("Buenos Aires", "Laferrere"),
        [1759] = ("Buenos Aires", "González Catán"),
        [1761] = ("Buenos Aires", "Pontevedra"),
        [1763] = ("Buenos Aires", "Virrey del Pino"),
        [1765] = ("Buenos Aires", "Isidro Casanova"),
        [1766] = ("Buenos Aires", "Tablada"),
        [1768] = ("Buenos Aires", "Villa Madero"),
        [1770] = ("Buenos Aires", "Aldo Bonzi"),
        [1772] = ("Buenos Aires", "Villa Celina"),
        [1773] = ("Buenos Aires", "Ingeniero Budge"),
        [1776] = ("Buenos Aires", "9 de Abril"),
        [1778] = ("Buenos Aires", "Ciudad Evita"),
        [1802] = ("Buenos Aires", "Aeropuerto Ezeiza"),
        [1804] = ("Buenos Aires", "Ezeiza"),
        [1806] = ("Buenos Aires", "Tristán Suárez"),
        [1807] = ("Buenos Aires", "Carlos Spegazzini"),
        [1808] = ("Buenos Aires", "Vicente Casares"),
        [1812] = ("Buenos Aires", "Máximo Paz"),
        [1814] = ("Buenos Aires", "Cañuelas"),
        [1815] = ("Buenos Aires", "Uribelarrea"),
        [1816] = ("Buenos Aires", "Villa Adriana"),
        [1822] = ("Buenos Aires", "Valentín Alsina"),
        [1824] = ("Buenos Aires", "Lanús"),
        [1825] = ("Buenos Aires", "Monte Chingolo"),
        [1826] = ("Buenos Aires", "Remedios de Escalada"),
        [1828] = ("Buenos Aires", "Banfield"),
        [1832] = ("Buenos Aires", "Lomas de Zamora"),
        [1834] = ("Buenos Aires", "Temperley"),
        [1836] = ("Buenos Aires", "Llavallol"),
        [1838] = ("Buenos Aires", "Luis Guillón"),
        [1842] = ("Buenos Aires", "Monte Grande"),
        [1846] = ("Buenos Aires", "Adrogué"),
        [1847] = ("Buenos Aires", "Rafael Calzada"),
        [1848] = ("Buenos Aires", "Las Malvinas"),
        [1849] = ("Buenos Aires", "Claypole"),
        [1852] = ("Buenos Aires", "Burzaco"),
        [1854] = ("Buenos Aires", "Longchamps"),
        [1856] = ("Buenos Aires", "Glew"),
        [1858] = ("Buenos Aires", "Villa Numancia"),
        [1862] = ("Buenos Aires", "Guernica"),
        [1864] = ("Buenos Aires", "Alejandro Korn"),
        [1865] = ("Buenos Aires", "San Vicente"),
        [1870] = ("Buenos Aires", "Avellaneda"),
        [1871] = ("Buenos Aires", "Dock Sud"),
        [1872] = ("Buenos Aires", "Sarandí"),
        [1874] = ("Buenos Aires", "Villa Dominico"),
        [1875] = ("Buenos Aires", "Wilde"),
        [1876] = ("Buenos Aires", "Bernal"),
        [1878] = ("Buenos Aires", "Quilmes"),
        [1879] = ("Buenos Aires", "Quilmes Oeste"),
        [1881] = ("Buenos Aires", "San Francisco Solano"),
        [1882] = ("Buenos Aires", "Ezpeleta"),
        [1884] = ("Buenos Aires", "Berazategui"),
        [1885] = ("Buenos Aires", "Guillermo E. Hudson"),
        [1886] = ("Buenos Aires", "Ranelagh"),
        [1888] = ("Buenos Aires", "Florencio Varela"),
        [1889] = ("Buenos Aires", "El Rocio"),
        [1890] = ("Buenos Aires", "Juan María Gutiérrez"),
        [1891] = ("Buenos Aires", "Ingeniero Juan Allan"),
        [1893] = ("Buenos Aires", "Centro Agricola El Pato"),
        [1894] = ("Buenos Aires", "Villa Elisa"),
        [1895] = ("Buenos Aires", "Arturo Seguí"),
        [1896] = ("Buenos Aires", "City Bell"),
        [1897] = ("Buenos Aires", "Manuel B. Gonnet"),
        [1900] = ("Buenos Aires", "La Plata"),
        [1901] = ("Buenos Aires", "La Plata"),
        [1903] = ("Buenos Aires", "Melchor Romero"),
        [1905] = ("Buenos Aires", "Jose Ferrari"),
        [1907] = ("Buenos Aires", "El Pino"),
        [1909] = ("Buenos Aires", "Ignacio Correas"),
        [1911] = ("Buenos Aires", "General Mansilla"),
        [1913] = ("Buenos Aires", "Atalaya"),
        [1915] = ("Buenos Aires", "Roberto Payro"),
        [1917] = ("Buenos Aires", "Verónica"),
        [1919] = ("Buenos Aires", "Punta Indio"),
        [1921] = ("Buenos Aires", "Magdalena"),
        [1923] = ("Buenos Aires", "Berisso"),
        [1925] = ("Buenos Aires", "Ensenada"),
        [1927] = ("Buenos Aires", "Río Santiago"),
        [1929] = ("Buenos Aires", "Río Santiago"),
        [1931] = ("Buenos Aires", "Punta Lara"),
        [1980] = ("Buenos Aires", "Coronel Brandsen"),
        [1981] = ("Buenos Aires", "Gobernador Obligado"),
        [1983] = ("Buenos Aires", "Gomez De La Vega"),
        [1984] = ("Buenos Aires", "Domselaar"),
        [1986] = ("Buenos Aires", "Altamirano"),
        [1987] = ("Buenos Aires", "Ranchos"),
        [2800] = ("Buenos Aires", "Zárate"),
        [2801] = ("Buenos Aires", "Alto Verde"),
        [2802] = ("Buenos Aires", "Otamendi"),
        [2804] = ("Buenos Aires", "Campana"),
        [2805] = ("Buenos Aires", "Campana"),
        [2806] = ("Buenos Aires", "Lima"),
        [2808] = ("Buenos Aires", "Atucha"),
        [2812] = ("Buenos Aires", "Capilla del Señor"),
        [2813] = ("Buenos Aires", "Arroyo de la Cruz"),
        [2814] = ("Buenos Aires", "Los Cardales"),
        [2820] = ("Entre Ríos", "Gualeguaychú"),
        [2821] = ("Entre Ríos", "Rincón del Gato"),
        [2823] = ("Entre Ríos", "Ceibas"),
        [2824] = ("Entre Ríos", "Villa Faustino M. Parera"),
        [2826] = ("Entre Ríos", "Urdinarrain"),
        [2828] = ("Entre Ríos", "Escriña"),
        [2840] = ("Entre Ríos", "Gualeguay"),
        [2841] = ("Entre Ríos", "González Calderón"),
        [2843] = ("Entre Ríos", "General Galarza"),
        [2845] = ("Entre Ríos", "Gobernador Mansilla"),
        [2846] = ("Entre Ríos", "Holt - Ibicuy"),
        [2848] = ("Entre Ríos", "Médanos"),
        [2852] = ("Entre Ríos", "Enrique Carbó"),
        [2854] = ("Entre Ríos", "Larroque"),
        [2864] = ("Entre Ríos", "Km 340"),
        [2875] = ("Córdoba", "Quebracho Ladeado"),
    };

    internal static (string? Provincia, string? Localidad) LookupCp(int cp)
        => LOCALIDADES_CP.TryGetValue(cp, out var x) ? (x.Provincia, x.Localidad) : (null, "Código sin uso (no figura en el Correo)");
}
