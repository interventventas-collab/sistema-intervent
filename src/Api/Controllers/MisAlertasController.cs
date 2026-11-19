using System.Security.Claims;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

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

    private static readonly string[] TiposValidos = { "SHELL_BAJO", "BANCO_BAJO", "CHEQUE_VENCE", "FECHA_MES", "EMAIL_REMITENTE", "APARATO_NUEVO" };
    private static readonly string[] RolesValidos = { "admin", "oficina", "deposito" };

    // 2026-07-11: alertas "del sistema" (eventos automáticos que antes vivían en la pantalla de Telegram).
    // Se siembran solas (Program.cs), no se pueden crear ni borrar, solo prender/apagar y elegir canal.
    // Un robot NO las evalúa: se disparan desde el evento real (venta MeLi / fichada).
    private static readonly string[] TiposSistema = { "VENTA_MELI", "FICHADA", "PUBLI_MELI", "ENVIO_RECHAZADO", "UBICACION_ERRONEA", "DRIVE_CAIDO", "WA_ESPERANDO" };

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
        bool EstaDisparada, bool Vista, string? UltimoDetalle, DateTime? DisparadaAt, bool EsSistema,
        List<int> Destinatarios, string? LineaPhoneId);

    public record AlertaUpsertRequest(string Tipo, decimal? Umbral, string? TextoParam, string Mensaje,
        bool CanalCampanita, bool CanalWhatsApp, bool CanalCorreo, bool CanalTelegram, bool Activa, List<string>? Roles,
        List<int>? Destinatarios = null, string? LineaPhoneId = null);

    private static List<string> ParseRoles(string? alcance)
        => string.IsNullOrWhiteSpace(alcance)
            ? new List<string>()
            : alcance.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static AlertaDto Map(MisAlerta a, List<int>? destinatarios = null) => new(
        a.Id, a.Tipo, a.Umbral, a.TextoParam, a.Mensaje,
        a.CanalCampanita, a.CanalWhatsApp, a.CanalCorreo, a.CanalTelegram, a.Activa, ParseRoles(a.Alcance),
        a.EstaDisparada, a.Vista, a.UltimoDetalle, a.DisparadaAt, TiposSistema.Contains(a.Tipo),
        destinatarios ?? new List<int>(), a.LineaPhoneId);

    /// <summary>Destinatarios (personas de la libretita) de una alerta. Clave "alerta:{id}" en Auto_Destinatarios.</summary>
    private async Task<List<int>> DestinatariosDeAsync(int alertaId) =>
        await _db.AutoDestinatarios.Where(d => d.AutoKey == $"alerta:{alertaId}")
            .Select(d => d.PersonaId).ToListAsync();

    private async Task GuardarDestinatariosAsync(int alertaId, List<int>? personas)
    {
        if (personas is null) return; // null = no tocar (compat con llamadas viejas)
        var key = $"alerta:{alertaId}";
        _db.AutoDestinatarios.RemoveRange(_db.AutoDestinatarios.Where(d => d.AutoKey == key));
        foreach (var pid in personas.Distinct())
            _db.AutoDestinatarios.Add(new AutoDestinatario { AutoKey = key, PersonaId = pid });
        await _db.SaveChangesAsync();
    }

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
        var dest = await _db.AutoDestinatarios.Where(d => d.AutoKey.StartsWith("alerta:")).ToListAsync();
        return Ok(ordenadas.Select(a => Map(a,
            dest.Where(d => d.AutoKey == $"alerta:{a.Id}").Select(d => d.PersonaId).ToList())));
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
            Alcance = alcance!,
            LineaPhoneId = string.IsNullOrWhiteSpace(r.LineaPhoneId) ? null : r.LineaPhoneId.Trim()
        };
        _db.MisAlertas.Add(a);
        await _db.SaveChangesAsync();
        await GuardarDestinatariosAsync(a.Id, r.Destinatarios);
        return Ok(Map(a, await DestinatariosDeAsync(a.Id)));
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
        a.LineaPhoneId = string.IsNullOrWhiteSpace(r.LineaPhoneId) ? null : r.LineaPhoneId.Trim();
        if (redefinio) { a.EstaDisparada = false; a.Vista = false; a.UltimoDetalle = null; }
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await GuardarDestinatariosAsync(a.Id, r.Destinatarios);
        return Ok(Map(a, await DestinatariosDeAsync(a.Id)));
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
        // 2026-09-09: se limpia tambien DisparadaAt. Las alertas por evento la usan como marca de
        // "ya avise" para no repetir el aviso; si no se borra, apagar y prender no sirve para
        // destrabarlas y el proximo aviso queda mudo.
        if (!a.Activa) { a.EstaDisparada = false; a.Vista = false; a.UltimoDetalle = null; a.DisparadaAt = null; }
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(a));
    }

    // ---------- Alertas del sistema (Ventas MeLi / Fichadas): prender/apagar + elegir canal ----------
    public record SistemaAlertaRequest(bool Activa, bool CanalCampanita, bool CanalTelegram, bool? CanalWhatsApp = null, bool? CanalCorreo = null, List<int>? Destinatarios = null, string? LineaPhoneId = null, bool LineaSet = false);

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
        // 2026-08-03: la línea solo se toca cuando el que llama lo pide (LineaSet), así prender/apagar
        // o tildar un canal NO borra la línea elegida.
        if (r.LineaSet) a.LineaPhoneId = string.IsNullOrWhiteSpace(r.LineaPhoneId) ? null : r.LineaPhoneId.Trim();
        // Si se apaga o se saca la campanita, limpiamos el estado de "disparada" para que no quede colgado.
        // 2026-09-09: DisparadaAt tambien (ver Toggle): es la marca de "ya avise" de las alertas por evento.
        if (!a.Activa || !a.CanalCampanita) { a.EstaDisparada = false; a.Vista = false; a.UltimoDetalle = null; a.DisparadaAt = null; }
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await GuardarDestinatariosAsync(a.Id, r.Destinatarios);
        return Ok(Map(a, await DestinatariosDeAsync(a.Id)));
    }

    /// <summary>2026-07-23 (pedido Osmar): dispara una PRUEBA de la alerta YA, por los canales y
    /// destinatarios que tiene configurados — para verificar que llega donde tiene que llegar.</summary>
    [HttpPost("{id:int}/probar")]
    public async Task<IActionResult> Probar(int id,
        [FromServices] TelegramService tg,
        [FromServices] WhatsAppOutboundService wa,
        [FromServices] AutoAvisoSender sender)
    {
        var a = await _db.MisAlertas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (a is null) return NotFound();

        var titulo = string.IsNullOrWhiteSpace(a.Mensaje) ? a.Tipo : a.Mensaje;
        var texto = $"🧪 PRUEBA de alerta: {titulo}\n(así se va a ver cuando se dispare de verdad)";
        var partes = new List<string>();
        var okAlguno = false;

        var idsDest = await _db.AutoDestinatarios.Where(d => d.AutoKey == $"alerta:{id}")
            .Select(d => d.PersonaId).ToListAsync();
        var pers = await _db.AutoPersonas.AsNoTracking()
            .Where(p => p.Activo && idsDest.Contains(p.Id)).ToListAsync();

        if (a.CanalCampanita)
        {
            _db.MisAlertasHistorial.Add(new MisAlertaHistorial
            {
                Tipo = a.Tipo, Mensaje = $"🧪 Prueba: {titulo}",
                Detalle = "Disparada a mano desde Automatizaciones y Alertas",
                Alcance = a.Alcance, PorTelegram = false, CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
            okAlguno = true; partes.Add("🔔 campanita OK");
        }

        if (a.CanalTelegram)
        {
            int ok = 0, tot = 0;
            foreach (var p in pers.Where(x => x.TelegramChatId is > 0))
            {
                tot++;
                var (enviado, _) = await tg.SendMessageAsync(texto, chatId: p.TelegramChatId);
                if (enviado) ok++;
            }
            if (tot == 0) { var (enviado, _) = await tg.SendMessageAsync(texto, categoria: "ALERTAS"); if (enviado) { ok = 1; tot = 1; } }
            okAlguno |= ok > 0; partes.Add($"📲 Telegram {ok}/{tot}");
        }

        if (a.CanalWhatsApp)
        {
            int ok = 0, tot = 0;
            foreach (var p in pers.Where(x => !string.IsNullOrWhiteSpace(x.WhatsAppNumero)))
            {
                tot++;
                var numero = p.WhatsAppNumero!.StartsWith("whatsapp:") ? p.WhatsAppNumero : "whatsapp:" + p.WhatsAppNumero;
                var (sid, canal, lin) = await wa.SendTextAsync(numero, texto, lineaOverride: a.LineaPhoneId);
                if (sid != null)
                {
                    ok++;
                    _db.WhatsAppTwilioMensajes.Add(new WhatsAppTwilioMensaje
                    {
                        Direccion = "OUTGOING", Numero = numero, Cuerpo = texto,
                        TwilioMessageSid = sid, Canal = canal, LineaPhoneId = lin, Procesado = true, CreatedAt = DateTime.UtcNow
                    });
                    await _db.SaveChangesAsync();
                }
            }
            okAlguno |= ok > 0; partes.Add($"📱 WhatsApp {ok}/{tot}");
        }

        if (a.CanalCorreo)
        {
            int ok = 0, tot = 0;
            foreach (var p in pers.Where(x => !string.IsNullOrWhiteSpace(x.Email)))
            {
                tot++;
                if (await sender.EnviarEmailAsync(p.Email!, $"🧪 Prueba: {titulo}", texto, HttpContext.RequestAborted)) ok++;
            }
            okAlguno |= ok > 0; partes.Add($"📧 correo {ok}/{tot}");
        }

        var detalle = partes.Count > 0 ? string.Join(" · ", partes) : "la alerta no tiene ningún canal tildado";
        return Ok(new { ok = okAlguno, detalle });
    }

    // ---------- Campanita de la topbar ----------
    /// <summary>18/11/2026: la campanita ya mostró los avisos de facturas nuevas / VEP (deja de titilar).</summary>
    private const string KeyCampanaVista = "campana.compras_impuestos.vista";
    public record AlertaDisparadaDto(int Id, string Tipo, string Mensaje, string? Detalle, DateTime? DisparadaAt, bool Vista);
    /// <summary>19/11/2026: Numeritos = cuántos avisos hay por pantalla (clave = ruta del botón), para el numerito rojo
    /// de cada botón de las tarjetas. El mismo aviso está en la campanita.</summary>
    public record AlertasBellDto(int NoVistas, List<AlertaDisparadaDto> Disparadas, Dictionary<string, int>? Numeritos = null);

    // ── 2026-11-18: interruptor general "📱 Avisos automáticos por WhatsApp" ──
    // Apagado = ningún robot manda WhatsApp por su cuenta; campanita, Telegram y mail siguen igual.
    // Lo lee cualquiera con acceso a Mis alertas; lo cambia solo el admin.
    public record WaAvisosAutomaticosDto(bool Prendidos);

    [HttpGet("wa-avisos-automaticos")]
    public async Task<IActionResult> GetWaAvisosAutomaticos()
        => Ok(new WaAvisosAutomaticosDto(await WaAvisosAutomaticos.EstanPrendidosAsync(_db)));

    [HttpPut("wa-avisos-automaticos")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> SetWaAvisosAutomaticos([FromBody] WaAvisosAutomaticosDto r)
    {
        await WaAvisosAutomaticos.SetAsync(_db, r.Prendidos);
        return Ok(new WaAvisosAutomaticosDto(r.Prendidos));
    }

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
        var numeritos = new Dictionary<string, int>();
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
        // 17/11/2026: pagos a proveedores programados para hoy/mañana o atrasados (solo admin).
        // Id negativo = -Id del pago programado (tocarlo abre la ventana "Transferir a ...").
        if (bucket == "admin")
        {
            var hoy = DateTime.UtcNow.AddHours(-3).Date;
            var manana = hoy.AddDays(1);
            var progs = await _db.CafePagosProgramados.AsNoTracking().Include(p => p.Proveedor)
                .Where(p => p.Estado == "PENDIENTE" && p.TransferidoAt == null && p.Fecha <= manana)
                .OrderBy(p => p.Fecha).ToListAsync();
            var ar = System.Globalization.CultureInfo.GetCultureInfo("es-AR");
            foreach (var p in progs)
            {
                var cuando = p.Fecha < hoy ? $"ATRASADO desde el {p.Fecha:dd/MM}" : p.Fecha == hoy ? "Hoy" : "Mañana";
                var vista = p.AvisoVistoAt.HasValue && p.AvisoVistoAt.Value.AddHours(-3).Date >= hoy;
                lista.Insert(0, new AlertaDisparadaDto(-p.Id, p.Fecha < hoy ? "PAGO_PROG_ATRASADO" : "PAGO_PROGRAMADO",
                    $"{cuando} pagás a {p.Proveedor?.Nombre ?? "proveedor"} " + p.Importe.ToString("$#,0", ar),
                    p.Medio == "TRANSFERENCIA" ? "Tocá para ver alias, CBU e importe y transferir" : "Tocá para pagarlo",
                    DateTime.UtcNow, vista));
            }
        }
        if (bucket == "admin")
        {
            var ar2 = System.Globalization.CultureInfo.GetCultureInfo("es-AR");
            var hoyAr = DateTime.UtcNow.AddHours(-3).Date;
            var campVista = ComprasAfipRobot.ParseUtc(await ComprasAfipRobot.LeerAsync(_db, KeyCampanaVista));
            // 18/11/2026: facturas recibidas nuevas (las que no viste en "Facturas recibidas"). Id -1000000.
            var vistasHasta = ComprasAfipRobot.ParseUtc(await ComprasAfipRobot.LeerAsync(_db, ComprasAfipRobot.KeyVistas));
            if (vistasHasta.HasValue)
            {
                var nuevas = await _db.ContadoraComprobantes.AsNoTracking()
                    .Where(c => c.Naturaleza == "COMPRA" && !c.EsNotaCredito && c.PrimeraVezEn > vistasHasta.Value)
                    .Select(c => new { c.ReceptorNombre, c.Total, c.PrimeraVezEn }).ToListAsync();
                if (nuevas.Count > 0)
                {
                    numeritos["/compras/facturas-recibidas"] = nuevas.Count;
                    var ult = nuevas.Max(n => n.PrimeraVezEn);
                    var nombres = nuevas.Select(n => FacturasRecibidasController.Lindo(n.ReceptorNombre)).Distinct().ToList();
                    lista.Add(new AlertaDisparadaDto(-1000000, "FACTURAS_NUEVAS",
                        $"{nuevas.Count} factura{(nuevas.Count == 1 ? "" : "s")} nueva{(nuevas.Count == 1 ? "" : "s")} de proveedores " + nuevas.Sum(n => n.Total).ToString("$#,0", ar2),
                        string.Join(" · ", nombres.Take(3)) + (nombres.Count > 3 ? $" y {nombres.Count - 3} más" : ""),
                        ult, campVista.HasValue && campVista.Value >= ult));
                }
            }
            // 18/11/2026: VEP que vencen en 3 días o ya vencieron sin pago. Id -2000000-Id.
            var veps = await _db.ImpuestosVeps.AsNoTracking()
                .Where(v => v.Estado == "PENDIENTE" && v.Vence != null && v.Vence <= hoyAr.AddDays(3) && v.Vence >= hoyAr.AddDays(-30))
                .ToListAsync();
            foreach (var v in veps)
            {
                var d = v.Vence!.Value.Date;
                var cuando = d < hoyAr ? $"VENCIÓ el {d:dd/MM}" : d == hoyAr ? "Vence HOY" : d == hoyAr.AddDays(1) ? "Vence mañana" : $"Vence el {d:dd/MM}";
                lista.Add(new AlertaDisparadaDto(-2000000 - v.Id, d < hoyAr ? "VEP_VENCIDO" : "VEP_VENCE",
                    $"{cuando}: {v.Concepto} {v.Importe.ToString("$#,0", ar2)}", "Impuestos · tocá para verlo",
                    DateTime.UtcNow, campVista.HasValue && campVista.Value.AddHours(-3).Date >= hoyAr));
            }
            // 18/11/2026: almanaque de ARCA — vence en 3 días (o venció hace ≤3) y María no mandó el VEP. Id -3000000-i.
            var vencs = await VencimientosArca.EvaluarAsync(_db, hoyAr.AddDays(-3), hoyAr.AddDays(3));
            for (int i = 0; i < vencs.Count; i++)
            {
                var v = vencs[i];
                if (v.Estado != "SIN_VEP") continue;
                var d = v.Vence.Date;
                var cuando = d < hoyAr ? $"VENCIÓ el {d:dd/MM}" : d == hoyAr ? "Vence HOY" : d == hoyAr.AddDays(1) ? "Vence mañana" : $"Vence el {d:dd/MM}";
                lista.Add(new AlertaDisparadaDto(-3000000 - i, d < hoyAr ? "VEP_VENCIDO" : "VEP_VENCE",
                    $"{cuando}: {v.Nombre}", d < hoyAr ? "No veo el pago · Impuestos" : "María todavía no mandó el VEP · Impuestos",
                    DateTime.UtcNow, campVista.HasValue && campVista.Value.AddHours(-3).Date >= hoyAr));
            }
        }
        if (bucket == "admin")
        {
            // 18/11/2026: "#gasto" por WhatsApp pendientes de cargar. Id -4000000-Id (tocarlo abre Nuevo pago).
            var campVistaG = ComprasAfipRobot.ParseUtc(await ComprasAfipRobot.LeerAsync(_db, KeyCampanaVista));
            var ar3 = System.Globalization.CultureInfo.GetCultureInfo("es-AR");
            var gastos = await _db.GastosWhatsApp.AsNoTracking().Where(g => g.Estado == "PENDIENTE")
                .OrderByDescending(g => g.Id).Take(20).ToListAsync();
            foreach (var g in gastos)
            {
                var txt = g.Texto.Length > 70 ? g.Texto[..70] + "…" : g.Texto;
                lista.Add(new AlertaDisparadaDto(-4000000 - g.Id, "GASTO_WA",
                    $"Gasto: {txt}" + (string.IsNullOrEmpty(g.MediaUrl) ? "" : " 📎"),
                    (g.Importe is decimal imp ? imp.ToString("$#,0", ar3) + " · " : "") + "WhatsApp · tocá para cargarlo",
                    g.CreatedAt, campVistaG.HasValue && campVistaG.Value >= g.CreatedAt));
            }
        }
        if (bucket == "admin")
        {
            // 18/11/2026: cargas de combustible de Shell (últimos 3 días). Id -5000000-Id → /combustible.
            try
            {
                var campVistaS = ComprasAfipRobot.ParseUtc(await ComprasAfipRobot.LeerAsync(_db, KeyCampanaVista));
                var arS = System.Globalization.CultureInfo.GetCultureInfo("es-AR");
                var cargasS = await HttpContext.RequestServices.GetRequiredService<ShellCargasService>().ParaCampanitaAsync();
                foreach (var c in cargasS)
                {
                    var donde = $"{c.Estacion} · {c.FechaHoraAr:dd/MM HH:mm}" + (c.Odometro is int km ? $" · km {km.ToString("#,0", arS)}" : "");
                    lista.Add(new AlertaDisparadaDto(-5000000 - c.Id, c.Alarmas.Count > 0 ? "SHELL_CARGA_RARA" : "SHELL_CARGA",
                        $"{c.Nombre} cargó {c.Litros.ToString("0.#", arS)} L · {c.Importe.ToString("$#,0", arS)}",
                        c.Alarmas.Count > 0 ? "⚠ " + string.Join(" · ", c.Alarmas) + " · " + donde : donde,
                        c.DetectadaAt, campVistaS.HasValue && campVistaS.Value >= c.DetectadaAt));
                }
            }
            catch (Exception ex) { Console.WriteLine("[campanita] cargas Shell: " + ex.Message); }
        }
        if (bucket == "admin")
        {
            // 19/11/2026: pagos del Galicia a proveedores sin registrar (botón 🏦 Registrar el pago). Id -6000000.
            try
            {
                var arT = System.Globalization.CultureInfo.GetCultureInfo("es-AR");
                var campVistaT = ComprasAfipRobot.ParseUtc(await ComprasAfipRobot.LeerAsync(_db, KeyCampanaVista));
                var fr = ActivatorUtilities.CreateInstance<FacturasRecibidasController>(HttpContext.RequestServices);
                fr.ControllerContext = ControllerContext;
                var av = await fr.PagosBancoSinRegistrarAsync();
                if (av.Movimientos > 0)
                {
                    numeritos["/compras/facturas-recibidas"] = numeritos.GetValueOrDefault("/compras/facturas-recibidas") + av.Movimientos;
                    lista.Add(new AlertaDisparadaDto(-6000000, "TRANSF_SIN_REG",
                        $"{av.Movimientos} pago{(av.Movimientos == 1 ? "" : "s")} del Galicia a proveedores sin registrar " + av.Total.ToString("$#,0", arT),
                        string.Join(" · ", av.Proveedores.Take(3)) + (av.Proveedores.Count > 3 ? $" y {av.Proveedores.Count - 3} más" : ""),
                        av.Ultimo, campVistaT.HasValue && av.Ultimo.HasValue && campVistaT.Value >= av.Ultimo.Value));
                }
            }
            catch (Exception ex) { Console.WriteLine("[campanita] pagos banco sin registrar: " + ex.Message); }
            // Extracto del banco: transferencias con el botón verde "💸 Registrar pago".
            try
            {
                var mc = HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
                if (!mc.TryGetValue(FacturasRecibidasController.KeyAvisoCruce, out int nCruce))
                {
                    nCruce = (await HttpContext.RequestServices.GetRequiredService<ContadoraService>().PreviewCruceBancoAsync()).Count;
                    mc.Set(FacturasRecibidasController.KeyAvisoCruce, nCruce, TimeSpan.FromMinutes(3));
                }
                if (nCruce > 0) numeritos["/cafe/tesoreria/extracto-banco"] = nCruce;
            }
            catch (Exception ex) { Console.WriteLine("[campanita] cruce banco: " + ex.Message); }
            var progN = lista.Count(a => a.Tipo.StartsWith("PAGO_PROG"));
            if (progN > 0) numeritos["/cafe/proveedores-ctacte"] = progN;
            var vepN = lista.Count(a => a.Tipo.StartsWith("VEP_"));
            if (vepN > 0) numeritos["/impuestos"] = vepN;
        }
        lista = lista.OrderByDescending(x => x.DisparadaAt ?? DateTime.MinValue).ToList();
        return Ok(new AlertasBellDto(lista.Count(a => !a.Vista), lista, numeritos));
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
        var n = rows.Count;
        if (bucket == "admin")
        {
            // 17/11/2026: los avisos de pagos programados se dan por vistos HOY (mañana vuelven a titilar si siguen).
            var manana = DateTime.UtcNow.AddHours(-3).Date.AddDays(1);
            var progs = await _db.CafePagosProgramados
                .Where(p => p.Estado == "PENDIENTE" && p.TransferidoAt == null && p.Fecha <= manana).ToListAsync();
            foreach (var p in progs) p.AvisoVistoAt = DateTime.UtcNow;
            n += progs.Count;
            await ComprasAfipRobot.GuardarAsync(_db, KeyCampanaVista, DateTime.UtcNow.ToString("o"));
        }
        if (_db.ChangeTracker.HasChanges()) await _db.SaveChangesAsync();
        return Ok(new { marcadas = n });
    }

    // ---------- Card "Correos importantes" del Dashboard ----------
    public record CorreoImportanteDto(int Id, string? Remitente, string? RemitenteEmail, string? Asunto,
        string? Adelanto, DateTime? Fecha, bool TieneAdjuntos, string? Adjuntos, string? GmailLink);

    [HttpGet("correos")]
    public async Task<IActionResult> CorreosImportantes()
    {
        var bucket = await GetBucketAsync();
        var crudos = await _db.MisAlertasCorreos
            .Where(c => c.Alerta != null && c.Alerta.Activa && c.Alerta.Alcance.Contains(bucket))
            .OrderByDescending(c => c.FechaRecibido)
            .Take(20)
            .ToListAsync();
        // 2026-08-26: el adelanto se limpia también acá (no solo al bajar el mail) para que los
        // correos ya guardados dejen de mostrar el CSS interno del mail en la card del Dashboard.
        var rows = crudos.Select(c => new CorreoImportanteDto(c.Id, c.Remitente, c.RemitenteEmail, c.Asunto,
                MisAlertasBackgroundService.LimpiarAdelanto(c.Adelanto, 200), c.FechaRecibido,
                c.TieneAdjuntos, c.Adjuntos, c.GmailLink))
            .ToList();
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
