using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-08-13: "Avisos de cierre de ventana de WhatsApp". El usuario arma reglas que vigilan las
/// conversaciones abiertas de WhatsApp y avisan cuando falta poco para que se cierre la ventana de
/// 24hs de la Cloud API — al equipo interno o al propio cliente. El robot AvisoVentanaBackgroundService
/// es quien las dispara. Los destinatarios internos viven en Auto_Destinatarios con clave "waventana:{id}"
/// (misma libretita de Personas del Centro de Automatizaciones).
/// </summary>
[ApiController]
[Route("api/aviso-ventana")]
[Authorize]
public class AvisoVentanaController : ControllerBase
{
    private readonly AppDbContext _db;
    public AvisoVentanaController(AppDbContext db) { _db = db; }

    private const string MENSAJE_DEFAULT =
        "⏰ La charla de WhatsApp con {cliente} ({linea}) se cierra en {tiempo}. Si hay que responderle, hacelo antes de perder la ventana.";

    // ---------- DTOs (nombres alineados con el frontend: AutoLineaDto / AutoPersonaDto) ----------
    public record LineaDto(string PhoneId, string Numero, string? Nombre);
    public record PersonaDto(int Id, string Nombre, long? TelegramChatId, string? WhatsAppNumero, string? Email, bool Activo);
    public record ReglaDto(int Id, string Nombre, bool Activa, string? WatchLineaPhoneId, string? SoloNumeros,
        List<int> UmbralesMin, string Destino, string? SaleLineaPhoneId, string Mensaje, List<int> Destinatarios);
    public record ReglaUpsert(string Nombre, bool Activa, string? WatchLineaPhoneId, string? SoloNumeros,
        List<int>? UmbralesMin, string Destino, string? SaleLineaPhoneId, string Mensaje, List<int>? Destinatarios);
    public record BundleDto(List<ReglaDto> Reglas, List<LineaDto> Lineas, List<PersonaDto> Personas);

    // ---------- Helpers ----------
    private static List<int> ParseUmbrales(string? csv) => (csv ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n > 0).Distinct()
        .OrderByDescending(n => n).ToList();

    private static string UmbralesCsv(List<int>? l) => string.Join(",",
        (l ?? new List<int>()).Where(n => n > 0).Distinct().OrderByDescending(n => n));

    private async Task<List<LineaDto>> LineasAsync()
    {
        var raw = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith("whatsapp.linea."))
            .Select(s => new { s.Key, s.Value })
            .ToListAsync();
        var cfg = await _db.WhatsAppLineasConfig.AsNoTracking().ToDictionaryAsync(c => c.LineaId, c => c.Nombre);
        return raw
            .Select(l => new { PhoneId = l.Key.Substring("whatsapp.linea.".Length), Numero = l.Value ?? "" })
            .Where(l => !l.Numero.StartsWith("IG ", StringComparison.Ordinal)) // solo WhatsApp (no Instagram)
            .Select(l => new LineaDto(l.PhoneId, l.Numero, cfg.TryGetValue(l.PhoneId, out var n) ? n : null))
            .OrderBy(l => l.Numero)
            .ToList();
    }

    private async Task<List<PersonaDto>> PersonasAsync()
        => await _db.AutoPersonas.AsNoTracking().Where(p => p.Activo).OrderBy(p => p.Nombre)
            .Select(p => new PersonaDto(p.Id, p.Nombre, p.TelegramChatId, p.WhatsAppNumero, p.Email, p.Activo))
            .ToListAsync();

    private async Task<List<int>> DestinatariosDeAsync(int reglaId) =>
        await _db.AutoDestinatarios.Where(d => d.AutoKey == $"waventana:{reglaId}")
            .Select(d => d.PersonaId).ToListAsync();

    private async Task GuardarDestinatariosAsync(int reglaId, List<int>? personas)
    {
        if (personas is null) return;
        var key = $"waventana:{reglaId}";
        _db.AutoDestinatarios.RemoveRange(_db.AutoDestinatarios.Where(d => d.AutoKey == key));
        foreach (var pid in personas.Distinct())
            _db.AutoDestinatarios.Add(new AutoDestinatario { AutoKey = key, PersonaId = pid });
        await _db.SaveChangesAsync();
    }

    private static string? Validar(ReglaUpsert r)
    {
        if (string.IsNullOrWhiteSpace(r.Nombre)) return "Poné un nombre a la regla";
        if (string.IsNullOrWhiteSpace(r.Mensaje)) return "Escribí el mensaje del aviso";
        if (r.Destino != "INTERNO" && r.Destino != "CLIENTE") return "Destino inválido";
        if ((r.UmbralesMin ?? new()).All(n => n <= 0)) return "Elegí al menos un momento (12h, 6h, 2h, 1h o 15min)";
        return null;
    }

    private static ReglaDto Map(WhatsAppAvisoVentanaRegla a, List<int> destinatarios) => new(
        a.Id, a.Nombre, a.Activa, a.WatchLineaPhoneId, a.SoloNumeros,
        ParseUmbrales(a.UmbralesMin), a.Destino, a.SaleLineaPhoneId, a.Mensaje, destinatarios);

    // ---------- Endpoints ----------
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var reglas = await _db.WhatsAppAvisoVentanaReglas.OrderBy(a => a.Id).ToListAsync();
        var dest = await _db.AutoDestinatarios.Where(d => d.AutoKey.StartsWith("waventana:")).ToListAsync();
        var reglasDto = reglas.Select(a => Map(a,
            dest.Where(d => d.AutoKey == $"waventana:{a.Id}").Select(d => d.PersonaId).ToList())).ToList();
        return Ok(new BundleDto(reglasDto, await LineasAsync(), await PersonasAsync()));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ReglaUpsert r)
    {
        var err = Validar(r);
        if (err is not null) return BadRequest(new { error = err });
        var a = new WhatsAppAvisoVentanaRegla
        {
            Nombre = r.Nombre.Trim(),
            Activa = r.Activa,
            WatchLineaPhoneId = string.IsNullOrWhiteSpace(r.WatchLineaPhoneId) ? null : r.WatchLineaPhoneId.Trim(),
            SoloNumeros = string.IsNullOrWhiteSpace(r.SoloNumeros) ? null : r.SoloNumeros.Trim(),
            UmbralesMin = UmbralesCsv(r.UmbralesMin),
            Destino = r.Destino,
            SaleLineaPhoneId = string.IsNullOrWhiteSpace(r.SaleLineaPhoneId) ? null : r.SaleLineaPhoneId.Trim(),
            Mensaje = r.Mensaje.Trim()
        };
        _db.WhatsAppAvisoVentanaReglas.Add(a);
        await _db.SaveChangesAsync();
        await GuardarDestinatariosAsync(a.Id, r.Destinatarios);
        // Si nace activa, "línea base": no disparar por el backlog de charlas ya pasadas de un umbral.
        if (a.Activa) await AvisoVentanaBackgroundService.BaselineReglaAsync(_db, a);
        return Ok(Map(a, await DestinatariosDeAsync(a.Id)));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ReglaUpsert r)
    {
        var a = await _db.WhatsAppAvisoVentanaReglas.FirstOrDefaultAsync(x => x.Id == id);
        if (a is null) return NotFound();
        var err = Validar(r);
        if (err is not null) return BadRequest(new { error = err });
        a.Nombre = r.Nombre.Trim();
        a.Activa = r.Activa;
        a.WatchLineaPhoneId = string.IsNullOrWhiteSpace(r.WatchLineaPhoneId) ? null : r.WatchLineaPhoneId.Trim();
        a.SoloNumeros = string.IsNullOrWhiteSpace(r.SoloNumeros) ? null : r.SoloNumeros.Trim();
        a.UmbralesMin = UmbralesCsv(r.UmbralesMin);
        a.Destino = r.Destino;
        a.SaleLineaPhoneId = string.IsNullOrWhiteSpace(r.SaleLineaPhoneId) ? null : r.SaleLineaPhoneId.Trim();
        a.Mensaje = r.Mensaje.Trim();
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await GuardarDestinatariosAsync(a.Id, r.Destinatarios);
        // 2026-08-13: en CUALQUIER guardado de una regla activa, línea base: marca lo ya cruzado como
        // "ya avisado" (SIN enviar). Así GUARDAR nunca dispara un mensaje — solo los cruces que pasen
        // de acá en más. (Antes, guardar una regla ya pasada de un umbral la re-disparaba → spam.)
        if (a.Activa) await AvisoVentanaBackgroundService.BaselineReglaAsync(_db, a);
        return Ok(Map(a, await DestinatariosDeAsync(a.Id)));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var a = await _db.WhatsAppAvisoVentanaReglas.FirstOrDefaultAsync(x => x.Id == id);
        if (a is null) return NotFound();
        _db.AutoDestinatarios.RemoveRange(_db.AutoDestinatarios.Where(d => d.AutoKey == $"waventana:{id}"));
        _db.WhatsAppAvisoVentanaReglas.Remove(a);
        await _db.SaveChangesAsync();
        return Ok();
    }

    [HttpPost("{id:int}/toggle")]
    public async Task<IActionResult> Toggle(int id)
    {
        var a = await _db.WhatsAppAvisoVentanaReglas.FirstOrDefaultAsync(x => x.Id == id);
        if (a is null) return NotFound();
        a.Activa = !a.Activa;
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        // Al PRENDER, línea base: no disparar por el backlog de charlas ya pasadas de un umbral.
        if (a.Activa) await AvisoVentanaBackgroundService.BaselineReglaAsync(_db, a);
        return Ok(Map(a, await DestinatariosDeAsync(a.Id)));
    }

    /// <summary>Dispara una PRUEBA: manda el mensaje de ejemplo por los canales/destinatarios de la regla,
    /// para verificar que llega. Las reglas "al cliente" no se prueban acá (no hay un cliente concreto).</summary>
    [HttpPost("{id:int}/probar")]
    public async Task<IActionResult> Probar(int id, [FromServices] WhatsAppOutboundService wa)
    {
        var a = await _db.WhatsAppAvisoVentanaReglas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (a is null) return NotFound();

        // Reglas "al cliente": la prueba se manda a los números de "Vigilar solo" (para no mandarle
        // a TODOS los clientes por accidente). Si no hay números puntuales, no se puede probar.
        if (a.Destino == "CLIENTE")
        {
            var nums = (a.SoloNumeros ?? "")
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (nums.Count == 0)
                return Ok(new { ok = false, detalle = "Poné un número en '🔢 Vigilar solo' para poder probar (si no, no sé a quién mandarle la prueba)." });

            var textoC = "🧪 PRUEBA · " + a.Mensaje
                .Replace("{cliente}", "vos").Replace("{tiempo}", "2 horas").Replace("{linea}", "esta línea");
            int okc = 0, totc = 0;
            foreach (var raw in nums)
            {
                var soloDig = new string(raw.Where(char.IsDigit).ToArray());
                if (soloDig.Length == 0) continue;
                totc++;
                var numero = "whatsapp:+" + soloDig;
                var (sid, canal, lin) = await wa.SendTextAsync(numero, textoC);
                if (sid != null)
                {
                    okc++;
                    _db.WhatsAppTwilioMensajes.Add(new WhatsAppTwilioMensaje
                    {
                        Direccion = "OUTGOING", Numero = numero, Cuerpo = textoC,
                        TwilioMessageSid = sid, Canal = canal, LineaPhoneId = lin, Procesado = true, CreatedAt = DateTime.UtcNow
                    });
                    await _db.SaveChangesAsync();
                }
            }
            var detC = $"📱 Prueba enviada {okc}/{totc}" + (okc < totc ? " · a los que no llegó, es porque ese número no te escribió hace <24hs (regla de Meta)." : "");
            return Ok(new { ok = okc > 0, detalle = detC });
        }

        var idsDest = await _db.AutoDestinatarios.Where(d => d.AutoKey == $"waventana:{id}")
            .Select(d => d.PersonaId).ToListAsync();
        var pers = await _db.AutoPersonas.AsNoTracking()
            .Where(p => p.Activo && idsDest.Contains(p.Id) && p.WhatsAppNumero != null).ToListAsync();
        if (pers.Count == 0)
            return Ok(new { ok = false, detalle = "La regla no tiene a nadie tildado con WhatsApp cargado." });

        var texto = "🧪 PRUEBA · " + a.Mensaje
            .Replace("{cliente}", "Juan Pérez")
            .Replace("{tiempo}", "2 horas")
            .Replace("{linea}", "esta línea");

        int ok = 0, tot = 0;
        foreach (var p in pers)
        {
            tot++;
            var numero = p.WhatsAppNumero!.StartsWith("whatsapp:") ? p.WhatsAppNumero : "whatsapp:" + p.WhatsAppNumero;
            var (sid, canal, lin) = await wa.SendTextAsync(numero, texto, lineaOverride: a.SaleLineaPhoneId);
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
        var detalle = $"📱 WhatsApp {ok}/{tot}" + (ok < tot ? " · a los que no llegó, es porque no te escribieron a esa línea en las últimas 24hs (regla de Meta)." : "");
        return Ok(new { ok = ok > 0, detalle });
    }

    /// <summary>2026-08-13: crea las 2 reglas de arranque (una por línea: TRANSRADIO y FRIKAF),
    /// avisando al equipo (Osmar + Gabriel) en 12h/6h/2h/1h/15min. Idempotente: si ya hay reglas, no hace nada.</summary>
    [HttpPost("seed")]
    public async Task<IActionResult> Seed()
    {
        if (await _db.WhatsAppAvisoVentanaReglas.AnyAsync())
            return Ok(new { creadas = 0, detalle = "Ya había reglas cargadas; no toqué nada." });

        var lineas = await LineasAsync();
        string? Linea(params string[] keys) => lineas.FirstOrDefault(l =>
            keys.Any(k => (l.Nombre ?? "").ToUpperInvariant().Contains(k) || (l.Numero ?? "").Contains(k)))?.PhoneId;
        var transradio = Linea("TRANSRADIO", "120812933");
        var frikaf = Linea("FRIKAF", "INTERVENT", "122525458");

        var personas = await _db.AutoPersonas.Where(p => p.Activo).ToListAsync();
        int? Pid(string name) => personas.FirstOrDefault(p => p.Nombre.ToUpperInvariant().Contains(name))?.Id;
        var dest = new[] { "OSMAR", "GABRIEL" }.Select(Pid).Where(x => x != null).Select(x => x!.Value).ToList();

        var creadas = new List<(string nombre, string? linea)>
        {
            ("Aviso cierre ventana — FIJO TRANSRADIO", transradio),
            ("Aviso cierre ventana — FRIKAF by INTERVENT", frikaf),
        };

        int n = 0;
        foreach (var (nombre, linea) in creadas)
        {
            var a = new WhatsAppAvisoVentanaRegla
            {
                Nombre = nombre,
                Activa = false,                  // nacen APAGADAS: se revisan y se prenden a mano (seguridad)
                WatchLineaPhoneId = linea,       // null si no encontró la línea → vigila todas (igual sirve)
                SaleLineaPhoneId = linea,
                UmbralesMin = "720,360,120,60,15",
                Destino = "INTERNO",
                Mensaje = MENSAJE_DEFAULT
            };
            _db.WhatsAppAvisoVentanaReglas.Add(a);
            await _db.SaveChangesAsync();
            await GuardarDestinatariosAsync(a.Id, dest);
            n++;
        }
        return Ok(new { creadas = n, detalle = $"Creé {n} reglas de arranque (APAGADAS). Revisá líneas y destinatarios, y prendé la que quieras. OJO: si vigila TODAS las charlas y avisás al equipo, en una cuenta con muchas conversaciones puede mandar muchos mensajes." });
    }
}
