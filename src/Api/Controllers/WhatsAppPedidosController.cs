using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Api.Controllers;

[ApiController]
[Route("api/whatsapp/pedidos")]
[Authorize]
public class WhatsAppPedidosController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly WhatsAppPedidoService _svc;

    public WhatsAppPedidosController(AppDbContext db, WhatsAppPedidoService svc)
    {
        _db = db; _svc = svc;
    }

    public class RecibirPedidoRequest
    {
        public string Telefono { get; set; } = "";
        public string Texto { get; set; } = "";
    }

    /// <summary>Recibe un pedido manual (paste). Parsea con IA y guarda en estado PARSEADO o ERROR.</summary>
    [HttpPost("recibir")]
    public async Task<IActionResult> Recibir([FromBody] RecibirPedidoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Texto))
            return BadRequest(new { error = "Texto vacío" });
        var pedido = await _svc.RecibirPedidoAsync(req.Telefono, req.Texto, "manual", HttpContext.RequestAborted);
        return Ok(new { id = pedido.Id, estado = pedido.Estado, error = pedido.ParseError });
    }

    public class PedidoListItemDto
    {
        public int Id { get; set; }
        public string Telefono { get; set; } = "";
        public string TextoCrudo { get; set; } = "";
        public int? ClienteId { get; set; }
        public string? ClienteNombre { get; set; }
        public string? ProductosParseados { get; set; }
        public string? ParseError { get; set; }
        public string Estado { get; set; } = "";
        public int? VentaIdGenerada { get; set; }
        public DateTime RecibidoAt { get; set; }
        public string Source { get; set; } = "";
        public DateTime? SeenAt { get; set; }
    }

    /// <summary>Lista pedidos recibidos, ordenados por fecha desc.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] string? estado = null, [FromQuery] bool soloSinVer = false, [FromQuery] int limit = 100)
    {
        var q = _db.WhatsAppPedidosRecibidos.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(estado)) q = q.Where(p => p.Estado == estado);
        if (soloSinVer) q = q.Where(p => p.SeenAt == null);
        var lista = await q.OrderByDescending(p => p.RecibidoAt).Take(Math.Clamp(limit, 1, 500))
            .Select(p => new PedidoListItemDto
            {
                Id = p.Id, Telefono = p.Telefono, TextoCrudo = p.TextoCrudo,
                ClienteId = p.ClienteId, ClienteNombre = p.ClienteNombre,
                ProductosParseados = p.ProductosParseados, ParseError = p.ParseError,
                Estado = p.Estado, VentaIdGenerada = p.VentaIdGenerada,
                RecibidoAt = p.RecibidoAt, Source = p.Source, SeenAt = p.SeenAt
            })
            .ToListAsync();
        return Ok(lista);
    }

    [HttpGet("count-pending")]
    public async Task<IActionResult> CountPending()
    {
        var c = await _svc.CountUnseenAsync(HttpContext.RequestAborted);
        return Ok(new { count = c });
    }

    [HttpPost("{id:int}/mark-seen")]
    public async Task<IActionResult> MarkSeen(int id)
    {
        var p = await _db.WhatsAppPedidosRecibidos.FindAsync(id);
        if (p is null) return NotFound();
        p.SeenAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok();
    }

    [HttpPost("{id:int}/descartar")]
    public async Task<IActionResult> Descartar(int id)
    {
        var p = await _db.WhatsAppPedidosRecibidos.FindAsync(id);
        if (p is null) return NotFound();
        p.Estado = "DESCARTADO";
        p.SeenAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok();
    }

    /// <summary>Re-parsea el texto con IA. Útil si el catálogo cambió o si dio error la primera vez.</summary>
    [HttpPost("{id:int}/re-parsear")]
    public async Task<IActionResult> ReParsear(int id)
    {
        var p = await _db.WhatsAppPedidosRecibidos.FindAsync(id);
        if (p is null) return NotFound();
        var parsed = await _svc.ParseTextoAsync(p.TextoCrudo, HttpContext.RequestAborted);
        p.ClienteNombre = parsed.ClienteNombre;
        p.ClienteId = parsed.ClienteId;
        p.ProductosParseados = JsonSerializer.Serialize(parsed);
        p.ParseadoAt = DateTime.UtcNow;
        p.Estado = string.IsNullOrEmpty(parsed.Error) ? "PARSEADO" : "ERROR";
        p.ParseError = parsed.Error;
        await _db.SaveChangesAsync();
        return Ok(new { p.Id, p.Estado, p.ParseError });
    }

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig()
    {
        var telefono = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "whatsapp.pedidos.vendedor_telefono");
        var trigger = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "whatsapp.pedidos.trigger");
        return Ok(new { telefono = telefono?.Value ?? "", trigger = trigger?.Value ?? "#PEDIDO" });
    }

    public class ConfigRequest { public string? Telefono { get; set; } public string? Trigger { get; set; } }

    [HttpPost("config")]
    public async Task<IActionResult> SetConfig([FromBody] ConfigRequest req)
    {
        var telefono = (req.Telefono ?? "").Trim();
        var trigger = string.IsNullOrWhiteSpace(req.Trigger) ? "#PEDIDO" : req.Trigger.Trim();
        var s1 = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "whatsapp.pedidos.vendedor_telefono");
        if (s1 is null) { _db.AppSettings.Add(new Models.AppSetting { Key = "whatsapp.pedidos.vendedor_telefono", Value = telefono, UpdatedAt = DateTime.UtcNow }); }
        else { s1.Value = telefono; s1.UpdatedAt = DateTime.UtcNow; }
        var s2 = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "whatsapp.pedidos.trigger");
        if (s2 is null) { _db.AppSettings.Add(new Models.AppSetting { Key = "whatsapp.pedidos.trigger", Value = trigger, UpdatedAt = DateTime.UtcNow }); }
        else { s2.Value = trigger; s2.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
        return Ok(new { telefono, trigger });
    }
}
