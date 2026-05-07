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

    public MeliShipmentsController(AppDbContext db, MeliShipmentService service) { _db = db; _service = service; }

    /// <summary>Lista los envios Flex (self_service) cargados localmente, ordenados por fecha.</summary>
    [HttpGet("flex")]
    public async Task<IActionResult> ListFlex(
        [FromQuery] string? status = null,
        [FromQuery] string? internalStatus = null,
        [FromQuery] int days = 7,
        [FromQuery] bool excludeDelivered = false)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var q = _db.MeliShipments
            .Include(s => s.MeliAccount)
            .Where(s => s.LogisticType == "self_service")
            .Where(s => s.DateCreated == null || s.DateCreated >= since);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(s => s.Status == status);
        if (!string.IsNullOrWhiteSpace(internalStatus)) q = q.Where(s => s.InternalStatus == internalStatus);
        if (excludeDelivered) q = q.Where(s => s.Status != "delivered" && s.Status != "cancelled");

        var list = await q.OrderByDescending(s => s.DateCreated).Take(500).ToListAsync();
        return Ok(list.Select(s => new
        {
            id = s.Id,
            meliShipmentId = s.MeliShipmentId,
            meliOrderId = s.MeliOrderId,
            cuenta = s.MeliAccount != null ? s.MeliAccount.Nickname : null,
            status = s.Status,
            substatus = s.Substatus,
            internalStatus = s.InternalStatus,
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

    public record StartPointDto(string? Address, decimal? Lat, decimal? Lng);

    /// <summary>Devuelve el punto de partida configurado para el mapa de rutas.</summary>
    [HttpGet("start-point")]
    public async Task<IActionResult> GetStartPoint()
    {
        var addr = (await _db.AppSettings.FindAsync("mapeo.start.address"))?.Value;
        var latStr = (await _db.AppSettings.FindAsync("mapeo.start.lat"))?.Value;
        var lngStr = (await _db.AppSettings.FindAsync("mapeo.start.lng"))?.Value;
        decimal? lat = decimal.TryParse(latStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var la) ? la : null;
        decimal? lng = decimal.TryParse(lngStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lo) ? lo : null;
        return Ok(new StartPointDto(addr, lat, lng));
    }

    /// <summary>Setea el punto de partida (direccion + coordenadas).</summary>
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

    /// <summary>Busca una direccion en OpenStreetMap (Nominatim, gratis) y devuelve hasta 5 candidatos con coordenadas.</summary>
    [HttpGet("geocode")]
    public async Task<IActionResult> Geocode([FromQuery] string q, [FromServices] IHttpClientFactory httpFactory)
    {
        if (string.IsNullOrWhiteSpace(q)) return BadRequest(new { error = "Tenes que escribir una direccion" });
        var http = httpFactory.CreateClient();
        // Nominatim pide un User-Agent identificable; buscamos en Argentina por default.
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
