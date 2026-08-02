using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace Api.Controllers;

/// <summary>
/// Webhook receptor + envio Twilio WhatsApp + chat para el dashboard.
/// </summary>
[ApiController]
[Route("api/whatsapp/twilio")]
public class WhatsAppTwilioController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<WhatsAppTwilioController> _logger;
    private readonly WhatsAppOutboundService _outbound;
    private readonly CafeReciboCobranzaPdfService _cobranzaPdfService;
    private readonly CafeVentasController _ventasController;
    private readonly MetaWhatsAppService _meta;
    private readonly CafeListasCustomController _listasCustomController;

    public WhatsAppTwilioController(AppDbContext db, ILogger<WhatsAppTwilioController> logger, WhatsAppOutboundService outbound, CafeReciboCobranzaPdfService cobranzaPdfService, CafeVentasController ventasController, MetaWhatsAppService meta, CafeListasCustomController listasCustomController)
    {
        _db = db;
        _logger = logger;
        _outbound = outbound;
        _cobranzaPdfService = cobranzaPdfService;
        _ventasController = ventasController;
        _meta = meta;
        _listasCustomController = listasCustomController;
    }

    // ===== Menu de identificacion de rol (auto-respuesta a numeros nuevos) =====
    // Marca textual unica para detectar mensajes "menu" en el historial.
    private const string MenuRolMarca = "Respondé con un número (1, 2 o 3)";
    private const string MenuRolTexto =
        "¡Hola! 👋 Para atenderte mejor, contestá con un número.\n\n" +
        "Respondé con un número (1, 2 o 3):\n\n" +
        "1) 🛍️ Soy cliente\n" +
        "2) 📦 Soy proveedor\n" +
        "3) 👥 Otros";
    private static readonly Dictionary<string, (string Rol, string Bienvenida)> RolPorOpcion = new()
    {
        ["1"] = ("cliente",   "¡Genial! 🛍️ Te marcamos como cliente. En breve te atendemos por acá."),
        ["2"] = ("proveedor", "¡Genial! 📦 Te marcamos como proveedor. En breve te atendemos por acá."),
        ["3"] = ("otro",      "¡Genial! 👍 Te anotamos. En breve te atendemos por acá.")
    };

    /// <summary>POST /api/whatsapp/twilio/webhook — Twilio postea aca cada mensaje entrante.</summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Webhook([FromForm] IFormCollection form)
    {
        var from = form["From"].ToString();
        var body = form["Body"].ToString();
        var profileName = form["ProfileName"].ToString();
        var messageSid = form["MessageSid"].ToString();
        int.TryParse(form["NumMedia"].ToString(), out var numMedia);
        var mediaUrl = numMedia > 0 ? form["MediaUrl0"].ToString() : null;

        _logger.LogInformation("WhatsApp Twilio IN: {From} ({Name}) → {Body}", from, profileName, body);

        var msg = new WhatsAppTwilioMensaje
        {
            Direccion = "INCOMING",
            Numero = from,
            NombrePerfil = string.IsNullOrEmpty(profileName) ? null : profileName,
            Cuerpo = body,
            MediaUrl = mediaUrl,
            NumMedia = numMedia,
            TwilioMessageSid = messageSid,
            Procesado = true, // Fase 2: marcamos como visto. Conversion a venta es manual desde el chat.
            CreatedAt = DateTime.UtcNow
        };
        _db.WhatsAppTwilioMensajes.Add(msg);
        await _db.SaveChangesAsync();

        // ===== Flujo identificacion de rol =====
        // Si el numero NO tiene contacto cargado: o le mandamos el menu (primera vez) o procesamos su respuesta 1/2/3.
        var contactoExistente = await _db.WhatsAppTwilioContactos.FirstOrDefaultAsync(c => c.Numero == from);
        if (contactoExistente == null && _outbound.AnyConfigured)
        {
            // Detectar si ya le mandamos el menu antes (busca la marca textual en mensajes OUTGOING al numero)
            var menuPrevio = await _db.WhatsAppTwilioMensajes
                .Where(m => m.Numero == from && m.Direccion == "OUTGOING" && m.Cuerpo != null && m.Cuerpo.Contains(MenuRolMarca))
                .OrderByDescending(m => m.CreatedAt)
                .FirstOrDefaultAsync();

            var respuesta = (body ?? "").Trim();
            if (menuPrevio != null && RolPorOpcion.TryGetValue(respuesta, out var seleccion))
            {
                // El usuario respondio 1/2/3 al menu: crear contacto + bienvenida.
                _db.WhatsAppTwilioContactos.Add(new WhatsAppTwilioContacto
                {
                    Numero = from,
                    Nombre = string.IsNullOrWhiteSpace(profileName) ? from.Replace("whatsapp:", "") : profileName,
                    Rol = seleccion.Rol,
                    Activo = true
                });
                await _db.SaveChangesAsync();
                await EnviarYRegistrarAsync(from, seleccion.Bienvenida);
            }
            else if (menuPrevio == null)
            {
                // Primera vez que escribe: mandar el menu.
                await EnviarYRegistrarAsync(from, MenuRolTexto);
            }
            // else: ya le mandamos el menu y respondio otra cosa que no es 1/2/3 -> dejar que el operador atienda manual.
        }

        return Content("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response></Response>", "text/xml");
    }

    /// <summary>Helper interno: envia texto via Twilio y registra el OUTGOING en BD. No tira excepciones (loguea).</summary>
    private async Task<string?> EnviarYRegistrarAsync(string numero, string texto)
    {
        try
        {
            var (sid, canal, lin) = await _outbound.SendTextAsync(numero, texto);
            _db.WhatsAppTwilioMensajes.Add(new WhatsAppTwilioMensaje
            {
                Direccion = "OUTGOING",
                Numero = numero,
                Cuerpo = texto,
                TwilioMessageSid = sid,
                Canal = canal,
                LineaPhoneId = lin,
                Procesado = true,
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
            return sid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando auto-mensaje a {Numero}", numero);
            return null;
        }
    }

    public record MenuRolRequest(string Numero);

    /// <summary>POST /api/whatsapp/twilio/menu-rol — envia manualmente el menu de identificacion a un numero.</summary>
    [HttpPost("menu-rol")]
    [Authorize]
    public async Task<IActionResult> EnviarMenuRol([FromBody] MenuRolRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Numero))
            return BadRequest(new { error = "Numero requerido" });
        if (!_outbound.AnyConfigured)
            return StatusCode(503, new { error = "WhatsApp no configurado (ni Meta ni Twilio)" });

        var numero = req.Numero.Trim();
        if (!numero.StartsWith("whatsapp:")) numero = "whatsapp:" + numero;

        // 2026-07-23: si Meta está configurado, mandamos el menú NUEVO con botones (el del bot
        // de bienvenida: elegir empresa). Si no, cae al texto viejo 1/2/3 por Twilio.
        string? sid;
        if (_meta.IsConfigured)
        {
            // 2026-07-23 (multi-línea): el menú sale por la línea donde venía la conversación
            var lineaConv = await _db.WhatsAppTwilioMensajes
                .Where(x => x.Numero == numero && x.Direccion == "INCOMING" && x.LineaPhoneId != null)
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => x.LineaPhoneId)
                .FirstOrDefaultAsync();
            sid = await _meta.SendButtonsAsync(numero, WhatsAppBotFlow.CuerpoNivel1, WhatsAppBotFlow.BotonesNivel1, lineaPhoneId: lineaConv);
            if (sid != null)
            {
                _db.WhatsAppTwilioMensajes.Add(new WhatsAppTwilioMensaje
                {
                    Direccion = "OUTGOING",
                    Numero = numero,
                    Cuerpo = WhatsAppBotFlow.CuerpoNivel1 + " [botones: Frikaf / Intervent / Intereventos]",
                    TwilioMessageSid = sid,
                    Canal = "CLOUD",
                    LineaPhoneId = lineaConv,
                    Procesado = true,
                    CreatedAt = DateTime.UtcNow
                });
                await _db.SaveChangesAsync();
            }
        }
        else
        {
            sid = await EnviarYRegistrarAsync(numero, MenuRolTexto);
        }
        if (sid == null) return StatusCode(500, new { error = "No se pudo enviar el menú (ver logs)" });
        return Ok(new { ok = true, sid });
    }

    public record SendRequest(string Numero, string Mensaje, string? LineaPhoneId = null);

    /// <summary>POST /api/whatsapp/twilio/send — envia un mensaje desde el chat del dashboard.</summary>
    [HttpPost("send")]
    [Authorize]
    public async Task<IActionResult> Send([FromBody] SendRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Numero) || string.IsNullOrWhiteSpace(req.Mensaje))
            return BadRequest(new { error = "Numero y mensaje son obligatorios" });

        if (!_outbound.AnyConfigured)
            return StatusCode(503, new { error = "WhatsApp no configurado: falta META_WA_TOKEN/PHONE_ID (Meta) o TWILIO_ACCOUNT_SID/AUTH_TOKEN (Twilio)" });

        try
        {
            var (sid, canal, lin) = await _outbound.SendTextAsync(req.Numero, req.Mensaje, req.LineaPhoneId);
            var msg = new WhatsAppTwilioMensaje
            {
                Direccion = "OUTGOING",
                Numero = req.Numero,
                Cuerpo = req.Mensaje,
                TwilioMessageSid = sid,
                Canal = canal,
                LineaPhoneId = lin,
                Procesado = true,
                CreatedAt = DateTime.UtcNow
            };
            _db.WhatsAppTwilioMensajes.Add(msg);
            await _db.SaveChangesAsync();
            return Ok(new { ok = true, sid, id = msg.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando mensaje WhatsApp Twilio");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ===== 2026-08-01: INICIAR CONVERSACIÓN NUEVA con plantilla aprobada =====

    /// <summary>Lista las plantillas APROBADAS de la WABA (para escribir primero, fuera de la ventana de 24h).</summary>
    [HttpGet("plantillas")]
    [Authorize]
    public async Task<IActionResult> Plantillas()
    {
        var todas = await _meta.GetTemplatesAsync();
        var aprobadas = todas
            .Where(t => string.Equals(t.Status, "APPROVED", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(t.Name, "hello_world", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Category).ThenBy(t => t.Name)
            .Select(t => new { t.Name, t.Language, t.Category, t.BodyText, t.VariableCount })
            .ToList();
        return Ok(aprobadas);
    }

    /// <summary>Lista las líneas de WhatsApp conectadas (phone_id + número visible) para elegir desde cuál iniciar.</summary>
    [HttpGet("lineas")]
    [Authorize]
    public async Task<IActionResult> Lineas()
    {
        var lineas = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith("whatsapp.linea."))
            .OrderBy(s => s.Value)
            .Select(s => new { PhoneId = s.Key.Substring("whatsapp.linea.".Length), Numero = s.Value })
            .ToListAsync();
        return Ok(lineas);
    }

    /// <summary>2026-08-01: nombre + imagen personalizados de cada línea (WhatsApp e Instagram),
    /// para identificarlas mejor. Devuelve TODAS las líneas conocidas (con o sin config).</summary>
    [HttpGet("lineas-config")]
    [Authorize]
    public async Task<IActionResult> LineasConfig()
    {
        var lineas = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith("whatsapp.linea."))
            .Select(s => new { Id = s.Key.Substring("whatsapp.linea.".Length), Numero = s.Value })
            .ToListAsync();
        var cfg = await _db.WhatsAppLineasConfig.AsNoTracking().ToDictionaryAsync(c => c.LineaId, c => c);
        var res = lineas.Select(l =>
        {
            cfg.TryGetValue(l.Id, out var c);
            return new
            {
                LineaId = l.Id,
                NumeroReal = l.Numero,
                EsInstagram = (l.Numero ?? "").StartsWith("IG ", StringComparison.Ordinal),
                Nombre = c?.Nombre,
                ImagenDataUrl = c?.ImagenDataUrl
            };
        }).OrderBy(x => x.EsInstagram).ThenBy(x => x.NumeroReal).ToList();
        return Ok(res);
    }

    public record LineaConfigUpsert(string LineaId, string? Nombre, string? ImagenDataUrl);

    /// <summary>Guarda (o borra) el nombre/imagen de una línea. ImagenDataUrl: null = no tocar, "" = quitar.</summary>
    [HttpPost("lineas-config")]
    [Authorize]
    public async Task<IActionResult> GuardarLineaConfig([FromBody] LineaConfigUpsert req)
    {
        if (string.IsNullOrWhiteSpace(req.LineaId))
            return BadRequest(new { error = "Falta la línea" });
        if (req.ImagenDataUrl != null && req.ImagenDataUrl.Length > 700_000)
            return BadRequest(new { error = "La imagen es muy grande. Probá con una más chica (menos de 500 KB)." });

        var cfg = await _db.WhatsAppLineasConfig.FirstOrDefaultAsync(c => c.LineaId == req.LineaId);
        if (cfg == null)
        {
            cfg = new WhatsAppLineaConfig { LineaId = req.LineaId };
            _db.WhatsAppLineasConfig.Add(cfg);
        }
        cfg.Nombre = string.IsNullOrWhiteSpace(req.Nombre) ? null : req.Nombre.Trim();
        if (req.ImagenDataUrl != null)
            cfg.ImagenDataUrl = req.ImagenDataUrl.Length == 0 ? null : req.ImagenDataUrl;
        cfg.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>INICIA una conversación mandando una plantilla aprobada a un número. WhatsApp lo exige para
    /// escribir primero. Guarda el saliente en la bandeja (Canal=CLOUD) para que aparezca en el chat.</summary>
    [HttpPost("iniciar")]
    [Authorize]
    public async Task<IActionResult> Iniciar([FromBody] IniciarRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Numero) || string.IsNullOrWhiteSpace(req.Plantilla) || string.IsNullOrWhiteSpace(req.Idioma))
            return BadRequest(new { error = "Número, plantilla e idioma son obligatorios" });
        if (!_meta.IsConfigured)
            return StatusCode(503, new { error = "WhatsApp Cloud (Meta) no está configurado" });

        var digits = MetaWhatsAppService.NormalizeTo(req.Numero);
        if (digits.Length < 10)
            return BadRequest(new { error = "El número no parece válido. Poné el número completo con código de país (ej: 5491122525458)." });
        var numeroStd = "whatsapp:+" + digits;

        try
        {
            var vars = req.Variables ?? new List<string>();
            var wamid = await _meta.SendTemplateAsync(digits, req.Plantilla, req.Idioma, vars, lineaPhoneId: req.LineaPhoneId);
            if (wamid == null)
                return StatusCode(502, new { error = "Meta rechazó el envío. Revisá el número, las variables o el método de pago de la cuenta de WhatsApp." });

            var cuerpo = MetaWhatsAppService.RenderTemplateBody(req.CuerpoPreview, vars);
            if (string.IsNullOrWhiteSpace(cuerpo)) cuerpo = $"[Plantilla: {req.Plantilla}]";
            var msg = new WhatsAppTwilioMensaje
            {
                Direccion = "OUTGOING",
                Numero = numeroStd,
                Cuerpo = cuerpo,
                TwilioMessageSid = wamid,
                Canal = "CLOUD",
                LineaPhoneId = req.LineaPhoneId,
                Procesado = true,
                CreatedAt = DateTime.UtcNow
            };
            _db.WhatsAppTwilioMensajes.Add(msg);
            await _db.SaveChangesAsync();
            return Ok(new { ok = true, numero = numeroStd, sid = wamid, id = msg.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error iniciando conversación WhatsApp por plantilla");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    public record IniciarRequest(string Numero, string Plantilla, string Idioma, string? LineaPhoneId, List<string>? Variables, string? CuerpoPreview);

    /// <summary>GET /api/whatsapp/twilio/conversaciones — lista numeros agrupados con ultimo mensaje.
    /// Si el numero esta en WhatsApp_TwilioContactos, devuelve NombreContacto + Rol (prevalece sobre NombrePerfil de WhatsApp).</summary>
    [HttpGet("conversaciones")]
    [Authorize]
    public async Task<IActionResult> Conversaciones()
    {
        var conv = await _db.WhatsAppTwilioMensajes
            .AsNoTracking()
            // 2026-08-01: agrupar por (Número + Línea) para que el MISMO contacto escribiendo a 2 líneas
            // nuestras aparezca como 2 conversaciones separadas y no se crucen los hilos.
            .GroupBy(m => new { m.Numero, m.LineaPhoneId })
            .Select(g => new
            {
                Numero = g.Key.Numero,
                // 2026-07-31: el nombre de perfil más reciente que NO sea nulo (en IG a veces el 1er
                // mensaje entra sin nombre y se completa en el siguiente; así no perdemos el remitente).
                NombrePerfil = g.Where(m => m.Direccion == "INCOMING" && m.NombrePerfil != null).OrderByDescending(m => m.CreatedAt).Select(m => m.NombrePerfil).FirstOrDefault(),
                UltimoMensaje = g.OrderByDescending(m => m.CreatedAt).Select(m => m.Cuerpo).FirstOrDefault(),
                UltimoDireccion = g.OrderByDescending(m => m.CreatedAt).Select(m => m.Direccion).FirstOrDefault(),
                UltimoAt = g.Max(m => m.CreatedAt),
                Total = g.Count(),
                // 2026-08-01: la línea es ahora parte de la clave del grupo (número+línea)
                Linea = g.Key.LineaPhoneId,
                // 2026-07-31: canal del último mensaje (TWILIO/CLOUD/INSTAGRAM) para el iconito en el chat
                Canal = g.OrderByDescending(m => m.CreatedAt).Select(m => m.Canal).FirstOrDefault()
            })
            .ToListAsync();
        // Nombre visible de cada línea (lo auto-registra el webhook en AppSettings)
        var lineasNombres = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith("whatsapp.linea."))
            .ToDictionaryAsync(s => s.Key.Substring("whatsapp.linea.".Length), s => s.Value);
        // Join in-memory con contactos (poco volumen, mas simple que LINQ join)
        var contactos = await _db.WhatsAppTwilioContactos.AsNoTracking()
            .Where(c => c.Activo).ToDictionaryAsync(c => c.Numero, c => c);
        var clienteIds = contactos.Values.Where(c => c.ClienteId.HasValue).Select(c => c.ClienteId!.Value).Distinct().ToList();
        var clientes = await _db.CafeClientes.AsNoTracking()
            .Where(x => clienteIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Nombre, x.CodigoInterno })
            .ToDictionaryAsync(x => x.Id);
        var result = conv.Select(x =>
        {
            contactos.TryGetValue(x.Numero, out var c);
            string? clienteNombre = null;
            if (c?.ClienteId != null && clientes.TryGetValue(c.ClienteId.Value, out var cli)) clienteNombre = cli.Nombre;
            return new
            {
                x.Numero,
                NombrePerfil = c?.Nombre ?? x.NombrePerfil,
                Rol = c?.Rol,
                ClienteId = c?.ClienteId,
                ClienteNombre = clienteNombre,
                x.UltimoMensaje,
                x.UltimoDireccion,
                x.UltimoAt,
                x.Total,
                x.Linea,
                LineaNumero = x.Linea != null && lineasNombres.TryGetValue(x.Linea, out var ln) ? ln : null,
                x.Canal
            };
        }).OrderByDescending(x => x.UltimoAt).ToList();
        return Ok(result);
    }

    // ===== Respuestas rapidas CRUD =====
    public record RespuestaUpsert(string Nombre, string Texto, int Orden, bool Activo);

    [HttpGet("respuestas-rapidas")]
    [Authorize]
    public async Task<IActionResult> ListarRespuestas()
    {
        var list = await _db.WhatsAppTwilioRespuestasRapidas.AsNoTracking()
            .OrderBy(r => r.Orden).ThenBy(r => r.Id).ToListAsync();
        return Ok(list);
    }

    [HttpPost("respuestas-rapidas")]
    [Authorize]
    public async Task<IActionResult> CrearRespuesta([FromBody] RespuestaUpsert req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre) || string.IsNullOrWhiteSpace(req.Texto))
            return BadRequest(new { error = "Nombre y texto son obligatorios" });
        var r = new WhatsAppTwilioRespuestaRapida
        {
            Nombre = req.Nombre.Trim(),
            Texto = req.Texto,
            Orden = req.Orden,
            Activo = req.Activo
        };
        _db.WhatsAppTwilioRespuestasRapidas.Add(r);
        await _db.SaveChangesAsync();
        return Ok(r);
    }

    [HttpPut("respuestas-rapidas/{id:int}")]
    [Authorize]
    public async Task<IActionResult> EditarRespuesta(int id, [FromBody] RespuestaUpsert req)
    {
        var r = await _db.WhatsAppTwilioRespuestasRapidas.FindAsync(id);
        if (r == null) return NotFound();
        r.Nombre = req.Nombre.Trim();
        r.Texto = req.Texto;
        r.Orden = req.Orden;
        r.Activo = req.Activo;
        await _db.SaveChangesAsync();
        return Ok(r);
    }

    [HttpDelete("respuestas-rapidas/{id:int}")]
    [Authorize]
    public async Task<IActionResult> BorrarRespuesta(int id)
    {
        var r = await _db.WhatsAppTwilioRespuestasRapidas.FindAsync(id);
        if (r == null) return NotFound();
        _db.WhatsAppTwilioRespuestasRapidas.Remove(r);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ===== Datos bancarios / CBUs CRUD (2026-07-29) =====
    public record CbuUpsert(string Nombre, string Banco, string TipoCuenta, string Titular, string Cuit, string Cbu, string Alias, string Mail, int Orden, bool Activo);

    [HttpGet("datos-bancarios")]
    [Authorize]
    public async Task<IActionResult> ListarCbus()
    {
        var list = await _db.WhatsAppTwilioDatosBancarios.AsNoTracking()
            .OrderBy(r => r.Orden).ThenBy(r => r.Id).ToListAsync();
        return Ok(list);
    }

    [HttpPost("datos-bancarios")]
    [Authorize]
    public async Task<IActionResult> CrearCbu([FromBody] CbuUpsert req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { error = "El nombre es obligatorio" });
        var r = new WhatsAppTwilioDatoBancario
        {
            Nombre = (req.Nombre ?? "").Trim(),
            Banco = (req.Banco ?? "").Trim(),
            TipoCuenta = (req.TipoCuenta ?? "").Trim(),
            Titular = (req.Titular ?? "").Trim(),
            Cuit = (req.Cuit ?? "").Trim(),
            Cbu = (req.Cbu ?? "").Trim(),
            Alias = (req.Alias ?? "").Trim(),
            Mail = (req.Mail ?? "").Trim(),
            Orden = req.Orden,
            Activo = req.Activo
        };
        _db.WhatsAppTwilioDatosBancarios.Add(r);
        await _db.SaveChangesAsync();
        return Ok(r);
    }

    [HttpPut("datos-bancarios/{id:int}")]
    [Authorize]
    public async Task<IActionResult> EditarCbu(int id, [FromBody] CbuUpsert req)
    {
        var r = await _db.WhatsAppTwilioDatosBancarios.FindAsync(id);
        if (r == null) return NotFound();
        r.Nombre = (req.Nombre ?? "").Trim();
        r.Banco = (req.Banco ?? "").Trim();
        r.TipoCuenta = (req.TipoCuenta ?? "").Trim();
        r.Titular = (req.Titular ?? "").Trim();
        r.Cuit = (req.Cuit ?? "").Trim();
        r.Cbu = (req.Cbu ?? "").Trim();
        r.Alias = (req.Alias ?? "").Trim();
        r.Mail = (req.Mail ?? "").Trim();
        r.Orden = req.Orden;
        r.Activo = req.Activo;
        await _db.SaveChangesAsync();
        return Ok(r);
    }

    [HttpDelete("datos-bancarios/{id:int}")]
    [Authorize]
    public async Task<IActionResult> BorrarCbu(int id)
    {
        var r = await _db.WhatsAppTwilioDatosBancarios.FindAsync(id);
        if (r == null) return NotFound();
        _db.WhatsAppTwilioDatosBancarios.Remove(r);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ===== Contactos CRUD =====
    public record ContactoUpsert(string Numero, string Nombre, string Rol, string? Notas, bool Activo, int? ClienteId);

    [HttpGet("contactos")]
    [Authorize]
    public async Task<IActionResult> ListarContactos()
    {
        var list = await _db.WhatsAppTwilioContactos.AsNoTracking()
            .OrderBy(c => c.Nombre).ToListAsync();
        // Join in-memory con CafeClientes
        var ids = list.Where(c => c.ClienteId.HasValue).Select(c => c.ClienteId!.Value).Distinct().ToList();
        var clientes = await _db.CafeClientes.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.Nombre, x.CodigoInterno })
            .ToDictionaryAsync(x => x.Id);
        var result = list.Select(c => new
        {
            c.Id, c.Numero, c.Nombre, c.Rol, c.Notas, c.Activo, c.ClienteId,
            ClienteNombre = c.ClienteId.HasValue && clientes.TryGetValue(c.ClienteId.Value, out var cli) ? cli.Nombre : null,
            ClienteCodigo = c.ClienteId.HasValue && clientes.TryGetValue(c.ClienteId.Value, out var cli2) ? (cli2.CodigoInterno?.ToString()) : null
        }).ToList();
        return Ok(result);
    }

    /// <summary>GET /api/whatsapp/twilio/clientes-buscar?q=texto — busqueda liviana para autocomplete.</summary>
    [HttpGet("clientes-buscar")]
    [Authorize]
    public async Task<IActionResult> BuscarClientes([FromQuery] string q = "", [FromQuery] int top = 15)
    {
        q = (q ?? "").Trim();
        var query = _db.CafeClientes.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            int.TryParse(q, out var qNum);
            query = query.Where(c => c.Nombre.Contains(q)
                || (qNum > 0 && c.CodigoInterno == qNum)
                || (c.Telefono != null && c.Telefono.Contains(q)));
        }
        var list = await query
            .OrderBy(c => c.Nombre)
            .Take(Math.Clamp(top, 1, 50))
            .Select(c => new { c.Id, c.Nombre, CodigoInterno = c.CodigoInterno.HasValue ? c.CodigoInterno.ToString() : null, c.Telefono })
            .ToListAsync();
        return Ok(list);
    }

    [HttpPost("contactos")]
    [Authorize]
    public async Task<IActionResult> CrearContacto([FromBody] ContactoUpsert req)
    {
        if (string.IsNullOrWhiteSpace(req.Numero) || string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { error = "Numero y nombre son obligatorios" });
        var numero = req.Numero.Trim();
        if (!numero.StartsWith("whatsapp:")) numero = "whatsapp:" + numero;
        if (await _db.WhatsAppTwilioContactos.AnyAsync(c => c.Numero == numero))
            return BadRequest(new { error = "Ese numero ya esta cargado" });
        var c = new WhatsAppTwilioContacto
        {
            Numero = numero,
            Nombre = req.Nombre.Trim(),
            Rol = string.IsNullOrWhiteSpace(req.Rol) ? "otro" : req.Rol.Trim(),
            Notas = req.Notas,
            Activo = req.Activo,
            ClienteId = req.ClienteId
        };
        _db.WhatsAppTwilioContactos.Add(c);
        await _db.SaveChangesAsync();
        return Ok(c);
    }

    [HttpPut("contactos/{id:int}")]
    [Authorize]
    public async Task<IActionResult> EditarContacto(int id, [FromBody] ContactoUpsert req)
    {
        var c = await _db.WhatsAppTwilioContactos.FindAsync(id);
        if (c == null) return NotFound();
        c.Nombre = req.Nombre.Trim();
        c.Rol = string.IsNullOrWhiteSpace(req.Rol) ? "otro" : req.Rol.Trim();
        c.Notas = req.Notas;
        c.Activo = req.Activo;
        c.ClienteId = req.ClienteId;
        await _db.SaveChangesAsync();
        return Ok(c);
    }

    /// <summary>2026-07-23 (pedido Osmar): borra una conversación completa (todos los mensajes de
    /// ese número + sus reacciones) DEL SISTEMA. El chat en el celular del cliente no se toca.
    /// El contacto (si existe) queda: si vuelve a escribir, arranca conversación nueva con su nombre.</summary>
    [HttpDelete("conversaciones")]
    [Authorize]
    public async Task<IActionResult> BorrarConversacion([FromQuery] string numero)
    {
        if (string.IsNullOrWhiteSpace(numero)) return BadRequest(new { error = "Falta el número" });
        var ids = await _db.WhatsAppTwilioMensajes
            .Where(m => m.Numero == numero).Select(m => m.Id).ToListAsync();
        if (ids.Count == 0) return NotFound(new { error = "No hay mensajes de ese número" });

        var reacs = await _db.WhatsAppTwilioReacciones.Where(r => ids.Contains(r.MensajeId)).ToListAsync();
        _db.WhatsAppTwilioReacciones.RemoveRange(reacs);
        var msgs = await _db.WhatsAppTwilioMensajes.Where(m => m.Numero == numero).ToListAsync();
        _db.WhatsAppTwilioMensajes.RemoveRange(msgs);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Conversación {Numero} borrada ({Count} mensajes)", numero, msgs.Count);
        return Ok(new { ok = true, borrados = msgs.Count });
    }

    // ===== Reacciones a mensajes =====
    // 2026-07-23 (pedido Osmar): ademas de guardarse como etiqueta interna, si el mensaje entro por
    // la Cloud API (Canal=CLOUD, tiene wamid) la reaccion SE MANDA al WhatsApp del cliente — la ve
    // en su celu como una reaccion comun. Quitar la reaccion tambien se la saca al cliente.
    // OJO: WhatsApp permite UNA reaccion nuestra por mensaje: si marcas dos emojis, el cliente ve el ultimo.
    public record ReaccionRequest(int MensajeId, string Emoji);

    /// <summary>POST /reacciones — toggle: si ya existe ese emoji para ese mensaje, lo borra; sino lo crea.</summary>
    [HttpPost("reacciones")]
    [Authorize]
    public async Task<IActionResult> ToggleReaccion([FromBody] ReaccionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Emoji)) return BadRequest();
        var existing = await _db.WhatsAppTwilioReacciones
            .FirstOrDefaultAsync(r => r.MensajeId == req.MensajeId && r.Emoji == req.Emoji);
        bool removed;
        if (existing != null)
        {
            _db.WhatsAppTwilioReacciones.Remove(existing);
            await _db.SaveChangesAsync();
            removed = true;
        }
        else
        {
            _db.WhatsAppTwilioReacciones.Add(new WhatsAppTwilioReaccion
            {
                MensajeId = req.MensajeId,
                Emoji = req.Emoji,
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
            removed = false;
        }

        // Mandar la reaccion real al cliente (solo mensajes de la Cloud API, que tienen wamid)
        var enviadaAlCliente = false;
        try
        {
            var msg = await _db.WhatsAppTwilioMensajes.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == req.MensajeId);
            if (msg is not null && msg.Canal == "CLOUD"
                && !string.IsNullOrWhiteSpace(msg.TwilioMessageSid)
                && msg.TwilioMessageSid.StartsWith("wamid.", StringComparison.OrdinalIgnoreCase))
            {
                // Al quitar mandamos emoji vacio (Meta la saca del celu del cliente).
                // 2026-07-23 (multi-línea): la reacción sale por la línea del propio mensaje.
                var sid = await _meta.SendReactionAsync(msg.Numero, msg.TwilioMessageSid, removed ? "" : req.Emoji, lineaPhoneId: msg.LineaPhoneId);
                enviadaAlCliente = sid != null && !removed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo mandar la reaccion al cliente (mensaje {Id})", req.MensajeId);
        }

        return Ok(new { ok = true, removed, enviadaAlCliente });
    }

    [HttpDelete("contactos/{id:int}")]
    [Authorize]
    public async Task<IActionResult> BorrarContacto(int id)
    {
        var c = await _db.WhatsAppTwilioContactos.FindAsync(id);
        if (c == null) return NotFound();
        _db.WhatsAppTwilioContactos.Remove(c);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>GET /api/whatsapp/twilio/mensajes?numero=whatsapp:+34... — devuelve el hilo de un numero con reacciones.</summary>
    [HttpGet("mensajes")]
    [Authorize]
    public async Task<IActionResult> Mensajes([FromQuery] string? numero, [FromQuery] string? linea = null, [FromQuery] int top = 200)
    {
        var q = _db.WhatsAppTwilioMensajes.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(numero)) q = q.Where(m => m.Numero == numero);
        // 2026-08-01: filtrar por LÍNEA para no mezclar hilos del mismo contacto en 2 líneas.
        // Sentinela "null" = la conversación de la línea sin registrar (Twilio/legacy). Si el
        // parámetro no viene (deep-links viejos), no se filtra (compatibilidad hacia atrás).
        if (linea != null)
        {
            var lp = linea == "null" ? null : linea;
            q = q.Where(m => m.LineaPhoneId == lp);
        }
        var msgs = await q
            .OrderByDescending(m => m.CreatedAt)
            .Take(Math.Clamp(top, 1, 500))
            .Select(m => new
            {
                m.Id, m.Direccion, m.Numero, m.NombrePerfil,
                m.Cuerpo, m.MediaUrl, m.MediaFilename, m.NumMedia,
                m.Procesado, m.RespuestaEnviada, m.CreatedAt, m.EstadoEntrega
            })
            .ToListAsync();
        msgs.Reverse();
        // Cargar reacciones de estos mensajes
        var ids = msgs.Select(m => m.Id).ToList();
        var reacciones = await _db.WhatsAppTwilioReacciones.AsNoTracking()
            .Where(r => ids.Contains(r.MensajeId))
            .GroupBy(r => new { r.MensajeId, r.Emoji })
            .Select(g => new { g.Key.MensajeId, g.Key.Emoji, Count = g.Count() })
            .ToListAsync();
        var reacByMsg = reacciones.GroupBy(r => r.MensajeId)
            .ToDictionary(g => g.Key, g => g.Select(x => new { x.Emoji, x.Count }).ToList());
        var result = msgs.Select(m => new
        {
            m.Id, m.Direccion, m.Numero, m.NombrePerfil, m.Cuerpo,
            m.MediaUrl, m.MediaFilename, m.NumMedia, m.Procesado, m.RespuestaEnviada, m.CreatedAt, m.EstadoEntrega,
            Reacciones = reacByMsg.TryGetValue(m.Id, out var rs) ? rs.Cast<object>().ToList() : new List<object>()
        }).ToList();
        return Ok(result);
    }

    // ===== ADJUNTOS — Fase 1: Subir desde PC =====
    // Path donde se guardan los archivos. Existe como volume montado igual que /data/files.
    private const string UploadsDir = "/data/whatsapp-uploads";

    public record UploadResp(string Token, string Url, string OriginalFilename, long SizeBytes, string ContentType, DateTime ExpiresAt);

    /// <summary>POST /api/whatsapp/twilio/upload — sube un archivo y devuelve URL publica con token de 24h.</summary>
    [HttpPost("upload")]
    [Authorize]
    [RequestSizeLimit(20 * 1024 * 1024)] // 20 MB margen sobre el limite de Twilio (16 MB)
    public async Task<IActionResult> Upload([FromForm] IFormFile? file)
    {
        if (file == null || file.Length == 0) return BadRequest(new { error = "No se recibio archivo" });
        if (file.Length > 16 * 1024 * 1024) return BadRequest(new { error = "El archivo supera el limite de 16 MB que admite WhatsApp" });

        Directory.CreateDirectory(UploadsDir);

        var token = GenerarToken();
        var ext = Path.GetExtension(file.FileName);
        var stored = token + ext;
        var path = Path.Combine(UploadsDir, stored);
        using (var fs = System.IO.File.Create(path)) await file.CopyToAsync(fs);

        var contentType = string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType;
        var originalFilename = file.FileName;

        // 2026-08-01: notas de voz — WhatsApp solo acepta audio en OGG/OPUS. El navegador graba webm/mp4,
        // así que si es audio lo convertimos con ffmpeg a ogg/opus mono para que llegue como nota de voz REAL.
        if (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            var oggStored = token + ".ogg";
            var oggPath = Path.Combine(UploadsDir, oggStored);
            if (await ConvertirAOggOpusAsync(path, oggPath))
            {
                try { System.IO.File.Delete(path); } catch { }
                stored = oggStored;
                ext = ".ogg";
                contentType = "audio/ogg";
                var baseName = Path.GetFileNameWithoutExtension(originalFilename);
                originalFilename = (string.IsNullOrWhiteSpace(baseName) ? "audio" : baseName) + ".ogg";
            }
            // si ffmpeg falla, se queda el original y se manda como archivo común
        }

        var up = new WhatsAppTwilioUpload
        {
            Token = token,
            OriginalFilename = originalFilename,
            StoredFilename = stored,
            ContentType = contentType,
            SizeBytes = new FileInfo(Path.Combine(UploadsDir, stored)).Length,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        };
        _db.WhatsAppTwilioUploads.Add(up);
        await _db.SaveChangesAsync();

        // La extension va en la URL para que el chat sepa si mostrar vista previa de imagen.
        var url = $"{Request.Scheme}://{Request.Host}/api/whatsapp/twilio/files/{token}{ext}";
        return Ok(new UploadResp(token, url, up.OriginalFilename, up.SizeBytes, up.ContentType, up.ExpiresAt));
    }

    /// <summary>2026-08-01: convierte un audio (webm/mp4/...) a OGG/OPUS mono con ffmpeg (formato de nota
    /// de voz que acepta WhatsApp). Devuelve true si salió bien. Si ffmpeg no está o falla, devuelve false.</summary>
    private async Task<bool> ConvertirAOggOpusAsync(string input, string output)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -i \"{input}\" -vn -c:a libopus -b:a 32k -ar 48000 -ac 1 \"{output}\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            var salioATiempo = await Task.Run(() => p.WaitForExit(30000));
            if (!salioATiempo) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0 && System.IO.File.Exists(output) && new FileInfo(output).Length > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffmpeg: no se pudo convertir el audio a ogg/opus");
            return false;
        }
    }

    /// <summary>GET /api/whatsapp/twilio/files/{token} — sirve el archivo publicamente (sin auth)
    /// para que lo baje el proveedor (Twilio/Meta) y para mostrarlo en el chat.
    /// 2026-07-23: el token puede venir CON extension (ej "abc123.jpg"). Se agrega a la URL para que
    /// la pantalla sepa que es una imagen y muestre la vista previa (antes, sin extension, mostraba
    /// todo como "archivo adjunto"). Los tokens son base64url y NO tienen puntos, asi que sacar la
    /// extension es seguro y las URLs viejas (sin extension) siguen funcionando igual.</summary>
    [HttpGet("files/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> ServirArchivo(string token)
    {
        var tokenLimpio = Path.GetFileNameWithoutExtension(token);
        var up = await _db.WhatsAppTwilioUploads.FirstOrDefaultAsync(u => u.Token == tokenLimpio);
        if (up == null) return NotFound();
        if (up.ExpiresAt < DateTime.UtcNow) return NotFound(new { error = "Expirado" });

        var path = Path.Combine(UploadsDir, up.StoredFilename);
        if (!System.IO.File.Exists(path)) return NotFound();

        // Marcar primera descarga (cuando el proveedor lo baje)
        if (up.DownloadedAt == null)
        {
            up.DownloadedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        // Las imagenes se sirven "inline" para poder previsualizarlas en el chat;
        // el resto va como descarga, con su nombre original.
        if (up.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return PhysicalFile(path, up.ContentType);

        return PhysicalFile(path, up.ContentType, up.OriginalFilename);
    }

    public record SendMediaRequest(string Numero, string MediaUrl, string? Caption, string? OriginalFilename, string? LineaPhoneId = null);

    /// <summary>POST /api/whatsapp/twilio/send-media — envia mensaje con adjunto via Twilio.</summary>
    [HttpPost("send-media")]
    [Authorize]
    public async Task<IActionResult> SendMedia([FromBody] SendMediaRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Numero) || string.IsNullOrWhiteSpace(req.MediaUrl))
            return BadRequest(new { error = "Numero y mediaUrl son obligatorios" });
        if (!_outbound.AnyConfigured)
            return StatusCode(503, new { error = "WhatsApp no configurado (ni Meta ni Twilio)" });

        try
        {
            // El nombre original importa: la URL /files/{token} no tiene extension, asi que
            // sin el no se puede saber si mandarlo como imagen o como documento.
            var (sid, canal, lin) = await _outbound.SendMediaAsync(req.Numero, req.MediaUrl, req.Caption, req.OriginalFilename, req.LineaPhoneId);
            var msg = new WhatsAppTwilioMensaje
            {
                Direccion = "OUTGOING",
                Numero = req.Numero,
                Cuerpo = req.Caption ?? "",
                MediaUrl = req.MediaUrl,
                MediaFilename = req.OriginalFilename,
                NumMedia = 1,
                TwilioMessageSid = sid,
                Canal = canal,
                LineaPhoneId = lin,
                Procesado = true,
                CreatedAt = DateTime.UtcNow
            };
            _db.WhatsAppTwilioMensajes.Add(msg);
            await _db.SaveChangesAsync();
            return Ok(new { ok = true, sid, id = msg.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando media WhatsApp");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private static string GenerarToken()
    {
        var bytes = new byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').Replace("=", "");
    }

    // ===== ADJUNTOS — Fase 2: Archivos del servidor =====
    // Busca/lista archivos que ya viven en el sistema (uploads previos, cobranzas, etc).
    // El operador los puede elegir sin tener que descargarlos a su PC y resubirlos.

    public record ServerFileDto(string Tipo, int Id, string Label, string? SubLabel, string? Info, DateTime Fecha);

    /// <summary>GET /api/whatsapp/twilio/server-files?tipo=UPLOAD|COBRANZA&amp;search=&amp;take=20
    /// Lista archivos disponibles en el servidor para adjuntar al chat.</summary>
    [HttpGet("server-files")]
    [Authorize]
    public async Task<IActionResult> ServerFiles([FromQuery] string tipo, [FromQuery] string? search = null, [FromQuery] int take = 30)
    {
        if (take < 1 || take > 100) take = 30;
        var s = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        if (tipo == "UPLOAD")
        {
            var q = _db.WhatsAppTwilioUploads.Where(u => u.ExpiresAt > DateTime.UtcNow);
            if (s != null)
            {
                q = q.Where(u => u.OriginalFilename.Contains(s));
            }
            var list = await q.OrderByDescending(u => u.CreatedAt).Take(take)
                .Select(u => new ServerFileDto(
                    "UPLOAD", u.Id, u.OriginalFilename,
                    $"{FormatSize(u.SizeBytes)} · {u.ContentType}",
                    u.NumeroDestino != null ? $"Mandado antes a {u.NumeroDestino}" : null,
                    u.CreatedAt))
                .ToListAsync();
            return Ok(list);
        }
        if (tipo == "COBRANZA")
        {
            var q = _db.CafeCobranzas.Include(c => c.Cliente).Where(c => c.Estado == "VIGENTE");
            if (s != null)
            {
                int? sInt = int.TryParse(s, out var nn) ? nn : null;
                q = q.Where(c =>
                    c.Numero.Contains(s)
                    || (c.Cliente != null && (
                        c.Cliente.Nombre.Contains(s)
                        || (c.Cliente.RazonSocial != null && c.Cliente.RazonSocial.Contains(s))
                        || (sInt.HasValue && c.Cliente.CodigoInterno == sInt.Value))));
            }
            var list = await q.OrderByDescending(c => c.Fecha).Take(take)
                .Select(c => new ServerFileDto(
                    "COBRANZA", c.Id, $"Recibo {c.Numero}",
                    c.Cliente != null ? c.Cliente.Nombre : "—",
                    $"${c.Total:N0}",
                    c.Fecha))
                .ToListAsync();
            return Ok(list);
        }
        if (tipo == "VENTA")
        {
            // 2026-06-22: ventas/facturas/cotizaciones. Excluye anuladas.
            var q = _db.CafeVentas.Where(v => v.Estado != "anulado");
            if (s != null)
            {
                int? sInt = int.TryParse(s, out var nn) ? nn : null;
                q = q.Where(v =>
                    v.Numero.Contains(s)
                    || (v.ClienteNombreSnapshot != null && v.ClienteNombreSnapshot.Contains(s))
                    || (v.ClienteRazonSocialSnapshot != null && v.ClienteRazonSocialSnapshot.Contains(s))
                    || (sInt.HasValue && _db.CafeClientes.Any(c => c.Id == v.ClienteId && c.CodigoInterno == sInt.Value)));
            }
            // 2026-07-24 (pedido Osmar): mostrar el importe CON IVA (si es factura ARCA autorizada)
            // y el N° de factura AFIP (0000-00000000) además del número interno del comprobante.
            // Traemos los campos crudos y formateamos en memoria (el formato :D no traduce a SQL).
            var rows = await q.OrderByDescending(v => v.Fecha).Take(take)
                .Select(v => new
                {
                    v.Id, v.TipoComprobante, v.Numero, v.Total, v.ArcaImpTotal,
                    v.ArcaEstado, v.ArcaCae, v.ArcaPtoVta, v.ArcaCbteNro, v.Fecha,
                    Cliente = !string.IsNullOrWhiteSpace(v.ClienteRazonSocialSnapshot) ? v.ClienteRazonSocialSnapshot : (v.ClienteNombreSnapshot ?? "—")
                })
                .ToListAsync();
            var list = rows.Select(v =>
            {
                var autorizada = v.ArcaEstado == "autorizado" && !string.IsNullOrEmpty(v.ArcaCae)
                                 && v.ArcaPtoVta.HasValue && v.ArcaCbteNro.HasValue;
                // Título: comprobante + número interno + (N° AFIP si la factura está autorizada)
                var label = $"{(v.TipoComprobante ?? "X")} {v.Numero}";
                if (autorizada)
                    label += $" · N° {v.ArcaPtoVta:D4}-{v.ArcaCbteNro:D8}";
                // Importe: el total CON IVA de la factura si está autorizada; sino el total de la venta.
                var monto = (autorizada && v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0) ? v.ArcaImpTotal.Value : v.Total;
                var info = $"${monto:N0}" + (autorizada ? " c/IVA" : "");
                return new ServerFileDto("VENTA", v.Id, label, v.Cliente, info, v.Fecha);
            }).ToList();
            return Ok(list);
        }
        if (tipo == "LISTA")
        {
            // 2026-07-23 (pedido Osmar): listas de precios personalizadas, para mandarlas por el chat
            var q = _db.CafeListasPreciosCustom.Include(l => l.ClienteNav).Where(l => l.IsActive);
            if (s != null)
                q = q.Where(l => l.Nombre.Contains(s)
                    || (l.ClienteNav != null && l.ClienteNav.Nombre.Contains(s))
                    || (l.NumeroLista != null && l.NumeroLista.Contains(s)));
            var list = await q.OrderBy(l => l.Nombre).Take(take)
                .Select(l => new ServerFileDto(
                    "LISTA", l.Id, $"💲 {l.Nombre}",
                    l.ClienteNav != null ? $"Cliente: {l.ClienteNav.Nombre}" : (l.TipoCliente ?? "General"),
                    l.NumeroLista != null ? $"Lista N° {l.NumeroLista}" : "",
                    l.UpdatedAt))
                .ToListAsync();
            return Ok(list);
        }
        return BadRequest(new { error = "Tipo no soportado. Validos: UPLOAD, COBRANZA, VENTA, LISTA" });
    }

    public record SendServerFileRequest(string Numero, string Tipo, int Id, string? Caption, string? LineaPhoneId = null);

    /// <summary>POST /api/whatsapp/twilio/send-server-file
    /// Envía un archivo del servidor al WhatsApp del numero indicado.</summary>
    [HttpPost("send-server-file")]
    [Authorize]
    public async Task<IActionResult> SendServerFile([FromBody] SendServerFileRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Numero)) return BadRequest(new { error = "Numero obligatorio" });
        if (!_outbound.AnyConfigured) return StatusCode(503, new { error = "WhatsApp no configurado (ni Meta ni Twilio)" });

        string mediaUrl;
        string filename;

        switch (req.Tipo)
        {
            case "UPLOAD":
            {
                // Reusa el upload existente — extiende su expiracion 24h mas asi Twilio alcanza a descargarlo.
                var up = await _db.WhatsAppTwilioUploads.FirstOrDefaultAsync(u => u.Id == req.Id);
                if (up == null) return NotFound(new { error = "Upload no encontrado" });
                if (up.ExpiresAt < DateTime.UtcNow.AddHours(1))
                {
                    up.ExpiresAt = DateTime.UtcNow.AddHours(24);
                    await _db.SaveChangesAsync();
                }
                mediaUrl = $"{Request.Scheme}://{Request.Host}/api/whatsapp/twilio/files/{up.Token}{Path.GetExtension(up.StoredFilename)}";
                filename = up.OriginalFilename;
                break;
            }
            case "COBRANZA":
            {
                var c = await _db.CafeCobranzas
                    .Include(x => x.Cliente)
                    .Include(x => x.Comprobantes).ThenInclude(cc => cc.Venta)
                    .Include(x => x.Medios).ThenInclude(m => m.Caja)
                    .Include(x => x.Medios).ThenInclude(m => m.Cheque)
                    .FirstOrDefaultAsync(x => x.Id == req.Id);
                if (c == null) return NotFound(new { error = "Cobranza no encontrada" });
                if (c.Cliente == null) return BadRequest(new { error = "Cobranza sin cliente, no se puede generar PDF" });

                var settings = await _db.CafeSettings.FindAsync(1);
                var comps = c.Comprobantes.Select(x => (
                    numero: x.Venta?.Numero ?? "",
                    importe: x.Importe,
                    aCuenta: x.VentaId is null
                )).ToList();
                var medios = c.Medios.Select(m => (
                    cajaNombre: m.Caja?.Nombre ?? "—",
                    importe: m.Importe,
                    referencia: m.Referencia,
                    chequeInfo: m.Cheque is null ? null : $"Cheque {m.Cheque.Banco} N° {m.Cheque.Numero}"
                )).ToList();
                var bytes = _cobranzaPdfService.GenerarPdfBytes(c, c.Cliente, comps, medios, settings);
                filename = $"Recibo-{c.Numero}.pdf";

                // Guardar como upload nuevo con token, asi Twilio lo descarga via URL publica.
                Directory.CreateDirectory(UploadsDir);
                var token = GenerarToken();
                var stored = token + ".pdf";
                await System.IO.File.WriteAllBytesAsync(Path.Combine(UploadsDir, stored), bytes);
                var up = new WhatsAppTwilioUpload
                {
                    Token = token,
                    OriginalFilename = filename,
                    StoredFilename = stored,
                    ContentType = "application/pdf",
                    SizeBytes = bytes.Length,
                    NumeroDestino = req.Numero,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddHours(24)
                };
                _db.WhatsAppTwilioUploads.Add(up);
                await _db.SaveChangesAsync();
                mediaUrl = $"{Request.Scheme}://{Request.Host}/api/whatsapp/twilio/files/{token}{Path.GetExtension(stored)}";
                break;
            }
            case "VENTA":
            {
                var v = await _db.CafeVentas
                    .Include(x => x.Items).ThenInclude(i => i.ProductoNav)
                    .FirstOrDefaultAsync(x => x.Id == req.Id);
                if (v == null) return NotFound(new { error = "Venta no encontrada" });
                var cfg = await _db.CafeSettings.FindAsync(1);

                // Reusa exactamente la misma logica del endpoint GET /cafe/ventas/{id}/pdf
                // (factura ARCA si autorizada / cotizacion sino). Garantiza que el PDF que mandamos
                // por WhatsApp == el PDF que descarga el operador desde la pantalla.
                var bytes = await _ventasController.GenerarPdfBytesAsync(v, cfg);
                filename = CafeVentasController.BuildPdfFilename(v);
                if (!filename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) filename += ".pdf";

                Directory.CreateDirectory(UploadsDir);
                var token = GenerarToken();
                var stored = token + ".pdf";
                await System.IO.File.WriteAllBytesAsync(Path.Combine(UploadsDir, stored), bytes);
                var up = new WhatsAppTwilioUpload
                {
                    Token = token,
                    OriginalFilename = filename,
                    StoredFilename = stored,
                    ContentType = "application/pdf",
                    SizeBytes = bytes.Length,
                    NumeroDestino = req.Numero,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddHours(24)
                };
                _db.WhatsAppTwilioUploads.Add(up);
                await _db.SaveChangesAsync();
                mediaUrl = $"{Request.Scheme}://{Request.Host}/api/whatsapp/twilio/files/{token}{Path.GetExtension(stored)}";
                break;
            }
            case "LISTA":
            {
                // 2026-07-23 (pedido Osmar): manda el PDF de una lista de precios personalizada.
                // Reusa la MISMA generación que el botón "Descargar PDF" de /cafe/listas-precios-custom.
                var (bytes, fname) = await _listasCustomController.GenerarPdfBytesAsync(req.Id);
                if (bytes is null) return NotFound(new { error = "Lista no encontrada o inactiva" });
                filename = fname;

                Directory.CreateDirectory(UploadsDir);
                var token = GenerarToken();
                var stored = token + ".pdf";
                await System.IO.File.WriteAllBytesAsync(Path.Combine(UploadsDir, stored), bytes);
                var up = new WhatsAppTwilioUpload
                {
                    Token = token,
                    OriginalFilename = filename,
                    StoredFilename = stored,
                    ContentType = "application/pdf",
                    SizeBytes = bytes.Length,
                    NumeroDestino = req.Numero,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddHours(24)
                };
                _db.WhatsAppTwilioUploads.Add(up);
                await _db.SaveChangesAsync();
                mediaUrl = $"{Request.Scheme}://{Request.Host}/api/whatsapp/twilio/files/{token}{Path.GetExtension(stored)}";
                break;
            }
            default:
                return BadRequest(new { error = "Tipo no soportado. Validos: UPLOAD, COBRANZA, VENTA, LISTA" });
        }

        try
        {
            var (sid, canal, lin) = await _outbound.SendMediaAsync(req.Numero, mediaUrl, req.Caption, filename, req.LineaPhoneId);
            var msg = new WhatsAppTwilioMensaje
            {
                Direccion = "OUTGOING",
                Numero = req.Numero,
                Cuerpo = req.Caption ?? "",
                MediaUrl = mediaUrl,
                MediaFilename = filename,
                NumMedia = 1,
                TwilioMessageSid = sid,
                Canal = canal,
                LineaPhoneId = lin,
                Procesado = true,
                CreatedAt = DateTime.UtcNow
            };
            _db.WhatsAppTwilioMensajes.Add(msg);
            await _db.SaveChangesAsync();
            return Ok(new { ok = true, sid, id = msg.Id, filename });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando server-file WhatsApp Twilio");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / 1024.0 / 1024.0:F1} MB";
    }
}
