using System.Text.Json;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Configuración del respondedor POST-VENTA de café: cuando alguien compra una de las
/// publicaciones elegidas, el sistema le manda al comprador un mensaje automático preguntando
/// la molienda/formato. Config en AppSettings (claves cafe.postventa.*) + envío real por MeLi.
/// </summary>
[ApiController]
[Route("api/meli/postventa")]
[Authorize]
public class MeliPostventaController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly MeliOrderService _orders;

    public MeliPostventaController(AppDbContext db, MeliOrderService orders)
    {
        _db = db;
        _orders = orders;
    }

    private async Task SetAsync(string key, string value)
    {
        var s = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == key);
        if (s is null) { s = new AppSetting { Key = key }; _db.AppSettings.Add(s); }
        s.Value = value;
        s.UpdatedAt = DateTime.UtcNow;
    }

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig()
    {
        var cfg = await _orders.LeerConfigPostventaAsync();
        var seleccionados = cfg.Items.ToHashSet();

        // Publicaciones de CAFÉ real: las vinculadas a un producto del módulo cuya categoría es
        // "CAFE" (el módulo también maneja Colombraro, bolsas, etc. como "OTROS" — esos NO van).
        var cafeProdIds = _db.CafeProductos.Where(c => c.Categoria == "CAFE").Select(c => c.Id);
        var raw = await _db.MeliItems
            .Where(i => i.CafeProductoId != null && cafeProdIds.Contains(i.CafeProductoId.Value))
            .Select(i => new { i.MeliItemId, i.Title, i.Thumbnail, i.Status })
            .ToListAsync();

        var publicaciones = raw
            .GroupBy(x => x.MeliItemId)
            .Select(g =>
            {
                var f = g.First();
                return new
                {
                    id = f.MeliItemId,
                    title = f.Title,
                    thumbnail = f.Thumbnail,
                    status = f.Status,
                    selected = seleccionados.Contains(f.MeliItemId)
                };
            })
            .ToList();

        // Asegurar que cualquier item elegido (aunque ya no figure como café) aparezca igual.
        var presentes = publicaciones.Select(p => p.id).ToHashSet();
        foreach (var mla in cfg.Items.Where(m => !presentes.Contains(m)))
        {
            var it = await _db.MeliItems.Where(i => i.MeliItemId == mla)
                .Select(i => new { i.Title, i.Thumbnail, i.Status }).FirstOrDefaultAsync();
            publicaciones.Add(new
            {
                id = mla,
                title = it?.Title ?? mla,
                thumbnail = it?.Thumbnail,
                status = it?.Status,
                selected = true
            });
        }

        var pubsOrdenadas = publicaciones
            .OrderByDescending(p => p.selected)
            .ThenBy(p => p.title)
            .ToList();

        // Ventas recientes de las publicaciones elegidas (para poder hacer una prueba real).
        var ventasRecientes = new List<object>();
        if (cfg.Items.Count > 0)
        {
            var desde = DateTime.UtcNow.AddDays(-30);
            var recientes = await _db.MeliOrders
                .Where(o => cfg.Items.Contains(o.ItemId) && o.DateCreated >= desde)
                .OrderByDescending(o => o.DateCreated)
                .Take(60)
                .Select(o => new { o.Id, o.MeliOrderId, o.BuyerNickname, o.ItemTitle, o.DateCreated, o.PostventaCafeMsgSentAt })
                .ToListAsync();

            ventasRecientes = recientes
                .GroupBy(o => o.MeliOrderId)
                .Select(g => g.First())
                .Take(30)
                .Select(o => (object)new
                {
                    orderId = o.Id,
                    meliOrderId = o.MeliOrderId,
                    buyer = o.BuyerNickname,
                    item = o.ItemTitle,
                    fecha = o.DateCreated,
                    yaEnviado = o.PostventaCafeMsgSentAt != null // solo envíos REALES (no el backfill)
                })
                .ToList();
        }

        return Ok(new
        {
            enabled = cfg.Enabled,
            mensajes = cfg.Mensajes,
            opciones = cfg.Opciones,
            publicaciones = pubsOrdenadas,
            ventasRecientes
        });
    }

    public record SaveConfigRequest(bool Enabled, List<string>? Mensajes, List<string>? Opciones, List<string>? Items);

    [HttpPut("config")]
    public async Task<IActionResult> SaveConfig([FromBody] SaveConfigRequest req)
    {
        var mensajes = (req.Mensajes ?? new List<string>())
            .Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).ToList();
        if (mensajes.Count == 0) mensajes = MeliOrderService.PostventaMensajesDefault.ToList();
        var opciones = (req.Opciones ?? new List<string>())
            .Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).ToList();
        if (opciones.Count == 0) opciones = MeliOrderService.PostventaOpcionesDefault.ToList();
        var items = (req.Items ?? new List<string>())
            .Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct().ToList();

        await SetAsync(MeliOrderService.CfgPostventaEnabled, req.Enabled ? "true" : "false");
        await SetAsync(MeliOrderService.CfgPostventaMensajes, JsonSerializer.Serialize(mensajes));
        await SetAsync(MeliOrderService.CfgPostventaOpciones, string.Join("\n", opciones));
        await SetAsync(MeliOrderService.CfgPostventaItems, JsonSerializer.Serialize(items));
        await _db.SaveChangesAsync();

        return Ok(new { ok = true });
    }

    /// <summary>Busca entre TODAS las publicaciones (no solo las detectadas como café) para poder
    /// agregar manualmente alguna que no aparezca en el listado principal.</summary>
    [HttpGet("buscar")]
    public async Task<IActionResult> Buscar([FromQuery] string? q)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2) return Ok(new { publicaciones = new List<object>() });

        var raw = await _db.MeliItems
            .Where(i => i.Title.Contains(q) || i.MeliItemId.Contains(q))
            .Select(i => new { i.MeliItemId, i.Title, i.Thumbnail, i.Status })
            .Take(400)
            .ToListAsync();

        var pubs = raw
            .GroupBy(x => x.MeliItemId)
            .Select(g =>
            {
                var f = g.First();
                return new { id = f.MeliItemId, title = f.Title, thumbnail = f.Thumbnail, status = f.Status, selected = false };
            })
            .OrderBy(p => p.title)
            .Take(50)
            .ToList();

        return Ok(new { publicaciones = pubs });
    }

    /// <summary>Prueba REAL: manda el mensaje al comprador de una venta puntual (elegida por el
    /// usuario). orderId = Id local de la fila MeliOrders.</summary>
    [HttpPost("probar/{orderId:int}")]
    public async Task<IActionResult> Probar(int orderId)
    {
        var order = await _db.MeliOrders.Include(o => o.MeliAccount)
            .FirstOrDefaultAsync(o => o.Id == orderId);
        if (order is null) return NotFound(new { ok = false, detalle = "No se encontró la venta." });
        if (order.MeliAccount is null) return BadRequest(new { ok = false, detalle = "La venta no tiene cuenta de MeLi asociada." });

        var cfg = await _orders.LeerConfigPostventaAsync();
        var text = MeliOrderService.ComponerMensajePostventa(MeliOrderService.ElegirSaludo(cfg.Mensajes), cfg.Opciones);
        long packId = order.PackId ?? order.MeliOrderId;

        var (ok, err) = await _orders.SendPackMessageAsync(packId, order.MeliAccount, order.BuyerId, text);
        if (!ok) return Ok(new { ok = false, detalle = err ?? "No se pudo enviar." });

        // Marcar todas las filas de esa orden como avisadas (con fecha real) para no duplicar.
        var rows = await _db.MeliOrders.Where(o => o.MeliOrderId == order.MeliOrderId).ToListAsync();
        var ahora = DateTime.UtcNow;
        foreach (var r in rows) { r.PostventaCafeMsgSent = true; r.PostventaCafeMsgSentAt = ahora; }
        await _db.SaveChangesAsync();

        return Ok(new { ok = true, detalle = $"Mensaje enviado a {order.BuyerNickname}." });
    }
}
