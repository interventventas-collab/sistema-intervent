using System.Text.Json;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-08-26: mensajes de WhatsApp agendados para salir más tarde ("escribile a la tarde").
///
/// Vive en un archivo aparte a propósito: WhatsAppTwilioController ya tiene 2600 líneas y varias
/// sesiones tocan ese archivo a la vez. La ruta sí cuelga del mismo lugar que el resto del chat.
/// </summary>
[ApiController]
[Route("api/whatsapp/twilio/programados")]
[Authorize]
public class WhatsAppProgramadosController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly WhatsAppProgramadosService _svc;
    private readonly MetaWhatsAppService _meta;
    private readonly ILogger<WhatsAppProgramadosController> _logger;

    /// <summary>Tope de cuán lejos se puede agendar. No es un límite técnico: es para que no queden
    /// mensajes olvidados saliendo dentro de un año a un cliente que ya ni se acuerda.</summary>
    private const int MaxDiasAdelante = 60;

    public WhatsAppProgramadosController(AppDbContext db, WhatsAppProgramadosService svc,
        MetaWhatsAppService meta, ILogger<WhatsAppProgramadosController> logger)
    {
        _db = db; _svc = svc; _meta = meta; _logger = logger;
    }

    public record ProgramadoDto(int Id, string Numero, string? LineaPhoneId, string Tipo, string? Texto,
        string? MediaUrl, string? MediaFilename, string? Plantilla, string? Idioma, string? CuerpoPreview,
        DateTime ProgramadoPara, string Estado, DateTime? EnviadoAt, string? Error, string? CreadoPorNombre,
        DateTime CreatedAt);

    public record CrearRequest(string Numero, string? LineaPhoneId, string Tipo, string? Texto,
        string? MediaUrl, string? MediaFilename, int? UploadId, string? Plantilla, string? Idioma,
        List<string>? Variables, string? CuerpoPreview, DateTime ProgramadoPara);

    public record EditarRequest(string? Texto, DateTime? ProgramadoPara);

    /// <summary>GET — qué tiene agendado un número. Devuelve primero lo PENDIENTE (que es lo que
    /// importa: todavía se puede frenar) y después los últimos resueltos, para poder mirar atrás.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] string numero, [FromQuery] int historial = 5)
    {
        if (string.IsNullOrWhiteSpace(numero)) return BadRequest(new { error = "Falta el número" });

        var pendientes = await _db.WhatsAppMensajesProgramados.AsNoTracking()
            .Where(x => x.Numero == numero && x.Estado == WhatsAppMensajeProgramado.EstadoPendiente)
            .OrderBy(x => x.ProgramadoPara)
            .ToListAsync();

        var resueltos = await _db.WhatsAppMensajesProgramados.AsNoTracking()
            .Where(x => x.Numero == numero && x.Estado != WhatsAppMensajeProgramado.EstadoPendiente)
            .OrderByDescending(x => x.UpdatedAt)
            .Take(Math.Clamp(historial, 0, 50))
            .ToListAsync();

        return Ok(new
        {
            pendientes = pendientes.Select(ToDto).ToList(),
            resueltos = resueltos.Select(ToDto).ToList(),
            ventanaAbierta = await _svc.VentanaAbiertaAsync(numero),
            ventanaCierra = await VentanaCierraAsync(numero)
        });
    }

    /// <summary>POST — agenda un mensaje. No manda nada ahora: solo lo anota.</summary>
    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Numero)) return BadRequest(new { error = "Falta el número" });

        var tipo = (req.Tipo ?? "").Trim().ToUpperInvariant();
        if (tipo != WhatsAppMensajeProgramado.TipoTexto && tipo != WhatsAppMensajeProgramado.TipoAdjunto
            && tipo != WhatsAppMensajeProgramado.TipoPlantilla)
            return BadRequest(new { error = "Tipo desconocido (esperaba TEXTO, ADJUNTO o PLANTILLA)" });

        var cuando = DateTime.SpecifyKind(req.ProgramadoPara, DateTimeKind.Utc);
        // Un minuto de gracia: si el reloj del navegador va unos segundos atrasado, no queremos
        // rebotarle un "es en el pasado" al operador que eligió la hora que viene.
        if (cuando < DateTime.UtcNow.AddMinutes(-1))
            return BadRequest(new { error = "Esa hora ya pasó. Elegí un horario futuro." });
        if (cuando > DateTime.UtcNow.AddDays(MaxDiasAdelante))
            return BadRequest(new { error = $"No se puede programar a más de {MaxDiasAdelante} días." });

        switch (tipo)
        {
            case WhatsAppMensajeProgramado.TipoTexto when string.IsNullOrWhiteSpace(req.Texto):
                return BadRequest(new { error = "Escribí el mensaje que querés programar." });
            case WhatsAppMensajeProgramado.TipoAdjunto when string.IsNullOrWhiteSpace(req.MediaUrl):
                return BadRequest(new { error = "Falta el archivo adjunto." });
            case WhatsAppMensajeProgramado.TipoPlantilla when string.IsNullOrWhiteSpace(req.Plantilla):
                return BadRequest(new { error = "Elegí la plantilla." });
            case WhatsAppMensajeProgramado.TipoPlantilla when !_meta.IsConfigured:
                return StatusCode(503, new { error = "WhatsApp Cloud (Meta) no está configurado: las plantillas salen solo por ahí." });
        }

        var fila = new WhatsAppMensajeProgramado
        {
            Numero = req.Numero,
            LineaPhoneId = string.IsNullOrWhiteSpace(req.LineaPhoneId) ? null : req.LineaPhoneId,
            Tipo = tipo,
            Texto = req.Texto,
            MediaUrl = req.MediaUrl,
            MediaFilename = req.MediaFilename,
            UploadId = req.UploadId,
            Plantilla = req.Plantilla,
            Idioma = string.IsNullOrWhiteSpace(req.Idioma) ? "es_AR" : req.Idioma,
            VariablesJson = req.Variables is { Count: > 0 } ? JsonSerializer.Serialize(req.Variables) : null,
            CuerpoPreview = req.CuerpoPreview,
            ProgramadoPara = cuando,
            Estado = WhatsAppMensajeProgramado.EstadoPendiente,
            CreadoPorUserId = UserId(),
            CreadoPorNombre = QuienEs(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.WhatsAppMensajesProgramados.Add(fila);
        await _db.SaveChangesAsync();

        await EstirarVencimientoAdjuntoAsync(fila);

        return Ok(new { ok = true, programado = ToDto(fila), aviso = await AvisoVentanaAsync(fila) });
    }

    /// <summary>PUT — cambiar el texto o la hora de uno que todavía no salió.</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Editar(int id, [FromBody] EditarRequest req)
    {
        var fila = await _db.WhatsAppMensajesProgramados.FirstOrDefaultAsync(x => x.Id == id);
        if (fila == null) return NotFound(new { error = "No existe ese mensaje programado" });
        if (fila.Estado != WhatsAppMensajeProgramado.EstadoPendiente)
            return BadRequest(new { error = "Ese mensaje ya se resolvió: no se puede editar." });

        if (req.Texto != null)
        {
            if (fila.Tipo == WhatsAppMensajeProgramado.TipoTexto && string.IsNullOrWhiteSpace(req.Texto))
                return BadRequest(new { error = "El mensaje no puede quedar vacío." });
            if (fila.Tipo == WhatsAppMensajeProgramado.TipoPlantilla)
                return BadRequest(new { error = "El texto de una plantilla lo define Meta: cancelá este y programá otro." });
            fila.Texto = req.Texto;
        }

        if (req.ProgramadoPara is DateTime nueva)
        {
            var cuando = DateTime.SpecifyKind(nueva, DateTimeKind.Utc);
            if (cuando < DateTime.UtcNow.AddMinutes(-1))
                return BadRequest(new { error = "Esa hora ya pasó. Elegí un horario futuro." });
            if (cuando > DateTime.UtcNow.AddDays(MaxDiasAdelante))
                return BadRequest(new { error = $"No se puede programar a más de {MaxDiasAdelante} días." });
            fila.ProgramadoPara = cuando;
        }

        fila.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await EstirarVencimientoAdjuntoAsync(fila);

        return Ok(new { ok = true, programado = ToDto(fila), aviso = await AvisoVentanaAsync(fila) });
    }

    /// <summary>DELETE — cancelar. No se borra la fila: queda como CANCELADO para que después se
    /// pueda ver que existió y quién lo frenó.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Cancelar(int id)
    {
        var fila = await _db.WhatsAppMensajesProgramados.FirstOrDefaultAsync(x => x.Id == id);
        if (fila == null) return NotFound(new { error = "No existe ese mensaje programado" });
        if (fila.Estado != WhatsAppMensajeProgramado.EstadoPendiente)
            return BadRequest(new { error = "Ese mensaje ya se resolvió: no hay nada que cancelar." });

        fila.Estado = WhatsAppMensajeProgramado.EstadoCancelado;
        fila.Error = $"Cancelado por {QuienEs() ?? "un operador"}.";
        fila.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>POST /{id}/ahora — "no esperes, mandalo ya".</summary>
    [HttpPost("{id:int}/ahora")]
    public async Task<IActionResult> Ahora(int id)
    {
        var fila = await _db.WhatsAppMensajesProgramados.FirstOrDefaultAsync(x => x.Id == id);
        if (fila == null) return NotFound(new { error = "No existe ese mensaje programado" });
        if (fila.Estado != WhatsAppMensajeProgramado.EstadoPendiente)
            return BadRequest(new { error = "Ese mensaje ya se resolvió." });

        var (ok, error) = await _svc.ProcesarAsync(fila);
        return ok ? Ok(new { ok = true }) : StatusCode(422, new { ok = false, error });
    }

    // ===== ayudantes =====

    /// <summary>Los adjuntos vencen a las 24 hs de subidos: si el mensaje sale después, Meta no
    /// podría bajar el archivo y el envío fallaría. Le estiramos el vencimiento hasta bien pasada
    /// la hora programada. Es a propósito acá y no un cambio global de los 24 hs.</summary>
    private async Task EstirarVencimientoAdjuntoAsync(WhatsAppMensajeProgramado fila)
    {
        if (fila.Tipo != WhatsAppMensajeProgramado.TipoAdjunto || fila.UploadId is not int upId) return;
        try
        {
            var up = await _db.WhatsAppTwilioUploads.FirstOrDefaultAsync(u => u.Id == upId);
            if (up == null) return;
            var necesario = fila.ProgramadoPara.AddHours(48);
            if (up.ExpiresAt < necesario) { up.ExpiresAt = necesario; await _db.SaveChangesAsync(); }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Programados] no pude estirar el vencimiento del adjunto {UploadId}", upId);
        }
    }

    /// <summary>Cuándo se cierra la ventana de 24 hs de este número (UTC), o null si ya está cerrada.</summary>
    private async Task<DateTime?> VentanaCierraAsync(string numero)
    {
        var ult = await _db.WhatsAppTwilioMensajes.AsNoTracking()
            .Where(x => x.Numero == numero && x.Direccion == "INCOMING")
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => (DateTime?)x.CreatedAt)
            .FirstOrDefaultAsync();
        if (ult == null) return null;
        var cierra = ult.Value.AddHours(24);
        return cierra > DateTime.UtcNow ? cierra : null;
    }

    /// <summary>Aviso en castellano si el mensaje se agendó para después de que se cierre la ventana.
    /// No bloquea: el operador manda igual sabiendo que puede no salir (el cliente todavía puede
    /// escribir antes y reabrirla). La plantilla no necesita aviso: atraviesa la ventana cerrada.</summary>
    private async Task<string?> AvisoVentanaAsync(WhatsAppMensajeProgramado fila)
    {
        if (fila.Tipo == WhatsAppMensajeProgramado.TipoPlantilla) return null;
        var cierra = await VentanaCierraAsync(fila.Numero);
        if (cierra == null)
            return "Ojo: ahora mismo no se le puede escribir texto libre (pasaron más de 24 hs desde su último mensaje). "
                 + "Si no te escribe antes de esa hora, el mensaje no va a salir. Para asegurarlo, programá una plantilla.";
        if (fila.ProgramadoPara > cierra.Value)
            return "Ojo: esa hora cae DESPUÉS de que se cierre la ventana de 24 hs de WhatsApp. "
                 + "Si el cliente no te vuelve a escribir antes, el mensaje no va a salir. Para asegurarlo, programá una plantilla.";
        return null;
    }

    private int? UserId()
    {
        var idStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return int.TryParse(idStr, out var uid) ? uid : null;
    }

    /// <summary>Quién lo programó: la firma del operador del PIN (OSMAR/GERMAN/…) si la hay, si no
    /// el nombre del usuario logueado. Sirve para que después se sepa quién dejó el mensaje puesto.</summary>
    private string? QuienEs()
    {
        var op = Request.Headers["X-Operator-Name"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(op)) return op.Trim().ToUpperInvariant();
        return User.Identity?.Name;
    }

    private static ProgramadoDto ToDto(WhatsAppMensajeProgramado x) => new(
        x.Id, x.Numero, x.LineaPhoneId, x.Tipo, x.Texto, x.MediaUrl, x.MediaFilename,
        x.Plantilla, x.Idioma, x.CuerpoPreview, x.ProgramadoPara, x.Estado, x.EnviadoAt,
        x.Error, x.CreadoPorNombre, x.CreatedAt);
}
