using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/meli/shipments")]
[Authorize]
public class MeliShipmentsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly MeliShipmentService _service;
    private readonly MeliLabelService _labelService;

    public MeliShipmentsController(AppDbContext db, MeliShipmentService service, MeliLabelService labelService)
    {
        _db = db; _service = service; _labelService = labelService;
    }

    /// <summary>
    /// 2026-08-13: Devuelve la etiqueta de envio oficial de MeLi lista para imprimir, INLINE (se abre
    /// en el navegador). Parametros:
    ///   - ids: numeros de envio (ShippingId) separados por coma.
    ///   - formato: "termica" (una por pagina 10x15), "a4-1" (una por hoja A4) o "a4-3" (tres por hoja A4).
    /// Se abre via window.open, asi que la cookie httpOnly del JWT viaja sola (mismo origen).
    /// </summary>
    [HttpGet("label")]
    public async Task<IActionResult> Label([FromQuery] string ids, [FromQuery] string formato = "a4-3")
    {
        var idArr = (ids ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var n) ? n : 0)
            .Where(n => n > 0)
            .Distinct()
            .Take(100)
            .ToArray();

        var r = await _labelService.GetLabelsPdfAsync(idArr, formato);

        if (!r.Ok || r.Pdf is null)
        {
            // Pagina de error legible en la pestana nueva (en vez de un JSON crudo).
            var msg = System.Net.WebUtility.HtmlEncode(r.Error ?? "Error desconocido al generar la etiqueta.");
            var html = "<!doctype html><html lang='es'><head><meta charset='utf-8'>" +
                       "<meta name='viewport' content='width=device-width, initial-scale=1'>" +
                       "<title>Etiqueta</title></head>" +
                       "<body style='font-family:system-ui,sans-serif;max-width:640px;margin:3rem auto;padding:0 1.25rem;'>" +
                       "<h2 style='color:#b91c1c;margin:0 0 .5rem;'>No se pudo generar la etiqueta</h2>" +
                       $"<p style='color:#374151;line-height:1.5;'>{msg}</p>" +
                       "<p style='color:#6b7280;font-size:.9rem;'>Tip: la etiqueta recien aparece cuando el envio esta " +
                       "en estado <b>listo para imprimir</b>. Si acabas de pagar/despachar, proba de nuevo en unos minutos.</p>" +
                       "</body></html>";
            return Content(html, "text/html");
        }

        var nombre = idArr.Length == 1 ? $"etiqueta-{idArr[0]}.pdf" : $"etiquetas-{idArr.Length}.pdf";
        Response.Headers["Content-Disposition"] = $"inline; filename=\"{nombre}\"";
        return File(r.Pdf, "application/pdf");
    }

    /// <summary>Lista los envios Flex (self_service) cargados localmente, ordenados por fecha.</summary>
    [HttpGet("flex")]
    public async Task<IActionResult> ListFlex(
        [FromQuery] string? status = null,
        [FromQuery] string? internalStatus = null,
        [FromQuery] string mode = "today",
        [FromQuery] bool excludeDelivered = false)
    {
        // mode = today | tomorrow | next3 | next7 | overdue | all
        // Filtra por EstimatedDeliveryLimit (la fecha límite de entrega que MeLi compromete al comprador).
        // Fallback: si EstimatedDeliveryLimit es null, usa DateCreated.
        var nowLocal = DateTime.UtcNow.AddHours(-3); // Ajuste a hora Argentina
        var todayLocal = nowLocal.Date;

        var q = _db.MeliShipments
            .Include(s => s.MeliAccount)
            .Where(s => s.LogisticType == "self_service");

        switch (mode.ToLowerInvariant())
        {
            case "today":
                {
                    var t1 = todayLocal.AddHours(3); // back to UTC
                    var t2 = todayLocal.AddDays(1).AddHours(3);
                    q = q.Where(s => (s.EstimatedDeliveryLimit ?? s.DateCreated) >= t1
                                  && (s.EstimatedDeliveryLimit ?? s.DateCreated) < t2);
                    break;
                }
            case "tomorrow":
                {
                    var t1 = todayLocal.AddDays(1).AddHours(3);
                    var t2 = todayLocal.AddDays(2).AddHours(3);
                    q = q.Where(s => (s.EstimatedDeliveryLimit ?? s.DateCreated) >= t1
                                  && (s.EstimatedDeliveryLimit ?? s.DateCreated) < t2);
                    break;
                }
            case "next3":
                {
                    var t1 = todayLocal.AddHours(3);
                    var t2 = todayLocal.AddDays(3).AddHours(3);
                    q = q.Where(s => (s.EstimatedDeliveryLimit ?? s.DateCreated) >= t1
                                  && (s.EstimatedDeliveryLimit ?? s.DateCreated) < t2);
                    break;
                }
            case "next7":
                {
                    var t1 = todayLocal.AddHours(3);
                    var t2 = todayLocal.AddDays(7).AddHours(3);
                    q = q.Where(s => (s.EstimatedDeliveryLimit ?? s.DateCreated) >= t1
                                  && (s.EstimatedDeliveryLimit ?? s.DateCreated) < t2);
                    break;
                }
            case "overdue":
                {
                    // Vencidos: SLA pasado y NO entregado
                    var nowUtc = DateTime.UtcNow;
                    q = q.Where(s => s.EstimatedDeliveryLimit != null
                                  && s.EstimatedDeliveryLimit < nowUtc
                                  && s.Status != "delivered" && s.Status != "cancelled");
                    break;
                }
            case "all":
            default:
                // Sin filtro adicional de fecha
                break;
        }

        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(s => s.Status == status);
        if (!string.IsNullOrWhiteSpace(internalStatus)) q = q.Where(s => s.InternalStatus == internalStatus);
        if (excludeDelivered) q = q.Where(s => s.Status != "delivered" && s.Status != "cancelled");

        var listFlex = await q
            .OrderBy(s => s.EstimatedDeliveryLimit ?? s.DateCreated)
            .Take(500).ToListAsync();

        // Sumamos los envios ME1 (mode='me1') que esten pendientes o en camino — sin filtrar por SLA,
        // porque ME1 normalmente no tiene EstimatedDeliveryLimit y son envios que el usuario entrega
        // personalmente cuando puede. Aparecen siempre en el mapa con badge "ME1" para diferenciarlos.
        var listMe1 = await _db.MeliShipments
            .Include(s => s.MeliAccount)
            .Where(s => s.Mode == "me1"
                     && s.Status != "delivered"
                     && s.Status != "not_delivered"
                     && s.Status != "cancelled")
            .OrderBy(s => s.DateCreated)
            .Take(200)
            .ToListAsync();

        var combined = listFlex.Concat(listMe1).ToList();

        return Ok(combined.Select(s => new
        {
            id = s.Id,
            meliShipmentId = s.MeliShipmentId,
            meliOrderId = s.MeliOrderId,
            cuenta = s.MeliAccount != null ? s.MeliAccount.Nickname : null,
            status = s.Status,
            substatus = s.Substatus,
            internalStatus = s.InternalStatus,
            mode = s.Mode,
            logisticType = s.LogisticType,
            trackingNumber = s.TrackingNumber,
            receiverName = s.ReceiverName,
            receiverPhone = s.ReceiverPhone,
            buyerNickname = s.BuyerNickname,
            addressLine = s.AddressLine,
            neighborhood = s.Neighborhood,
            city = s.City,
            state = s.State,
            zipCode = s.ZipCode,
            latitude = s.Latitude,
            longitude = s.Longitude,
            geolocationType = s.GeolocationType,
            comment = s.Comment,
            itemsSummary = s.ItemsSummary,
            orderTotal = s.OrderTotal,
            dateCreated = s.DateCreated,
            dateReadyToShip = s.DateReadyToShip,
            dateShipped = s.DateShipped,
            dateDelivered = s.DateDelivered,
            estimatedDeliveryFinal = s.EstimatedDeliveryFinal,
            estimatedDeliveryLimit = s.EstimatedDeliveryLimit,
            notes = s.Notes
        }));
    }

    public record SyncFlexRequest(int Days = 7, int MaxOrders = 200);

    /// <summary>Sincroniza envios Flex desde MeLi (las ultimas N ordenes).</summary>
    [HttpPost("sync-flex")]
    public async Task<IActionResult> SyncFlex([FromBody] SyncFlexRequest? req)
    {
        var r = await _service.SyncFlexAsync(req?.Days ?? 7, req?.MaxOrders ?? 200);
        return Ok(new { totalSynced = r.TotalSynced, totalFlex = r.TotalFlex, totalErrors = r.TotalErrors, errores = r.Errors });
    }

    /// <summary>Fuerza el refresco del estado de entrega de los envíos Flex/ME1 pendientes (botón "Actualizar entregas").</summary>
    [HttpPost("refresh-pending")]
    public async Task<IActionResult> RefreshPending()
    {
        var updated = await _service.RefreshPendingShipmentStatusesAsync();
        return Ok(new { count = updated });
    }

    /// <summary>Cuenta los envíos Flex que MeLi confirmó ENTREGADOS hoy (por DateDelivered, hora Argentina).
    /// Se usa para el contador "X entregados hoy" del Mapeo, incluso con los entregados ocultos.</summary>
    [HttpGet("flex/delivered-today")]
    public async Task<IActionResult> DeliveredToday()
    {
        var todayLocal = DateTime.UtcNow.AddHours(-3).Date;
        var t1 = todayLocal.AddHours(3);            // hoy 00:00 ART -> UTC
        var t2 = todayLocal.AddDays(1).AddHours(3); // mañana 00:00 ART -> UTC
        var count = await _db.MeliShipments
            .Where(s => s.LogisticType == "self_service"
                     && s.Status == "delivered"
                     && s.DateDelivered != null
                     && s.DateDelivered >= t1 && s.DateDelivered < t2)
            .CountAsync();
        return Ok(new { count });
    }

    /// <summary>2026-07-17: fuerza a preguntarle a MeLi el telefono del comprador de UN envio en el momento
    /// (boton "Traer telefono ahora"). Si MeLi ya lo libero, lo devuelve y lo deja escrito en la nota de la venta.</summary>
    [HttpPost("traer-telefono/{meliShipmentId:long}")]
    public async Task<IActionResult> TraerTelefono(long meliShipmentId)
    {
        var (ok, phone, notePosted) = await _service.TraerTelefonoYNotaAsync(meliShipmentId);
        if (!ok)
            return Ok(new { ok = false, phone = (string?)null, notePosted = false, message = "No se pudo consultar el envío en MeLi." });
        if (string.IsNullOrWhiteSpace(phone))
            return Ok(new { ok = true, phone = (string?)null, notePosted = false, message = "MeLi todavía no liberó el teléfono de este envío. Probá de nuevo más tarde." });
        return Ok(new { ok = true, phone, notePosted, message = notePosted ? "Teléfono traído y guardado en la nota de la venta." : "Teléfono traído." });
    }

    public record StartPointDto(string? Address, decimal? Lat, decimal? Lng, string? Time);

    /// <summary>Devuelve el punto de partida configurado para el mapa de rutas.</summary>
    [HttpGet("start-point")]
    public async Task<IActionResult> GetStartPoint()
    {
        var addr = (await _db.AppSettings.FindAsync("mapeo.start.address"))?.Value;
        var latStr = (await _db.AppSettings.FindAsync("mapeo.start.lat"))?.Value;
        var lngStr = (await _db.AppSettings.FindAsync("mapeo.start.lng"))?.Value;
        var time = (await _db.AppSettings.FindAsync("mapeo.start.time"))?.Value;
        decimal? lat = decimal.TryParse(latStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var la) ? la : null;
        decimal? lng = decimal.TryParse(lngStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lo) ? lo : null;
        return Ok(new StartPointDto(addr, lat, lng, string.IsNullOrWhiteSpace(time) ? null : time));
    }

    /// <summary>Setea el punto de partida (direccion + coordenadas + horario).</summary>
    [HttpPut("start-point")]
    public async Task<IActionResult> SetStartPoint([FromBody] StartPointDto req)
    {
        async Task Upsert(string key, string? value)
        {
            var existing = await _db.AppSettings.FindAsync(key);
            if (existing is null) _db.AppSettings.Add(new Api.Models.AppSetting { Key = key, Value = value ?? "", UpdatedAt = DateTime.UtcNow });
            else { existing.Value = value ?? ""; existing.UpdatedAt = DateTime.UtcNow; }
        }
        await Upsert("mapeo.start.address", req.Address);
        await Upsert("mapeo.start.lat", req.Lat?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Upsert("mapeo.start.lng", req.Lng?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Upsert("mapeo.start.time", req.Time);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    public record PublicBaseUrlDto(string? Url);

    /// <summary>
    /// URL pública del sistema (https://midominio.com) para armar los links de los choferes.
    /// Si no está seteada, se usa la URL del navegador del admin (que puede ser localhost:3000 — feo).
    /// </summary>
    [HttpGet("public-base-url")]
    public async Task<IActionResult> GetPublicBaseUrl()
    {
        var url = (await _db.AppSettings.FindAsync("mapeo.public_base_url"))?.Value;
        return Ok(new PublicBaseUrlDto(url));
    }

    [HttpPut("public-base-url")]
    public async Task<IActionResult> SetPublicBaseUrl([FromBody] PublicBaseUrlDto req)
    {
        var existing = await _db.AppSettings.FindAsync("mapeo.public_base_url");
        var v = string.IsNullOrWhiteSpace(req.Url) ? "" : req.Url.Trim().TrimEnd('/');
        if (existing is null) _db.AppSettings.Add(new Api.Models.AppSetting { Key = "mapeo.public_base_url", Value = v, UpdatedAt = DateTime.UtcNow });
        else { existing.Value = v; existing.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    public record GeocodeResult(string DisplayName, decimal Lat, decimal Lng);

    /// <summary>Busca una direccion con Google Geocoding (misma clave que las rutas) y devuelve hasta 5 candidatos con coordenadas. Si no hay clave de Google, cae a OpenStreetMap.</summary>
    [HttpGet("geocode")]
    public async Task<IActionResult> Geocode([FromQuery] string q, [FromServices] IHttpClientFactory httpFactory, [FromServices] IConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(q)) return BadRequest(new { error = "Tenes que escribir una direccion" });
        var http = httpFactory.CreateClient();
        var googleKey = config["GOOGLE_MAPS_API_KEY"] ?? Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY") ?? "";

        // Camino principal: Google Geocoding API (misma clave que ya usan las rutas).
        if (!string.IsNullOrWhiteSpace(googleKey))
        {
            try
            {
                http.Timeout = TimeSpan.FromSeconds(8);
                var gurl = $"https://maps.googleapis.com/maps/api/geocode/json?address={Uri.EscapeDataString(q)}&region=ar&language=es&key={googleKey}";
                var gresp = await http.GetAsync(gurl);
                if (gresp.IsSuccessStatusCode)
                {
                    var gbody = await gresp.Content.ReadAsStringAsync();
                    using var gdoc = System.Text.Json.JsonDocument.Parse(gbody);
                    var root = gdoc.RootElement;
                    var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
                    var glist = new List<GeocodeResult>();
                    if (status == "OK" && root.TryGetProperty("results", out var results))
                    {
                        foreach (var el in results.EnumerateArray())
                        {
                            string? display = el.TryGetProperty("formatted_address", out var fa) ? fa.GetString() : null;
                            if (display is null || !el.TryGetProperty("geometry", out var geom) || !geom.TryGetProperty("location", out var loc)) continue;
                            var lat = loc.GetProperty("lat").GetDecimal();
                            var lng = loc.GetProperty("lng").GetDecimal();
                            glist.Add(new GeocodeResult(display, lat, lng));
                            if (glist.Count >= 5) break;
                        }
                    }
                    return Ok(glist);
                }
            }
            catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
        }

        // Respaldo: OpenStreetMap (Nominatim, gratis) si no hay clave de Google configurada.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ai-ml-app/1.0 (mapeo flex)");
        var url = $"https://nominatim.openstreetmap.org/search?format=json&limit=5&countrycodes=ar&q={Uri.EscapeDataString(q)}";
        try
        {
            var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return Ok(new List<GeocodeResult>());
            var body = await resp.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(body).RootElement;
            var list = new List<GeocodeResult>();
            foreach (var el in doc.EnumerateArray())
            {
                string? display = el.TryGetProperty("display_name", out var dn) ? dn.GetString() : null;
                string? latS = el.TryGetProperty("lat", out var la) ? la.GetString() : null;
                string? lonS = el.TryGetProperty("lon", out var lo) ? lo.GetString() : null;
                if (display is null || latS is null || lonS is null) continue;
                if (!decimal.TryParse(latS, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lat)) continue;
                if (!decimal.TryParse(lonS, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lng)) continue;
                list.Add(new GeocodeResult(display, lat, lng));
            }
            return Ok(list);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Devuelve una imagen de Google Street View para unas coordenadas (proxy para no exponer la clave). Sirve para confirmar visualmente el punto.</summary>
    [HttpGet("streetview")]
    public async Task<IActionResult> StreetView([FromQuery] decimal lat, [FromQuery] decimal lng, [FromServices] IHttpClientFactory httpFactory, [FromServices] IConfiguration config)
    {
        var key = config["GOOGLE_MAPS_API_KEY"] ?? Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY") ?? "";
        if (string.IsNullOrWhiteSpace(key)) return NotFound();
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(8);
        var latS = lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var lngS = lng.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            // Primero preguntamos si hay foto de Street View en ese punto (metadata gratis).
            var metaUrl = $"https://maps.googleapis.com/maps/api/streetview/metadata?location={latS},{lngS}&key={key}";
            var metaResp = await http.GetAsync(metaUrl);
            if (metaResp.IsSuccessStatusCode)
            {
                var metaBody = await metaResp.Content.ReadAsStringAsync();
                using var mdoc = System.Text.Json.JsonDocument.Parse(metaBody);
                var st = mdoc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (st != "OK") return NoContent(); // 204: no hay foto en ese punto
            }
            var imgUrl = $"https://maps.googleapis.com/maps/api/streetview?size=600x300&location={latS},{lngS}&fov=80&source=outdoor&key={key}";
            var imgResp = await http.GetAsync(imgUrl);
            if (!imgResp.IsSuccessStatusCode) return NoContent();
            var bytes = await imgResp.Content.ReadAsByteArrayAsync();
            return File(bytes, "image/jpeg");
        }
        catch { return NoContent(); }
    }

    public record UpdateInternalStatusRequest(string InternalStatus, string? Notes);

    /// <summary>Actualiza el estado interno (en_ruta/entregado/no_encontrado/etc.) y notas del envio.</summary>
    [HttpPut("{id:int}/internal-status")]
    public async Task<IActionResult> UpdateInternalStatus(int id, [FromBody] UpdateInternalStatusRequest req)
    {
        var s = await _db.MeliShipments.FindAsync(id);
        if (s is null) return NotFound(new { error = "Envio no encontrado" });
        s.InternalStatus = string.IsNullOrWhiteSpace(req.InternalStatus) ? "pending" : req.InternalStatus.Trim().ToLower();
        if (req.Notes is not null) s.Notes = req.Notes;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
