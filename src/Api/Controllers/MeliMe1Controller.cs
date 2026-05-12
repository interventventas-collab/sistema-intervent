using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

    public MeliMe1Controller(AppDbContext db, MeliShipmentService service, AuditLogService audit)
    {
        _db = db; _service = service; _audit = audit;
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
}
