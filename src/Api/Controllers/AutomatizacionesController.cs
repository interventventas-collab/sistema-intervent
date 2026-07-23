using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>2026-07-23 (pedido Osmar): Centro de Automatizaciones — una API para gestionar TODOS
/// los robots del sistema desde una pantalla: avisos programados (interruptor, días, hora, canales,
/// destinatarios, probar) + respondedores (interruptor espejo de sus settings de siempre).</summary>
[ApiController]
[Route("api/automatizaciones")]
[Authorize]
public class AutomatizacionesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IServiceProvider _sp;

    public AutomatizacionesController(AppDbContext db, IServiceProvider sp)
    {
        _db = db;
        _sp = sp;
    }

    // Los respondedores existentes y su llave real en AppSettings (espejo: tocar acá o en su
    // pantalla de siempre es lo mismo). defaultOn = qué significa "sin fila" en AppSettings.
    private static readonly (string Key, string Nombre, string Descripcion, string SettingKey, bool DefaultOn, string? LinkConfig)[] Respondedores =
    {
        ("bot-bienvenida", "Bot de bienvenida WhatsApp", "Saluda a números desconocidos con los botones de empresa (Frikaf / Intervent / Intereventos) y el menú de opciones.", "whatsapp.bot.bienvenida_enabled", true, null),
        ("ia-lectora", "IA lectora de pedidos", "Lee cada pedido de WhatsApp apenas entra e identifica productos y cliente (consume unos centavos por pedido).", "whatsapp.pedidos.ia_enabled", true, "/cafe/pedidos-whatsapp"),
        ("pedido-recibido", "Respuesta \"PEDIDO RECIBIDO\"", "Contesta automáticamente cuando entra un pedido con ## o #código.", "whatsapp.pedidos.auto_responder_enabled", true, "/cafe/pedidos-whatsapp"),
        ("respondedor-meli", "Respondedor nocturno MercadoLibre", "Responde solo las preguntas de MeLi en el horario configurado (mensajes que rotan, firma Leandro).", "meli.autoreply.enabled", false, "/meli/respuestas-automaticas")
    };

    public record PersonaDto(int Id, string Nombre, long? TelegramChatId, string? WhatsAppNumero, string? Email, bool Activo);
    public record AvisoDto(string Key, string Nombre, string Descripcion, bool Enabled, string Dias, int Hora,
        bool CanalCampanita, bool CanalTelegram, bool CanalWhatsApp, bool CanalEmail,
        List<int> Destinatarios, DateTime? LastRunAt, bool? LastRunOk, string? LastRunDetalle);
    public record RespondedorDto(string Key, string Nombre, string Descripcion, bool Enabled, string? LinkConfig);

    private static readonly (string Key, string Nombre, string Descripcion)[] Avisos =
    {
        ("resumen-financiero", "🌅 Resumen financiero matutino", "Saldo Galicia + Shell Flota + cheques por cubrir, con total y vencimientos marcados."),
        ("deudas-diario", "📋 Deudas por cliente", "Cuánto te debe cada cliente (cuenta corriente) con el total general.")
    };

    [HttpGet]
    public async Task<IActionResult> Listar()
    {
        var personas = await _db.AutoPersonas.AsNoTracking().OrderBy(p => p.Id)
            .Select(p => new PersonaDto(p.Id, p.Nombre, p.TelegramChatId, p.WhatsAppNumero, p.Email, p.Activo))
            .ToListAsync();

        var configs = await _db.AutoConfigs.AsNoTracking().ToListAsync();
        var destinos = await _db.AutoDestinatarios.AsNoTracking().ToListAsync();
        var avisos = Avisos.Select(a =>
        {
            var c = configs.FirstOrDefault(x => x.AutoKey == a.Key) ?? new AutoConfig { AutoKey = a.Key };
            return new AvisoDto(a.Key, a.Nombre, a.Descripcion, c.Enabled, c.Dias, c.Hora,
                c.CanalCampanita, c.CanalTelegram, c.CanalWhatsApp, c.CanalEmail,
                destinos.Where(d => d.AutoKey == a.Key).Select(d => d.PersonaId).ToList(),
                c.LastRunAt, c.LastRunOk, c.LastRunDetalle);
        }).ToList();

        var settings = await _db.AppSettings.AsNoTracking()
            .Where(s => Respondedores.Select(r => r.SettingKey).Contains(s.Key)).ToListAsync();
        var respondedores = Respondedores.Select(r =>
        {
            var s = settings.FirstOrDefault(x => x.Key == r.SettingKey);
            var enabled = s is null ? r.DefaultOn : string.Equals(s.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            return new RespondedorDto(r.Key, r.Nombre, r.Descripcion, enabled, r.LinkConfig);
        }).ToList();

        return Ok(new { personas, avisos, respondedores });
    }

    public record PersonaUpsert(string Nombre, long? TelegramChatId, string? WhatsAppNumero, string? Email, bool Activo);

    [HttpPost("personas")]
    public async Task<IActionResult> CrearPersona([FromBody] PersonaUpsert req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Falta el nombre" });
        var p = new AutoPersona { Nombre = req.Nombre.Trim(), TelegramChatId = req.TelegramChatId, WhatsAppNumero = Norm(req.WhatsAppNumero), Email = req.Email?.Trim(), Activo = req.Activo };
        _db.AutoPersonas.Add(p);
        await _db.SaveChangesAsync();
        return Ok(new { p.Id });
    }

    [HttpPost("personas/{id:int}")]
    public async Task<IActionResult> EditarPersona(int id, [FromBody] PersonaUpsert req)
    {
        var p = await _db.AutoPersonas.FindAsync(id);
        if (p is null) return NotFound();
        p.Nombre = string.IsNullOrWhiteSpace(req.Nombre) ? p.Nombre : req.Nombre.Trim();
        p.TelegramChatId = req.TelegramChatId;
        p.WhatsAppNumero = Norm(req.WhatsAppNumero);
        p.Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim();
        p.Activo = req.Activo;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    public record AvisoConfigReq(bool Enabled, string Dias, int Hora,
        bool CanalCampanita, bool CanalTelegram, bool CanalWhatsApp, bool CanalEmail, List<int> Destinatarios);

    [HttpPost("avisos/{key}")]
    public async Task<IActionResult> GuardarAviso(string key, [FromBody] AvisoConfigReq req)
    {
        if (!Avisos.Any(a => a.Key == key)) return NotFound(new { error = "Automatización desconocida" });
        var c = await _db.AutoConfigs.FindAsync(key);
        if (c is null) { c = new AutoConfig { AutoKey = key }; _db.AutoConfigs.Add(c); }
        c.Enabled = req.Enabled;
        c.Dias = string.IsNullOrWhiteSpace(req.Dias) ? "1,2,3,4,5,6,7" : req.Dias;
        c.Hora = Math.Clamp(req.Hora, 0, 23);
        c.CanalCampanita = req.CanalCampanita;
        c.CanalTelegram = req.CanalTelegram;
        c.CanalWhatsApp = req.CanalWhatsApp;
        c.CanalEmail = req.CanalEmail;
        c.UpdatedAt = DateTime.UtcNow;

        var viejos = _db.AutoDestinatarios.Where(d => d.AutoKey == key);
        _db.AutoDestinatarios.RemoveRange(viejos);
        foreach (var pid in (req.Destinatarios ?? new()).Distinct())
            _db.AutoDestinatarios.Add(new AutoDestinatario { AutoKey = key, PersonaId = pid });

        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Dispara el aviso YA (ignora interruptor, días y hora — es una prueba manual).</summary>
    [HttpPost("avisos/{key}/probar")]
    public async Task<IActionResult> Probar(string key)
    {
        switch (key)
        {
            case "resumen-financiero":
            {
                var n = _sp.GetRequiredService<ResumenFinancieroNotifier>();
                var (ok, detalle) = await n.EnviarResumenAsync(HttpContext.RequestAborted);
                return Ok(new { ok, detalle });
            }
            case "deudas-diario":
            {
                var n = _sp.GetRequiredService<DeudoresDiarioNotifier>();
                var r = await n.EnviarResumenAsync(HttpContext.RequestAborted);
                return Ok(new { ok = r.Ok, detalle = r.Detalle });
            }
            default:
                return NotFound(new { error = "Automatización desconocida" });
        }
    }

    public record ToggleReq(bool Enabled);

    /// <summary>Prende/apaga un respondedor — es un ESPEJO de su setting de siempre.</summary>
    [HttpPost("respondedores/{key}/toggle")]
    public async Task<IActionResult> ToggleRespondedor(string key, [FromBody] ToggleReq req)
    {
        var r = Respondedores.FirstOrDefault(x => x.Key == key);
        if (r.SettingKey is null) return NotFound(new { error = "Respondedor desconocido" });
        var s = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == r.SettingKey);
        if (s is null) _db.AppSettings.Add(new AppSetting { Key = r.SettingKey, Value = req.Enabled ? "true" : "false", UpdatedAt = DateTime.UtcNow });
        else { s.Value = req.Enabled ? "true" : "false"; s.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    private static string? Norm(string? numero)
    {
        if (string.IsNullOrWhiteSpace(numero)) return null;
        var n = numero.Trim();
        return n.StartsWith("whatsapp:") ? n : "whatsapp:" + n;
    }
}
