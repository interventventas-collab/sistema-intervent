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

    /// <summary>Lista pedidos disponibles para "traer" desde Nueva Venta (sin venta creada y no descartados).</summary>
    [HttpGet("disponibles-para-venta")]
    public async Task<IActionResult> DisponiblesParaVenta([FromQuery] int limit = 50)
    {
        var lista = await _db.WhatsAppPedidosRecibidos.AsNoTracking()
            .Where(p => p.Estado != "VENTA_CREADA" && p.Estado != "DESCARTADO")
            .OrderByDescending(p => p.RecibidoAt)
            .Take(Math.Clamp(limit, 1, 200))
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

    /// <summary>Vincula un pedido a una venta recién creada. Marca como VENTA_CREADA + asocia VentaId.</summary>
    public class VincularRequest { public int VentaId { get; set; } }

    [HttpPost("{id:int}/vincular-venta")]
    public async Task<IActionResult> VincularVenta(int id, [FromBody] VincularRequest req)
    {
        var p = await _db.WhatsAppPedidosRecibidos.FindAsync(id);
        if (p is null) return NotFound();
        p.VentaIdGenerada = req.VentaId;
        p.Estado = "VENTA_CREADA";
        p.VentaCreadaAt = DateTime.UtcNow;
        p.SeenAt ??= DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok();
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
        var trigger = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "whatsapp.pedidos.trigger");
        var pollEnabled = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "whatsapp.pedidos.poll_enabled");
        var autoResp = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "whatsapp.pedidos.auto_responder_enabled");
        var telefonos = await _db.WhatsAppPedidosTelefonos.AsNoTracking()
            .OrderBy(t => t.Id)
            .Select(t => new TelefonoDto { Id = t.Id, Telefono = t.Telefono, Etiqueta = t.Etiqueta, Activo = t.Activo, LastMessageId = t.LastMessageId, LastReadAt = t.LastReadAt })
            .ToListAsync();
        return Ok(new
        {
            trigger = trigger?.Value ?? "#PED",
            pollEnabled = string.Equals(pollEnabled?.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            autoResponderEnabled = autoResp is null ? true : string.Equals(autoResp.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            telefonos
        });
    }

    public class TelefonoDto
    {
        public int Id { get; set; }
        public string Telefono { get; set; } = "";
        public string? Etiqueta { get; set; }
        public bool Activo { get; set; }
        public string? LastMessageId { get; set; }
        public DateTime? LastReadAt { get; set; }
    }

    public class ConfigRequest
    {
        public string? Trigger { get; set; }
        public bool? PollEnabled { get; set; }
        public bool? AutoResponderEnabled { get; set; }
    }

    [HttpPost("config")]
    public async Task<IActionResult> SetConfig([FromBody] ConfigRequest req)
    {
        var trigger = string.IsNullOrWhiteSpace(req.Trigger) ? "#PED" : req.Trigger.Trim();

        async Task UpsertAsync(string key, string value)
        {
            var s = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == key);
            if (s is null) _db.AppSettings.Add(new Models.AppSetting { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
            else { s.Value = value; s.UpdatedAt = DateTime.UtcNow; }
        }
        await UpsertAsync("whatsapp.pedidos.trigger", trigger);
        if (req.PollEnabled.HasValue)
            await UpsertAsync("whatsapp.pedidos.poll_enabled", req.PollEnabled.Value ? "true" : "false");
        if (req.AutoResponderEnabled.HasValue)
            await UpsertAsync("whatsapp.pedidos.auto_responder_enabled", req.AutoResponderEnabled.Value ? "true" : "false");
        await _db.SaveChangesAsync();
        return Ok(new { trigger, pollEnabled = req.PollEnabled, autoResponderEnabled = req.AutoResponderEnabled });
    }

    // === Teléfonos autorizados ===

    public class TelefonoUpsertRequest
    {
        public string Telefono { get; set; } = "";
        public string? Etiqueta { get; set; }
        public bool? Activo { get; set; }
    }

    [HttpPost("telefonos")]
    public async Task<IActionResult> AgregarTelefono([FromBody] TelefonoUpsertRequest req)
    {
        var tel = NormalizarTelefono(req.Telefono);
        if (string.IsNullOrWhiteSpace(tel) || tel.Length < 8) return BadRequest(new { error = "Telefono invalido" });
        if (await _db.WhatsAppPedidosTelefonos.AnyAsync(t => t.Telefono == tel))
            return Conflict(new { error = "Ya existe" });
        var entity = new Models.WhatsAppPedidosTelefono
        {
            Telefono = tel,
            Etiqueta = string.IsNullOrWhiteSpace(req.Etiqueta) ? null : req.Etiqueta!.Trim(),
            Activo = req.Activo ?? true,
            CreatedAt = DateTime.UtcNow
        };
        _db.WhatsAppPedidosTelefonos.Add(entity);
        await _db.SaveChangesAsync();
        return Ok(new { entity.Id, entity.Telefono, entity.Etiqueta, entity.Activo });
    }

    [HttpPost("telefonos/{id:int}")]
    public async Task<IActionResult> EditarTelefono(int id, [FromBody] TelefonoUpsertRequest req)
    {
        var entity = await _db.WhatsAppPedidosTelefonos.FindAsync(id);
        if (entity is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(req.Telefono))
        {
            var tel = NormalizarTelefono(req.Telefono);
            if (string.IsNullOrEmpty(tel) || tel.Length < 8) return BadRequest(new { error = "Telefono invalido" });
            entity.Telefono = tel;
        }
        if (req.Etiqueta != null) entity.Etiqueta = string.IsNullOrWhiteSpace(req.Etiqueta) ? null : req.Etiqueta.Trim();
        if (req.Activo.HasValue) entity.Activo = req.Activo.Value;
        await _db.SaveChangesAsync();
        return Ok();
    }

    [HttpDelete("telefonos/{id:int}")]
    public async Task<IActionResult> BorrarTelefono(int id)
    {
        var entity = await _db.WhatsAppPedidosTelefonos.FindAsync(id);
        if (entity is null) return NotFound();
        _db.WhatsAppPedidosTelefonos.Remove(entity);
        await _db.SaveChangesAsync();
        return Ok();
    }

    /// <summary>Resetea el cursor de un teléfono (o todos si id=0). Útil para forzar re-lectura.</summary>
    [HttpPost("telefonos/{id:int}/reset-cursor")]
    public async Task<IActionResult> ResetCursorTelefono(int id)
    {
        if (id == 0)
        {
            var todos = await _db.WhatsAppPedidosTelefonos.ToListAsync();
            foreach (var t in todos) { t.LastMessageId = null; t.LastReadAt = null; }
        }
        else
        {
            var entity = await _db.WhatsAppPedidosTelefonos.FindAsync(id);
            if (entity is null) return NotFound();
            entity.LastMessageId = null; entity.LastReadAt = null;
        }
        await _db.SaveChangesAsync();
        return Ok();
    }

    /// <summary>Alias retrocompatible (la pantalla vieja usaba este endpoint).</summary>
    [HttpPost("reset-cursor")]
    public Task<IActionResult> ResetCursorLegacy() => ResetCursorTelefono(0);

    private static string NormalizarTelefono(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        return new string(input.Where(char.IsDigit).ToArray());
    }
}
