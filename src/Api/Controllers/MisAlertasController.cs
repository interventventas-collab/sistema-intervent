using System.Security.Claims;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-07-10: Motor de alertas configurables ("Mis Alertas"). Las alertas son COMPARTIDAS
/// por rol (admin / oficina / deposito) — no son por-usuario. Cada alerta guarda en Alcance
/// qué roles la ven; por default "admin,oficina" (deposito afuera por ahora). Así el usuario
/// las ve entre con el login que entre, mientras su rol esté incluido.
/// El robot MisAlertasBackgroundService es quien las dispara.
/// </summary>
[ApiController]
[Route("api/mis-alertas")]
[Authorize]
public class MisAlertasController : ControllerBase
{
    private readonly AppDbContext _db;
    public MisAlertasController(AppDbContext db) { _db = db; }

    private static readonly string[] TiposValidos = { "SHELL_BAJO", "BANCO_BAJO", "CHEQUE_VENCE", "FECHA_MES", "EMAIL_REMITENTE" };
    private static readonly string[] RolesValidos = { "admin", "oficina", "deposito" };

    // 2026-07-11: alertas "del sistema" (eventos automáticos que antes vivían en la pantalla de Telegram).
    // Se siembran solas (Program.cs), no se pueden crear ni borrar, solo prender/apagar y elegir canal.
    // Un robot NO las evalúa: se disparan desde el evento real (venta MeLi / fichada).
    private static readonly string[] TiposSistema = { "VENTA_MELI", "FICHADA", "PUBLI_MELI" };

    private int? GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        return claim is not null && int.TryParse(claim.Value, out var id) ? id : null;
    }

    /// <summary>Bucket de rol del usuario actual (admin | oficina | deposito | otro), calculado
    /// igual que el frontend: admin si su rol es "admin"; si no, según sus permisos.</summary>
    private async Task<string> GetBucketAsync()
    {
        var uid = GetUserId();
        if (uid is null) return "otro";
        var user = await _db.Users.Include(u => u.RoleNav).FirstOrDefaultAsync(u => u.Id == uid.Value);
        if (user is null) return "otro";
        var roleName = user.RoleNav?.Name ?? user.Role;
        if (roleName.Equals("admin", StringComparison.OrdinalIgnoreCase)) return "admin";
        var perms = await _db.RolePermissions
            .Where(rp => rp.RoleId == user.RoleId)
            .Select(rp => rp.MenuKey)
            .ToListAsync();
        if (perms.Contains("oficina")) return "oficina";
        if (perms.Contains("deposito")) return "deposito";
        return "otro";
    }

    public record AlertaDto(int Id, string Tipo, decimal? Umbral, string? TextoParam, string Mensaje,
        bool CanalCampanita, bool CanalWhatsApp, bool CanalCorreo, bool CanalTelegram, bool Activa, List<string> Roles,
        bool EstaDisparada, bool Vista, string? UltimoDetalle, DateTime? DisparadaAt, bool EsSistema);

    public record AlertaUpsertRequest(string Tipo, decimal? Umbral, string? TextoParam, string Mensaje,
        bool CanalCampanita, bool CanalWhatsApp, bool CanalCorreo, bool CanalTelegram, bool Activa, List<string>? Roles);

    private static List<string> ParseRoles(string? alcance)
        => string.IsNullOrWhiteSpace(alcance)
            ? new List<string>()
            : alcance.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static AlertaDto Map(MisAlerta a) => new(
        a.Id, a.Tipo, a.Umbral, a.TextoParam, a.Mensaje,
        a.CanalCampanita, a.CanalWhatsApp, a.CanalCorreo, a.CanalTelegram, a.Activa, ParseRoles(a.Alcance),
        a.EstaDisparada, a.Vista, a.UltimoDetalle, a.DisparadaAt, TiposSistema.Contains(a.Tipo));

    /// <summary>Valida y normaliza los roles del selector. Devuelve el CSV a guardar o null si hay error.</summary>
    private static (string? alcance, string? error) NormalizarRoles(List<string>? roles)
    {
        var limpios = (roles ?? new List<string>())
            .Select(r => r?.Trim().ToLowerInvariant() ?? "")
            .Where(r => RolesValidos.Contains(r))
            .Distinct()
            .ToList();
        if (limpios.Count == 0) return (null, "Elegí al menos quién ve la alerta (Admin, Oficina o Depósito)");
        return (string.Join(",", limpios), null);
    }

    private static string? Validar(AlertaUpsertRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Tipo) || !TiposValidos.Contains(r.Tipo))
            return "Tipo de alerta inválido";
        if (string.IsNullOrWhiteSpace(r.Mensaje))
            return "El mensaje es obligatorio";
        if (r.Tipo == "EMAIL_REMITENTE")
        {
            if (string.IsNullOrWhiteSpace(r.TextoParam))
                return "Escribí el remitente (correo) a vigilar";
            return null;
        }
        if (r.Umbral is null || r.Umbral <= 0)
            return "Falta el valor (monto, días o día del mes)";
        if (r.Tipo == "FECHA_MES" && (r.Umbral < 1 || r.Umbral > 31))
            return "El día del mes tiene que estar entre 1 y 31";
        return null;
    }

    // ---------- CRUD (compartidas por rol) ----------
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var bucket = await GetBucketAsync();
        var rows = await _db.MisAlertas
            .Where(a => a.Alcance.Contains(bucket))
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();
        // Las alertas del sistema (Ventas MeLi / Fichadas) van SIEMPRE arriba; el resto por fecha.
        var ordenadas = rows.OrderBy(a => TiposSistema.Contains(a.Tipo) ? 0 : 1).ToList();
        return Ok(ordenadas.Select(Map));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AlertaUpsertRequest r)
    {
        var uid = GetUserId();
        if (uid is null) return Unauthorized();
        if (TiposSistema.Contains(r.Tipo))
            return BadRequest(new { error = "Esa alerta es del sistema y ya existe (Ventas o Fichadas). Solo se prende/apaga." });
        var err = Validar(r);
        if (err is not null) return BadRequest(new { error = err });
        var (alcance, errRoles) = NormalizarRoles(r.Roles);
        if (errRoles is not null) return BadRequest(new { error = errRoles });

        var a = new MisAlerta
        {
            UserId = uid.Value,
            Tipo = r.Tipo,
            Umbral = r.Umbral,
            TextoParam = string.IsNullOrWhiteSpace(r.TextoParam) ? null : r.TextoParam.Trim(),
            Mensaje = r.Mensaje.Trim(),
            CanalCampanita = r.CanalCampanita,
            CanalWhatsApp = r.CanalWhatsApp,
            CanalCorreo = r.CanalCorreo,
            CanalTelegram = r.CanalTelegram,
            Activa = r.Activa,
            Alcance = alcance!
        };
        _db.MisAlertas.Add(a);
        await _db.SaveChangesAsync();
        return Ok(Map(a));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] AlertaUpsertRequest r)
    {
        var bucket = await GetBucketAsync();
        var a = await _db.MisAlertas.FirstOrDefaultAsync(x => x.Id == id && x.Alcance.Contains(bucket));
        if (a is null) return NotFound();
        var err = Validar(r);
        if (err is not null) return BadRequest(new { error = err });
        var (alcance, errRoles) = NormalizarRoles(r.Roles);
        if (errRoles is not null) return BadRequest(new { error = errRoles });

        // Si cambia la definicion, reseteamos el estado de disparo para que vuelva a evaluarse limpio.
        var textoNuevo = string.IsNullOrWhiteSpace(r.TextoParam) ? null : r.TextoParam.Trim();
        var redefinio = a.Tipo != r.Tipo || a.Umbral != r.Umbral || a.TextoParam != textoNuevo;
        a.Tipo = r.Tipo;
        a.Umbral = r.Umbral;
        a.TextoParam = textoNuevo;
        a.Mensaje = r.Mensaje.Trim();
        a.CanalCampanita = r.CanalCampanita;
        a.CanalWhatsApp = r.CanalWhatsApp;
        a.CanalCorreo = r.CanalCorreo;
        a.CanalTelegram = r.CanalTelegram;
        a.Activa = r.Activa;
        a.Alcance = alcance!;
        if (redefinio) { a.EstaDisparada = false; a.Vista = false; a.UltimoDetalle = null; }
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(a));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var bucket = await GetBucketAsync();
        var a = await _db.MisAlertas.FirstOrDefaultAsync(x => x.Id == id && x.Alcance.Contains(bucket));
        if (a is null) return NotFound();
        if (TiposSistema.Contains(a.Tipo))
            return BadRequest(new { error = "Esta alerta es del sistema, no se puede borrar (solo prender/apagar)." });
        _db.MisAlertas.Remove(a);
        await _db.SaveChangesAsync();
        return Ok();
    }

    /// <summary>Prender/apagar rapido desde la lista (interruptor).</summary>
    [HttpPost("{id:int}/toggle")]
    public async Task<IActionResult> Toggle(int id)
    {
        var bucket = await GetBucketAsync();
        var a = await _db.MisAlertas.FirstOrDefaultAsync(x => x.Id == id && x.Alcance.Contains(bucket));
        if (a is null) return NotFound();
        a.Activa = !a.Activa;
        if (!a.Activa) { a.EstaDisparada = false; a.Vista = false; a.UltimoDetalle = null; }
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(a));
    }

    // ---------- Alertas del sistema (Ventas MeLi / Fichadas): prender/apagar + elegir canal ----------
    public record SistemaAlertaRequest(bool Activa, bool CanalCampanita, bool CanalTelegram, bool? CanalWhatsApp = null, bool? CanalCorreo = null);

    /// <summary>Prende/apaga una alerta del sistema y elige por dónde avisa (campanita y/o Telegram).
    /// Son compartidas (no por rol): cualquiera con acceso a Mis Alertas las configura.</summary>
    [HttpPut("sistema/{tipo}")]
    public async Task<IActionResult> UpdateSistema(string tipo, [FromBody] SistemaAlertaRequest r)
    {
        var t = (tipo ?? "").ToUpperInvariant();
        if (!TiposSistema.Contains(t)) return NotFound();
        var a = await _db.MisAlertas.FirstOrDefaultAsync(x => x.Tipo == t);
        if (a is null) return NotFound();
        a.Activa = r.Activa;
        a.CanalCampanita = r.CanalCampanita;
        a.CanalTelegram = r.CanalTelegram;
        // 2026-07-23: canales nuevos (van a las personas de la libretita de Automatizaciones)
        if (r.CanalWhatsApp.HasValue) a.CanalWhatsApp = r.CanalWhatsApp.Value;
        if (r.CanalCorreo.HasValue) a.CanalCorreo = r.CanalCorreo.Value;
        // Si se apaga o se saca la campanita, limpiamos el estado de "disparada" para que no quede colgado.
        if (!a.Activa || !a.CanalCampanita) { a.EstaDisparada = false; a.Vista = false; a.UltimoDetalle = null; }
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(a));
    }

    // ---------- Campanita de la topbar ----------
    public record AlertaDisparadaDto(int Id, string Tipo, string Mensaje, string? Detalle, DateTime? DisparadaAt, bool Vista);
    public record AlertasBellDto(int NoVistas, List<AlertaDisparadaDto> Disparadas);

    /// <summary>Alertas disparadas ahora que el rol del usuario puede ver (para la campanita).</summary>
    [HttpGet("disparadas")]
    public async Task<IActionResult> Disparadas()
    {
        var bucket = await GetBucketAsync();
        var rows = await _db.MisAlertas
            .Where(a => a.Activa && a.EstaDisparada && a.Alcance.Contains(bucket))
            .OrderByDescending(a => a.DisparadaAt)
            .ToListAsync();

        // 2026-07-23 (pedido Osmar): en la campanita cada MAIL importante es SU PROPIO renglón
        // (antes salía uno solo agrupado "N correos nuevos") y todo se ordena por hora, lo más
        // nuevo arriba.
        var lista = new List<AlertaDisparadaDto>();
        foreach (var a in rows)
        {
            if (a.Tipo == "EMAIL_REMITENTE")
            {
                var correos = await _db.MisAlertasCorreos.AsNoTracking()
                    .Where(c => c.AlertaId == a.Id)
                    .OrderByDescending(c => c.FechaRecibido)
                    .Take(10)
                    .ToListAsync();
                if (correos.Count == 0)
                {
                    lista.Add(new AlertaDisparadaDto(a.Id, a.Tipo, a.Mensaje, a.UltimoDetalle, a.DisparadaAt, a.Vista));
                    continue;
                }
                foreach (var c in correos)
                {
                    var fecha = c.FechaRecibido ?? c.DetectadoAt;
                    lista.Add(new AlertaDisparadaDto(a.Id, a.Tipo,
                        $"Mail de {c.Remitente ?? c.RemitenteEmail ?? "remitente importante"}",
                        $"\"{c.Asunto ?? "(sin asunto)"}\" — {fecha.AddHours(-3):HH:mm}",
                        fecha, a.Vista));
                }
            }
            else
            {
                lista.Add(new AlertaDisparadaDto(a.Id, a.Tipo, a.Mensaje, a.UltimoDetalle, a.DisparadaAt, a.Vista));
            }
        }
        lista = lista.OrderByDescending(x => x.DisparadaAt ?? DateTime.MinValue).ToList();
        return Ok(new AlertasBellDto(lista.Count(a => !a.Vista), lista));
    }

    /// <summary>Marca como vistas las alertas disparadas que el rol del usuario ve.</summary>
    [HttpPost("marcar-vistas")]
    public async Task<IActionResult> MarcarVistas()
    {
        var bucket = await GetBucketAsync();
        var rows = await _db.MisAlertas
            .Where(a => a.EstaDisparada && !a.Vista && a.Alcance.Contains(bucket))
            .ToListAsync();
        foreach (var a in rows) a.Vista = true;
        if (rows.Count > 0) await _db.SaveChangesAsync();
        return Ok(new { marcadas = rows.Count });
    }

    // ---------- Card "Correos importantes" del Dashboard ----------
    public record CorreoImportanteDto(int Id, string? Remitente, string? RemitenteEmail, string? Asunto,
        string? Adelanto, DateTime? Fecha, bool TieneAdjuntos, string? Adjuntos, string? GmailLink);

    [HttpGet("correos")]
    public async Task<IActionResult> CorreosImportantes()
    {
        var bucket = await GetBucketAsync();
        var rows = await _db.MisAlertasCorreos
            .Where(c => c.Alerta != null && c.Alerta.Activa && c.Alerta.Alcance.Contains(bucket))
            .OrderByDescending(c => c.FechaRecibido)
            .Take(20)
            .Select(c => new CorreoImportanteDto(c.Id, c.Remitente, c.RemitenteEmail, c.Asunto,
                c.Adelanto, c.FechaRecibido, c.TieneAdjuntos, c.Adjuntos, c.GmailLink))
            .ToListAsync();
        return Ok(rows);
    }

    // ---------- Historial de avisos (bitácora: cada notificación de a una) ----------
    public record HistorialDto(int Id, string Tipo, string Mensaje, string? Detalle,
        string? RemitenteEmail, string? GmailLink, bool PorTelegram, bool EnviadoTelegram, DateTime CreatedAt);

    /// <summary>Últimos avisos emitidos que el rol del usuario puede ver, más nuevos primero.
    /// Filtro opcional por tipo (EMAIL_REMITENTE, VENTA_MELI, FICHADA, etc.); "TODOS" o vacío = todos.</summary>
    [HttpGet("historial")]
    public async Task<IActionResult> Historial([FromQuery] string? tipo = null)
    {
        var bucket = await GetBucketAsync();
        var q = _db.MisAlertasHistorial.Where(h => h.Alcance.Contains(bucket));
        if (!string.IsNullOrWhiteSpace(tipo) && tipo != "TODOS")
            q = q.Where(h => h.Tipo == tipo);
        var rows = await q
            .OrderByDescending(h => h.CreatedAt)
            .Take(100)
            .Select(h => new HistorialDto(h.Id, h.Tipo, h.Mensaje, h.Detalle,
                h.RemitenteEmail, h.GmailLink, h.PorTelegram, h.EnviadoTelegram, h.CreatedAt))
            .ToListAsync();
        return Ok(rows);
    }

    // ---------- Config de la casilla de correo (para alertas EMAIL_REMITENTE) ----------
    // Se guarda en AppSettings (alertas.imap.*). La CLAVE nunca se devuelve al frontend.
    public record ConfigCorreoDto(string? Host, int Port, string? Usuario, bool TieneClave, bool Configurada);
    public record ConfigCorreoRequest(string? Host, int? Port, string? Usuario, string? Password);

    [HttpGet("config-correo")]
    public async Task<IActionResult> GetConfigCorreo()
    {
        var cfg = await _db.AppSettings.Where(s => s.Key.StartsWith("alertas.imap.")).ToListAsync();
        string? Get(string k) => cfg.FirstOrDefault(s => s.Key == k)?.Value;
        var host = Get("alertas.imap.host");
        var user = Get("alertas.imap.user");
        var pass = Get("alertas.imap.pass");
        if (!int.TryParse(Get("alertas.imap.port"), out var port) || port <= 0) port = 993;
        // 1) Casilla propia cargada por el usuario.
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
            return Ok(new ConfigCorreoDto(host, port, user, true, true));

        // 2) Reutilizo la casilla ya conectada del sistema (integración email-smtp).
        var integ = await _db.Integrations.FirstOrDefaultAsync(x => x.Provider == "email-smtp");
        if (integ is not null && !string.IsNullOrWhiteSpace(integ.AppSecret) && !string.IsNullOrWhiteSpace(integ.Settings))
        {
            try
            {
                using var doc = JsonDocument.Parse(integ.Settings);
                var root = doc.RootElement;
                string? u = null;
                if (root.TryGetProperty("username", out var un) && !string.IsNullOrWhiteSpace(un.GetString())) u = un.GetString();
                else if (root.TryGetProperty("fromAddress", out var fa)) u = fa.GetString();
                var ih = root.TryGetProperty("imapHost", out var h2) ? h2.GetString() : "imap.gmail.com";
                var ip = root.TryGetProperty("imapPort", out var p2) && p2.TryGetInt32(out var ipv) ? ipv : 993;
                if (!string.IsNullOrWhiteSpace(u))
                    return Ok(new ConfigCorreoDto(ih, ip, u, true, true));
            }
            catch { }
        }
        return Ok(new ConfigCorreoDto(host, port, user, false, false));
    }

    [HttpPost("config-correo")]
    public async Task<IActionResult> SaveConfigCorreo([FromBody] ConfigCorreoRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Usuario)) return BadRequest(new { error = "Falta el correo (usuario)" });

        async Task Set(string k, string v)
        {
            var s = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == k);
            if (s is null) { s = new AppSetting { Key = k }; _db.AppSettings.Add(s); }
            s.Value = v;
            s.UpdatedAt = DateTime.UtcNow;
        }

        await Set("alertas.imap.host", string.IsNullOrWhiteSpace(r.Host) ? "imap.gmail.com" : r.Host.Trim());
        await Set("alertas.imap.port", (r.Port is > 0 ? r.Port.Value : 993).ToString());
        await Set("alertas.imap.user", r.Usuario.Trim());
        // La clave solo se pisa si mandaron una nueva (asi no hay que re-tipearla cada vez).
        if (!string.IsNullOrWhiteSpace(r.Password))
            await Set("alertas.imap.pass", r.Password);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
