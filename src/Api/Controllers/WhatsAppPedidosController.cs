using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Api.Controllers;

[ApiController]
[Route("api/whatsapp/pedidos")]
[Authorize]
public class WhatsAppPedidosController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly WhatsAppPedidoService _svc;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<WhatsAppPedidosController> _log;
    private readonly IServiceScopeFactory _scopeFactory;

    public WhatsAppPedidosController(AppDbContext db, WhatsAppPedidoService svc, IHttpClientFactory httpFactory, ILogger<WhatsAppPedidosController> log, IServiceScopeFactory scopeFactory)
    {
        _db = db; _svc = svc; _httpFactory = httpFactory; _log = log; _scopeFactory = scopeFactory;
    }

    public class RecibirPedidoRequest
    {
        public string Telefono { get; set; } = "";
        public string Texto { get; set; } = "";
        /// <summary>2026-07-23: cliente ya conocido por el que llama (ej. contacto del chat
        /// vinculado). Solo se usa si el texto no trae #código.</summary>
        public int? ClienteId { get; set; }
        /// <summary>manual (paste en la pantalla) | whatsapp_chat (botones del chat)</summary>
        public string? Source { get; set; }
    }

    /// <summary>Recibe un pedido manual (paste o botones del chat). Parsea con IA (si el
    /// interruptor está prendido) y guarda en estado PARSEADO o ERROR.</summary>
    [HttpPost("recibir")]
    public async Task<IActionResult> Recibir([FromBody] RecibirPedidoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Texto))
            return BadRequest(new { error = "Texto vacío" });
        var source = string.IsNullOrWhiteSpace(req.Source) ? "manual" : req.Source.Trim();
        var pedido = await _svc.RecibirPedidoAsync(req.Telefono, req.Texto, source, HttpContext.RequestAborted, req.ClienteId);
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
        public string TipoSolicitado { get; set; } = "PEDIDO";
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
                RecibidoAt = p.RecibidoAt, Source = p.Source, SeenAt = p.SeenAt,
                TipoSolicitado = p.TipoSolicitado
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
                RecibidoAt = p.RecibidoAt, Source = p.Source, SeenAt = p.SeenAt,
                TipoSolicitado = p.TipoSolicitado
            })
            .ToListAsync();
        return Ok(lista);
    }

    /// <summary>Vincula un pedido a una venta recién creada. Marca como VENTA_CREADA + asocia VentaId.
    /// Además envía al emisor del pedido un WhatsApp con el detalle de los items y el total.</summary>
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

        // Mandar segunda respuesta WhatsApp con el detalle de la venta cargada (si esta autorizada la auto-respuesta).
        // IMPORTANTE: usar IServiceScopeFactory para tener un DbContext propio, porque el _db inyectado
        // se dispose al terminar el request HTTP, antes que corra esta Task.
        var telefonoDest = p.Telefono;
        var pedidoIdLocal = p.Id;
        var ventaIdLocal = req.VentaId;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await EnviarDetalleVentaPorWhatsApp(db, telefonoDest, pedidoIdLocal, ventaIdLocal, CancellationToken.None);
            }
            catch (Exception ex) { _log.LogError(ex, "[WA pedidos] error enviando detalle venta {VentaId}", ventaIdLocal); }
        });

        return Ok();
    }

    /// <summary>Arma el mensaje con detalle de la venta y se lo manda al telefono que originalmente envio el pedido.</summary>
    private async Task EnviarDetalleVentaPorWhatsApp(AppDbContext db, string telefonoDestino, int pedidoId, int ventaId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(telefonoDestino)) return;

        // Chequear si auto-respuesta esta activada
        var arSetting = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "whatsapp.pedidos.auto_responder_enabled", ct);
        var autoRespOn = arSetting is null || string.Equals(arSetting.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        if (!autoRespOn) return;

        // Cargar venta con items
        var venta = await db.CafeVentas.AsNoTracking()
            .Include(v => v.Items)
            .FirstOrDefaultAsync(v => v.Id == ventaId, ct);
        if (venta is null) return;

        // Armar el texto
        var sb = new StringBuilder();
        sb.AppendLine($"✅ PEDIDO #{pedidoId} CARGADO (venta {venta.Numero})");
        sb.AppendLine();
        var nombreCli = venta.ClienteNombreSnapshot ?? "Consumidor final";
        sb.AppendLine(nombreCli);
        sb.AppendLine();
        foreach (var it in venta.Items.OrderBy(i => i.Id))
        {
            // Formato: • 25x Cafe Brasil Premium 1KG — $135.000,00
            var formato = !string.IsNullOrEmpty(it.Formato) && it.Formato != "UNIT" ? $" {it.Formato}" : "";
            var subtotalStr = it.Subtotal == 0 ? "sin cargo" : $"${it.Subtotal:N2}";
            sb.AppendLine($"• {it.Cantidad}x {it.ProductoNombreSnapshot}{formato} — {subtotalStr}");
        }
        sb.AppendLine();
        if (venta.Descuento > 0) sb.AppendLine($"Descuento: -${venta.Descuento:N2}");
        sb.AppendLine($"*TOTAL: ${venta.Total:N2}*");
        sb.AppendLine();
        var tipoStr = venta.TipoComprobante switch
        {
            "FA" => "Factura A",
            "FB" => "Factura B",
            "FC" => "Factura C",
            "PRO" => "Proforma",
            _ => "Remito interno"
        };
        var cpStr = venta.CondicionPago switch
        {
            "EFECTIVO" => "Efectivo",
            "TRANSFERENCIA" => "Transferencia",
            "MERCADOPAGO" => "Mercado Pago",
            "DEBITO" => "Débito",
            "CREDITO" => "Crédito",
            "CTA_CORRIENTE" => "Cta. Cte.",
            "CHEQUE" => "Cheque",
            _ => venta.CondicionPago
        };
        sb.Append(tipoStr).Append(" · ").Append(cpStr);
        if (!string.IsNullOrEmpty(venta.EntregaPor))
            sb.Append(" · ").Append(venta.EntregaPor);
        sb.AppendLine();

        var texto = sb.ToString();
        var playwrightUrl = Environment.GetEnvironmentVariable("PLAYWRIGHT_URL") ?? "http://playwright:3001";
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(2);
        try
        {
            var body = new { recipients = new[] { new { phone = telefonoDestino, message = texto } } };
            var resp = await http.PostAsJsonAsync($"{playwrightUrl}/whatsapp/send-bulk", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("[WA pedidos detalle venta] Playwright {Code}: {Err}", (int)resp.StatusCode, err.Substring(0, Math.Min(200, err.Length)));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[WA pedidos detalle venta] error enviando a {Tel}", telefonoDestino);
        }
    }

    /// <summary>Elimina un pedido definitivamente de la DB.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Eliminar(int id)
    {
        var p = await _db.WhatsAppPedidosRecibidos.FindAsync(id);
        if (p is null) return NotFound();
        _db.WhatsAppPedidosRecibidos.Remove(p);
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
        // 2026-07-23: si el pedido ya tiene cliente (por #código o contacto vinculado), ese manda —
        // no pisarlo con lo que adivine la IA.
        if (!p.ClienteId.HasValue)
        {
            p.ClienteNombre = parsed.ClienteNombre;
            p.ClienteId = parsed.ClienteId;
        }
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
        var iaEnabled = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "whatsapp.pedidos.ia_enabled");
        var telefonos = await _db.WhatsAppPedidosTelefonos.AsNoTracking()
            .OrderBy(t => t.Id)
            .Select(t => new TelefonoDto { Id = t.Id, Telefono = t.Telefono, Etiqueta = t.Etiqueta, Activo = t.Activo, LastMessageId = t.LastMessageId, LastReadAt = t.LastReadAt })
            .ToListAsync();
        return Ok(new
        {
            trigger = trigger?.Value ?? "#PED",
            pollEnabled = string.Equals(pollEnabled?.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            autoResponderEnabled = autoResp is null ? true : string.Equals(autoResp.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            // 2026-07-23: interruptor de la IA lectora (default prendida)
            iaEnabled = iaEnabled is null ? true : string.Equals(iaEnabled.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
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
        public bool? IaEnabled { get; set; }
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
        if (req.IaEnabled.HasValue)
            await UpsertAsync("whatsapp.pedidos.ia_enabled", req.IaEnabled.Value ? "true" : "false");
        await _db.SaveChangesAsync();
        return Ok(new { trigger, pollEnabled = req.PollEnabled, autoResponderEnabled = req.AutoResponderEnabled, iaEnabled = req.IaEnabled });
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
