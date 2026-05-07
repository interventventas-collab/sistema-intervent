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
