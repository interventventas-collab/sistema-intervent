using System.Net.Http.Headers;
using System.Text.Json;
using Api.Data;
using Api.Services;
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
            lastSyncedAt = s.LastSyncedAt
        }));
    }

    public record SyncMe1Request(int Days = 30, int MaxOrders = 300);

    /// <summary>Trae los envios ME1 mas recientes de MeLi y los guarda localmente.</summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromBody] SyncMe1Request? req)
    {
        var r = await _service.SyncMe1Async(req?.Days ?? 30, req?.MaxOrders ?? 300);
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
        const string cacheKey = "me1:publicaciones:listado";
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

            foreach (var mla in ids)
            {
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
                        enBase = true
                    });
                }
                else
                {
                    resultado.Add(new { mla = mla, cuenta = account.Nickname, enBase = false });
                }
            }
        }

        var payload = new { total = resultado.Count, items = resultado };
        _cache.Set(cacheKey, payload, TimeSpan.FromMinutes(5));
        return Ok(payload);
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

    /// <summary>
    /// Devuelve la tabla de ZONAS acordadas con el usuario el 2026-06-08.
    /// Por ahora hardcodeada. Mañana puede pasar a una tabla DB editable.
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
            new { id = "la_plata",   nombre = "La Plata + extremos (Berisso, Ensenada, Zárate, Campana)", cpDesde = 1900, cpHasta = 2999, precio = 18000, color = "#a16207" },
        };
        return Ok(zonas);
    }
}
