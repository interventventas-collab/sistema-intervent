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
    // 2026-08-25: comparar texto ignorando MAYUSCULAS y TILDES ("olavarria" encuentra "OLAVARRÍA").
    private const string COLLATE_SIN_TILDES = "SQL_Latin1_General_CP1_CI_AI";

    private readonly AppDbContext _db;
    private readonly ILogger<WhatsAppTwilioController> _logger;
    private readonly WhatsAppOutboundService _outbound;
    private readonly CafeReciboCobranzaPdfService _cobranzaPdfService;
    private readonly CafeVentasController _ventasController;
    private readonly MetaWhatsAppService _meta;
    private readonly CafeListasCustomController _listasCustomController;
    // 2026-08-05: para adjuntar reservas de alquiler y recibos de visita por el chat, reusando su PDF.
    private readonly AlqReservasController _alqReservasController;
    private readonly VisitasController _visitasController;
    // 2026-08-06: aviso de venta a internos (resumen con botones al emitir).
    private readonly VentaAvisoWhatsAppService _avisoSvc;

    // 2026-08-18: avisa en vivo a las pantallas abiertas (para que el celu no sondee cada 12 s).
    private readonly WaLiveNotifier _waLive;

    public WhatsAppTwilioController(AppDbContext db, ILogger<WhatsAppTwilioController> logger, WhatsAppOutboundService outbound, CafeReciboCobranzaPdfService cobranzaPdfService, CafeVentasController ventasController, MetaWhatsAppService meta, CafeListasCustomController listasCustomController, AlqReservasController alqReservasController, VisitasController visitasController, VentaAvisoWhatsAppService avisoSvc, WaLiveNotifier waLive)
    {
        _waLive = waLive;
        _db = db;
        _logger = logger;
        _outbound = outbound;
        _cobranzaPdfService = cobranzaPdfService;
        _ventasController = ventasController;
        _meta = meta;
        _listasCustomController = listasCustomController;
        _alqReservasController = alqReservasController;
        _visitasController = visitasController;
        _avisoSvc = avisoSvc;
    }

    // ═══════════════ AVISO DE VENTA A INTERNOS (2026-08-06) ═══════════════
    // Al emitir una venta con la copia por WhatsApp tildada, en vez del PDF entero le mandamos al
    // interno un resumen con 3 botones (comprobante / cuenta corriente / detalle). La lógica vive en
    // VentaAvisoWhatsAppService (la comparte el webhook que atiende el botón). Si el aviso está
    // apagado, el servicio cae solo al PDF de siempre. baseUrl sale del Request de este controller.
    public record SendVentaAvisoRequest(string Numero, int VentaId, string? LineaPhoneId);

    [HttpPost("send-venta-aviso")]
    [Authorize]
    public async Task<IActionResult> SendVentaAviso([FromBody] SendVentaAvisoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Numero)) return BadRequest(new { error = "Numero obligatorio" });
        if (!_outbound.AnyConfigured) return StatusCode(503, new { error = "WhatsApp no configurado (ni Meta ni Twilio)" });

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var (ok, err) = await _avisoSvc.EnviarAvisoAsync(req.Numero, req.VentaId, req.LineaPhoneId, baseUrl);
        if (!ok) return StatusCode(502, new { error = err ?? "No se pudo enviar el aviso" });
        return Ok(new { ok = true });
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

    public record MenuRolRequest(string Numero, string? LineaPhoneId = null);

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
            // 2026-08-05 (fix): el menú sale por la línea del CHAT ABIERTO, que el frontend manda en
            // req.LineaPhoneId. Antes se elegía por el último mensaje entrante del contacto, y como una
            // conversación se identifica por número + línea, salía por la línea equivocada (o por la
            // línea API por defecto cuando no había entrante con línea). Si por algún motivo no viene la
            // línea del chat, caemos al comportamiento anterior (última línea por la que escribió).
            var lineaConv = !string.IsNullOrWhiteSpace(req.LineaPhoneId)
                ? req.LineaPhoneId
                : await _db.WhatsAppTwilioMensajes
                    .Where(x => x.Numero == numero && x.Direccion == "INCOMING" && x.LineaPhoneId != null)
                    .OrderByDescending(x => x.CreatedAt)
                    .Select(x => x.LineaPhoneId)
                    .FirstOrDefaultAsync();
            var textos = await BotTextos.CargarAsync(_db);
            sid = await _meta.SendButtonsAsync(numero, textos.CuerpoNivel1, textos.BotonesNivel1, lineaPhoneId: lineaConv);
            if (sid != null)
            {
                _db.WhatsAppTwilioMensajes.Add(new WhatsAppTwilioMensaje
                {
                    Direccion = "OUTGOING",
                    Numero = numero,
                    Cuerpo = textos.CuerpoNivel1 + " [botones: Frikaf / Intervent / Intereventos]",
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

    // 2026-08-05: ReplyToMensajeId = Id (local) del mensaje que estamos CITANDO al responder.
    public record SendRequest(string Numero, string Mensaje, string? LineaPhoneId = null, int? ReplyToMensajeId = null);

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
            // 2026-08-05: si estamos respondiendo citando, buscamos el wamid del mensaje citado.
            // Solo Meta (Cloud API) soporta citar; en Twilio el context se ignora sin romper nada.
            string? replyToSid = null;
            if (req.ReplyToMensajeId is int rid)
                replyToSid = await _db.WhatsAppTwilioMensajes.Where(x => x.Id == rid)
                    .Select(x => x.TwilioMessageSid).FirstOrDefaultAsync();

            var (sid, canal, lin) = await _outbound.SendTextAsync(req.Numero, req.Mensaje, req.LineaPhoneId, replyToSid);

            // 2026-08-12: si el proveedor (Meta/Twilio) NO devolvió un id, el mensaje NO se entregó.
            // ANTES lo guardábamos igual y devolvíamos ok=true, así que la pantalla mostraba el
            // mensaje como enviado aunque el cliente nunca lo recibía (caso típico: pasaron las 24 hs
            // y hace falta plantilla). Ahora NO lo guardamos y devolvemos un cartel claro para que
            // la pantalla (escritorio, celular y flotantes) muestre el error y no una burbuja falsa.
            if (string.IsNullOrEmpty(sid))
            {
                var ultEntrante = await _db.WhatsAppTwilioMensajes
                    .Where(x => x.Numero == req.Numero && x.Direccion == "INCOMING")
                    .OrderByDescending(x => x.CreatedAt)
                    .Select(x => (DateTime?)x.CreatedAt)
                    .FirstOrDefaultAsync();
                bool ventanaCerrada = ultEntrante == null || (DateTime.UtcNow - ultEntrante.Value).TotalHours >= 24;
                var msgErr = ventanaCerrada
                    ? "No se envió: pasaron más de 24 hs desde el último mensaje del cliente. WhatsApp no permite escribirle texto libre fuera de esa ventana; tenés que mandarle una plantilla aprobada."
                    : "No se envió: WhatsApp rechazó el mensaje. Revisá el número e intentá de nuevo en unos segundos.";
                _logger.LogWarning("WhatsApp send NO entregado a {Numero} (canal {Canal}). ventanaCerrada={Cerrada}", req.Numero, canal, ventanaCerrada);
                return StatusCode(422, new { ok = false, error = msgErr });
            }

            var msg = new WhatsAppTwilioMensaje
            {
                Direccion = "OUTGOING",
                Numero = req.Numero,
                Cuerpo = req.Mensaje,
                TwilioMessageSid = sid,
                ReplyToSid = replyToSid,
                Canal = canal,
                LineaPhoneId = lin,
                Procesado = true,
                CreatedAt = DateTime.UtcNow
            };
            _db.WhatsAppTwilioMensajes.Add(msg);
            await _db.SaveChangesAsync();
            // 2026-08-18: aviso en vivo para que aparezca al instante en las otras pantallas.
            await _waLive.AvisarAsync(req.Numero, lin, "OUTGOING");
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
                ImagenDataUrl = c?.ImagenDataUrl,
                Sonido = c?.Sonido,
                Tema = c?.Tema
            };
        }).OrderBy(x => x.EsInstagram).ThenBy(x => x.NumeroReal).ToList();
        return Ok(res);
    }

    public record LineaConfigUpsert(string LineaId, string? Nombre, string? ImagenDataUrl, string? Sonido = null, string? Tema = null);

    /// <summary>2026-08-01: SOLO los sonidos por línea (liviano, sin imágenes) — para que el panel flotante
    /// lo refresque cada 10s y los cambios de sonido tomen efecto sin recargar la página.</summary>
    [HttpGet("lineas-sonidos")]
    [Authorize]
    public async Task<IActionResult> LineasSonidos()
    {
        var list = await _db.WhatsAppLineasConfig.AsNoTracking()
            .Where(c => c.Sonido != null)
            .Select(c => new { c.LineaId, c.Sonido })
            .ToListAsync();
        return Ok(list);
    }

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
        cfg.Sonido = string.IsNullOrWhiteSpace(req.Sonido) ? null : req.Sonido.Trim();
        // 2026-08-15: tema (claro/oscuro) de la pantalla cuando trabajás con esta línea.
        // null = no tocar (el modal de nombre/imagen no lo manda), "oscuro" = oscuro, cualquier otra cosa = claro.
        if (req.Tema != null)
            cfg.Tema = string.Equals(req.Tema.Trim(), "oscuro", StringComparison.OrdinalIgnoreCase) ? "oscuro" : null;
        cfg.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // 2026-08-19: SONIDOS DE AVISO SUBIDOS POR EL USUARIO
    // Los seis de siempre están hechos por código en index.html (no son archivos). Acá se
    // guardan los que sube el usuario, para que la lista de la línea tenga los suyos también.
    // Se referencian como "subido:{Id}" en WhatsApp_LineasConfig.Sonido.
    // ═══════════════════════════════════════════════════════════════════════════════

    private const int SonidoMaxBytes = 300 * 1024;   // 300 KB: para un aviso de 1-2 segundos sobra

    /// <summary>Lista de sonidos subidos (sin el audio, que puede pesar). Para armar el desplegable.</summary>
    [HttpGet("sonidos")]
    [Authorize]
    public async Task<IActionResult> Sonidos()
    {
        var list = await _db.WhatsAppSonidos.AsNoTracking()
            .OrderBy(x => x.Nombre)
            .Select(x => new { x.Id, x.Nombre, Clave = "subido:" + x.Id })
            .ToListAsync();
        return Ok(list);
    }

    /// <summary>Devuelve el audio para que el navegador lo reproduzca. La cookie de sesión viaja sola.</summary>
    [HttpGet("sonidos/{id:int}/audio")]
    [Authorize]
    public async Task<IActionResult> SonidoAudio(int id)
    {
        var s = await _db.WhatsAppSonidos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return NotFound();
        // Cacheable un rato: el audio de un sonido no cambia nunca (para cambiarlo se sube otro).
        Response.Headers["Cache-Control"] = "private, max-age=86400";
        return File(s.Datos, string.IsNullOrEmpty(s.Mime) ? "audio/mpeg" : s.Mime);
    }

    /// <summary>Sube un sonido nuevo. Solo audio, y cortito (tope 300 KB).</summary>
    [HttpPost("sonidos")]
    [Authorize]
    public async Task<IActionResult> SubirSonido([FromForm] IFormFile? file, [FromForm] string? nombre)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No llegó ningún archivo" });
        if (file.Length > SonidoMaxBytes)
            return BadRequest(new { error = "El sonido es muy pesado. Tiene que ser de menos de 300 KB — un aviso de uno o dos segundos entra de sobra." });

        var mime = (file.ContentType ?? "").ToLowerInvariant();
        if (!mime.StartsWith("audio/", StringComparison.Ordinal))
            return BadRequest(new { error = "Eso no es un archivo de audio. Tiene que ser un mp3, wav, ogg o m4a." });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var datos = ms.ToArray();
        if (datos.Length == 0) return BadRequest(new { error = "El archivo llegó vacío" });

        var titulo = (nombre ?? "").Trim();
        if (string.IsNullOrEmpty(titulo))
            titulo = Path.GetFileNameWithoutExtension(file.FileName ?? "Sonido");
        if (titulo.Length > 80) titulo = titulo.Substring(0, 80);
        if (string.IsNullOrWhiteSpace(titulo)) titulo = "Sonido";

        var son = new WhatsAppSonido
        {
            Nombre = titulo,
            Mime = mime,
            Datos = datos,
            CreadoPor = User?.Identity?.Name,
            CreatedAt = DateTime.UtcNow
        };
        _db.WhatsAppSonidos.Add(son);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, id = son.Id, nombre = son.Nombre, clave = "subido:" + son.Id });
    }

    /// <summary>Borra un sonido subido. Las líneas que lo estaban usando vuelven a la campanita.</summary>
    [HttpDelete("sonidos/{id:int}")]
    [Authorize]
    public async Task<IActionResult> BorrarSonido(int id)
    {
        var s = await _db.WhatsAppSonidos.FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return NotFound(new { error = "Ese sonido ya no está" });

        // Si alguna línea lo tenía elegido, la dejamos sin sonido (usa la campanita) en vez de
        // que se quede apuntando a algo que ya no existe y no suene nada.
        var clave = "subido:" + id;
        var usandolo = await _db.WhatsAppLineasConfig.Where(c => c.Sonido == clave).ToListAsync();
        foreach (var c in usandolo) { c.Sonido = null; c.UpdatedAt = DateTime.UtcNow; }

        _db.WhatsAppSonidos.Remove(s);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, lineasAfectadas = usandolo.Count });
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

        // 2026-08-06: canonicalizamos el número ANTES de guardarlo y de mandarlo. Antes usábamos
        // NormalizeTo (solo dígitos) y guardábamos crudo, así un "9 11 2265-2222" (sin el 54) quedaba
        // como "+91122652222", pero la respuesta del cliente llega de Meta como "+5491122652222" → se
        // abrían DOS chats. ToInboxWhatsApp le pega el 54 correcto para que caigan en el mismo chat.
        var numeroStd = MetaWhatsAppService.ToInboxWhatsApp(req.Numero);
        var digits = MetaWhatsAppService.NormalizeTo(numeroStd);
        if (digits.Length < 10)
            return BadRequest(new { error = "El número no parece válido. Poné el número completo con código de país (ej: 5491122525458)." });

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
                // 2026-08-18: si el ultimo mensaje era una foto/audio/archivo, el Cuerpo viene vacio y
                // el renglon de la lista salia EN BLANCO. Con esto el celu puede poner "📷 Foto".
                UltimoMediaUrl = g.OrderByDescending(m => m.CreatedAt).Select(m => m.MediaUrl).FirstOrDefault(),
                UltimoDireccion = g.OrderByDescending(m => m.CreatedAt).Select(m => m.Direccion).FirstOrDefault(),
                UltimoAt = g.Max(m => m.CreatedAt),
                Total = g.Count(),
                // 2026-08-01: la línea es ahora parte de la clave del grupo (número+línea)
                Linea = g.Key.LineaPhoneId,
                // 2026-07-31: canal del último mensaje (TWILIO/CLOUD/INSTAGRAM) para el iconito en el chat
                Canal = g.OrderByDescending(m => m.CreatedAt).Select(m => m.Canal).FirstOrDefault(),
                // 2026-08-09: fecha del último mensaje ENTRANTE (del cliente). Sirve para "desarchivar solo":
                // si el cliente escribió después de que archivamos, la charla vuelve al listado sola.
                UltimoInboundAt = g.Where(m => m.Direccion == "INCOMING").Max(m => (DateTime?)m.CreatedAt)
            })
            .ToListAsync();
        // Nombre visible de cada línea (lo auto-registra el webhook en AppSettings)
        var lineasNombres = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith("whatsapp.linea."))
            .ToDictionaryAsync(s => s.Key.Substring("whatsapp.linea.".Length), s => s.Value);
        // Join in-memory con contactos (poco volumen, mas simple que LINQ join)
        var contactos = await _db.WhatsAppTwilioContactos.AsNoTracking()
            .Where(c => c.Activo).ToDictionaryAsync(c => c.Numero, c => c);
        // 2026-08-20: un mismo telefono puede tener VARIAS razones sociales colgadas. Traemos la
        // lista completa por numero (la columna ClienteId del contacto sigue siendo "el principal").
        var vinculos = await _db.WhatsAppContactoClientes.AsNoTracking()
            .OrderBy(v => v.Orden).ThenBy(v => v.Id).ToListAsync();
        var vincMap = vinculos.GroupBy(v => v.Numero)
            .ToDictionary(g => g.Key, g => g.Select(v => v.ClienteId).ToList());
        // Que tildo ESTE operador en cada chat (el tilde es de cada uno, no compartido).
        var quienElige = await QuienEligeAsync();
        var elegidos = await _db.WhatsAppClientesElegidos.AsNoTracking()
            .Where(e => e.Quien == quienElige)
            .ToDictionaryAsync(e => e.Numero, e => e.ClienteId);
        var clienteIds = contactos.Values.Where(c => c.ClienteId.HasValue).Select(c => c.ClienteId!.Value)
            .Concat(vinculos.Select(v => v.ClienteId))
            .Distinct().ToList();
        var clientes = await _db.CafeClientes.AsNoTracking()
            .Where(x => clienteIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Nombre, x.CodigoInterno })
            .ToDictionaryAsync(x => x.Id);
        // 2026-08-04: estado + responsable por conversación (número+línea). Solo hay fila si alguien
        // le puso estado o la pasó a otra persona; si no, se muestra "nueva" y sin asignar.
        var estados = await _db.WhatsAppConversaciones.AsNoTracking().ToListAsync();
        var estadoMap = estados
            .GroupBy(e => (e.Numero, e.LineaPhoneId ?? ""))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.Id).First());
        var result = conv.Select(x =>
        {
            contactos.TryGetValue(x.Numero, out var c);
            // 2026-08-20: lista de razones sociales de este numero + cual esta tildada ahora.
            var listaCli = ClientesDelNumero(x.Numero, c?.ClienteId, vincMap);
            var clienteEfectivo = ClienteEfectivo(x.Numero, c?.ClienteId, listaCli, elegidos);
            string? clienteNombre = null;
            if (clienteEfectivo != null && clientes.TryGetValue(clienteEfectivo.Value, out var cli)) clienteNombre = cli.Nombre;
            estadoMap.TryGetValue((x.Numero, x.Linea ?? ""), out var est);
            return new
            {
                x.Numero,
                NombrePerfil = c?.Nombre ?? x.NombrePerfil,
                Rol = c?.Rol,
                // 2026-08-20: ClienteId ahora es el EFECTIVO (el que tildo este operador). Si no
                // tildo nada, es el principal de siempre — asi las pantallas viejas no cambian.
                ClienteId = clienteEfectivo,
                ClienteNombre = clienteNombre,
                // Todas las razones sociales de este telefono. Con una sola, la pantalla se ve igual
                // que antes; con dos o mas, muestra los botoncitos para tildar.
                Clientes = listaCli.Select(id => new
                {
                    Id = id,
                    Nombre = clientes.TryGetValue(id, out var cx) ? cx.Nombre : $"Cliente #{id}",
                    Codigo = clientes.TryGetValue(id, out var cy) && cy.CodigoInterno.HasValue
                        ? cy.CodigoInterno.Value.ToString() : null
                }).ToList(),
                x.UltimoMensaje,
                x.UltimoMediaUrl,
                x.UltimoDireccion,
                x.UltimoAt,
                x.Total,
                x.Linea,
                LineaNumero = x.Linea != null && lineasNombres.TryGetValue(x.Linea, out var ln) ? ln : null,
                x.Canal,
                Estado = est?.Estado ?? "nueva",
                AsignadoOperador = est?.AsignadoOperador,
                AsignadoPor = est?.AsignadoPor,
                AsignadoNota = est?.AsignadoNota,
                // Si no hay asignación pendiente, "visto"=true (no dispara el aviso). Solo es false
                // cuando alguien la pasó a otra persona y esa persona todavía no la abrió.
                AsignadoVisto = est == null || est.AsignadoOperador == null || est.AsignadoVisto,
                // 2026-08-09: archivada = la archivaron y el cliente NO volvió a escribir desde entonces.
                // Si entra un mensaje entrante más nuevo (UltimoInboundAt > ArchivadoAt), se desarchiva sola.
                Archivado = est != null && est.ArchivadoAt != null
                            && (x.UltimoInboundAt == null || x.UltimoInboundAt <= est.ArchivadoAt),
                // 2026-08-19: chat FIJADO (chinche 📌). Va arriba de todo en la lista. Compartido.
                Fijado = est?.FijadoAt != null,
                FijadoAt = est?.FijadoAt,
                // 2026-08-19: sonido propio de esta charla (le gana al de la línea). Null = el de la línea.
                Sonido = est?.Sonido,
                // 2026-08-20: cuándo escribió el cliente por última vez. El servidor YA lo calculaba
                // (lo usa para desarchivar solo), pero no lo mandaba. Lo necesita la pantalla para
                // decir si a este contacto se le puede escribir libre o si ya cerró la ventana de
                // 24 hs de Meta — sin eso, al reenviar habría que adivinar.
                UltimoEntranteAt = x.UltimoInboundAt
            };
        })
        // 2026-08-19: primero los FIJADOS (el último que fijaste, más arriba) y después el resto
        // por hora del último mensaje, como siempre. El orden lo manda el servidor así vale igual
        // para la pantalla grande y para el celular.
        .OrderByDescending(x => x.Fijado)
        .ThenByDescending(x => x.FijadoAt ?? DateTime.MinValue)
        .ThenByDescending(x => x.UltimoAt).ToList();
        return Ok(result);
    }

    // ===== 2026-08-04: pasar/asignar conversación + estado (en curso / finalizada / etc) =====
    public record AsignarConvRequest(string Numero, string? LineaPhoneId, string? Operador, string? Nota);
    public record EstadoConvRequest(string Numero, string? LineaPhoneId, string Estado);
    public record VistoConvRequest(string Numero, string? LineaPhoneId);

    private static readonly string[] EstadosValidos = { "nueva", "en_curso", "esperando", "finalizada" };
    private static string? NormOp(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToUpperInvariant();

    /// <summary>Busca (o crea en memoria) la fila de estado de una conversación por número+línea.</summary>
    private async Task<WhatsAppConversacion> GetOrCreateConvAsync(string numero, string? linea)
    {
        var lin = string.IsNullOrEmpty(linea) ? null : linea;
        var row = lin == null
            ? await _db.WhatsAppConversaciones.FirstOrDefaultAsync(cc => cc.Numero == numero && cc.LineaPhoneId == null)
            : await _db.WhatsAppConversaciones.FirstOrDefaultAsync(cc => cc.Numero == numero && cc.LineaPhoneId == lin);
        if (row == null)
        {
            row = new WhatsAppConversacion { Numero = numero, LineaPhoneId = lin };
            _db.WhatsAppConversaciones.Add(row);
        }
        return row;
    }

    /// <summary>POST conversaciones/asignar — pasar la charla a un operador (OSMAR/GERMAN/GABRIEL/...).
    /// Operador null = sacar la asignación. El que la recibe la ve como pendiente (aviso) hasta abrirla.</summary>
    [HttpPost("conversaciones/asignar")]
    [Authorize]
    public async Task<IActionResult> AsignarConversacion([FromBody] AsignarConvRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Numero)) return BadRequest(new { error = "Falta el número" });
        var quien = NormOp(Request.Headers["X-Operator-Name"].FirstOrDefault());
        var target = NormOp(req.Operador);
        var row = await GetOrCreateConvAsync(req.Numero, req.LineaPhoneId);
        row.AsignadoOperador = target;
        row.AsignadoPor = target == null ? null : quien;
        row.AsignadoNota = string.IsNullOrWhiteSpace(req.Nota) ? null : req.Nota.Trim();
        row.AsignadoAt = target == null ? null : DateTime.UtcNow;
        // Si me la asigno a mí mismo (o la desasigno), no hay aviso pendiente.
        row.AsignadoVisto = target == null || target == quien;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>POST conversaciones/estado — marcar la charla como en curso / esperando / finalizada / nueva.</summary>
    [HttpPost("conversaciones/estado")]
    [Authorize]
    public async Task<IActionResult> CambiarEstadoConversacion([FromBody] EstadoConvRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Numero)) return BadRequest(new { error = "Falta el número" });
        var est = (req.Estado ?? "").Trim().ToLowerInvariant();
        if (!EstadosValidos.Contains(est)) return BadRequest(new { error = "Estado inválido" });
        var row = await GetOrCreateConvAsync(req.Numero, req.LineaPhoneId);
        row.Estado = est;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // 2026-08-06: guardar/editar la NOTA (comentario) de la conversación sin tocar el responsable
    // ni el estado. Antes la nota solo se guardaba cuando pasabas la charla a alguien, y si no la
    // pasabas se perdía. Ahora se puede escribir, guardar y editar sola.
    public record NotaConvRequest(string Numero, string? LineaPhoneId, string? Nota);
    [HttpPost("conversaciones/nota")]
    [Authorize]
    public async Task<IActionResult> GuardarNotaConversacion([FromBody] NotaConvRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Numero)) return BadRequest(new { error = "Falta el número" });
        var row = await GetOrCreateConvAsync(req.Numero, req.LineaPhoneId);
        row.AsignadoNota = string.IsNullOrWhiteSpace(req.Nota) ? null : req.Nota.Trim();
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>POST conversaciones/visto — el que la recibió la abrió: apaga el aviso "te la pasó X".</summary>
    [HttpPost("conversaciones/visto")]
    [Authorize]
    public async Task<IActionResult> MarcarConvVisto([FromBody] VistoConvRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Numero)) return BadRequest(new { error = "Falta el número" });
        var lin = string.IsNullOrEmpty(req.LineaPhoneId) ? null : req.LineaPhoneId;
        var numero = req.Numero;
        var row = lin == null
            ? await _db.WhatsAppConversaciones.FirstOrDefaultAsync(cc => cc.Numero == numero && cc.LineaPhoneId == null)
            : await _db.WhatsAppConversaciones.FirstOrDefaultAsync(cc => cc.Numero == numero && cc.LineaPhoneId == lin);
        if (row != null && !row.AsignadoVisto)
        {
            row.AsignadoVisto = true;
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        return Ok(new { ok = true });
    }

    // 2026-08-09: archivar/desarchivar una charla. Archivar = sacarla del listado general hasta que el
    // cliente vuelva a escribir (se desarchiva sola) o la busquen. Compartido para todo el equipo.
    public record ArchivarConvRequest(string Numero, string? LineaPhoneId, bool Archivar);
    [HttpPost("conversaciones/archivar")]
    [Authorize]
    public async Task<IActionResult> ArchivarConversacion([FromBody] ArchivarConvRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Numero)) return BadRequest(new { error = "Falta el número" });
        var row = await GetOrCreateConvAsync(req.Numero, req.LineaPhoneId);
        row.ArchivadoAt = req.Archivar ? DateTime.UtcNow : null;
        // 2026-08-19: si la archivan, se le saca el chinche 📌 (no tiene sentido que una charla
        // archivada siga arriba de todo; además liberamos uno de los 5 lugares).
        if (req.Archivar) row.FijadoAt = null;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // 2026-08-19: FIJAR / SACAR DE FIJADOS una charla (chinche 📌, como WhatsApp). Los fijados
    // aparecen arriba de todo en la lista. Máximo 5 a la vez — el tope lo controla el servidor
    // para que no se pase aunque lo fijen desde dos pantallas al mismo tiempo.
    public const int MaxFijados = 5;
    public record FijarConvRequest(string Numero, string? LineaPhoneId, bool Fijar);
    [HttpPost("conversaciones/fijar")]
    [Authorize]
    public async Task<IActionResult> FijarConversacion([FromBody] FijarConvRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Numero)) return BadRequest(new { error = "Falta el número" });
        var row = await GetOrCreateConvAsync(req.Numero, req.LineaPhoneId);
        if (req.Fijar)
        {
            if (row.FijadoAt == null)
            {
                var yaFijados = await _db.WhatsAppConversaciones.CountAsync(c => c.FijadoAt != null);
                if (yaFijados >= MaxFijados)
                    return BadRequest(new { error = $"Ya tenés {MaxFijados} chats fijados. Sacá uno para poder fijar este.", limite = true });
            }
            row.FijadoAt ??= DateTime.UtcNow;
            // Al fijar, la charla vuelve al listado (no puede estar archivada y fijada a la vez).
            row.ArchivadoAt = null;
        }
        else row.FijadoAt = null;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, fijado = row.FijadoAt != null });
    }

    // 2026-08-19: SONIDO PROPIO de una charla. Le gana al de la línea, para que un cliente
    // importante suene distinto al resto. Sonido vacío/null = vuelve a usar el de la línea.
    public record SonidoConvRequest(string Numero, string? LineaPhoneId, string? Sonido);
    [HttpPost("conversaciones/sonido")]
    [Authorize]
    public async Task<IActionResult> SonidoConversacion([FromBody] SonidoConvRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Numero)) return BadRequest(new { error = "Falta el número" });
        var son = (req.Sonido ?? "").Trim();
        if (son.Length > 30) return BadRequest(new { error = "Ese sonido no existe" });
        // Si apunta a un sonido subido, chequeamos que siga existiendo (lo pudo borrar otro).
        if (son.StartsWith("subido:", StringComparison.Ordinal))
        {
            var id = int.TryParse(son.Substring(7), out var n) ? n : 0;
            if (id <= 0 || !await _db.WhatsAppSonidos.AnyAsync(x => x.Id == id))
                return BadRequest(new { error = "Ese sonido ya no está. Actualizá la pantalla y elegí otro." });
        }
        var row = await GetOrCreateConvAsync(req.Numero, req.LineaPhoneId);
        row.Sonido = string.IsNullOrEmpty(son) ? null : son;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, sonido = row.Sonido });
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

    /// <summary>GET /api/whatsapp/twilio/clientes-buscar?q=texto — busqueda para autocomplete.
    /// 2026-08-03: busca por MUCHOS campos (nombre, razón social, CUIT/DNI, teléfonos, email,
    /// dirección, entre calles, localidad, ciudad, código y notas). Para números (CUIT/DNI/tel)
    /// también compara ignorando guiones/espacios/+, así "20123456789" encuentra "20-12345678-9".
    /// 2026-08-25: BUSCAR POR DIRECCIÓN DE VERDAD. Antes solo miraba la dirección principal de la
    /// ficha: escribir "olavarria 2621" no encontraba nada si esa calle era una de las direcciones
    /// de ENTREGA (que viven en una tabla aparte) o el domicilio de entrega viejo. Ahora además:
    ///   · mira TODAS las direcciones de entrega del cliente (Cafe_ClienteDirecciones) y DomicilioEntrega;
    ///   · compara sin tildes ni mayúsculas (collate _CI_AI), así "olavarria" encuentra "OLAVARRÍA";
    ///   · devuelve DireccionEntrega: la dirección de entrega que hizo match, para mostrarla en el
    ///     resultado (si no, el operador ve otra dirección y no entiende por qué apareció).</summary>
    [HttpGet("clientes-buscar")]
    [Authorize]
    public async Task<IActionResult> BuscarClientes([FromQuery] string q = "", [FromQuery] int top = 15)
    {
        q = (q ?? "").Trim();
        var query = _db.CafeClientes.AsNoTracking();
        // Direcciones de entrega que coinciden con lo tipeado: clienteId -> texto de la dirección.
        var dirPorCliente = new Dictionary<int, string>();
        var idsDir = new List<int>();
        if (!string.IsNullOrWhiteSpace(q))
        {
            // Si escribió 2+ palabras (ej. "carlos quintana"), buscamos por palabra suelta:
            // cada palabra tiene que aparecer en algún campo de texto, sin importar el orden.
            // "carlos quintana" y "quintana carlos" encuentran a "QUINTANA CARLOS ADRIAN".
            var palabras = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            // (1) Primero, la lista de direcciones de ENTREGA (un cliente puede tener varias).
            var dq = _db.CafeClienteDirecciones.AsNoTracking().Where(d => d.IsActive);
            foreach (var palabra in palabras)
            {
                var patDir = "%" + CafePreventasController.EscaparLike(palabra) + "%";
                dq = dq.Where(d =>
                       EF.Functions.Like(EF.Functions.Collate(d.Direccion, COLLATE_SIN_TILDES), patDir)
                    || (d.Etiqueta != null && EF.Functions.Like(EF.Functions.Collate(d.Etiqueta, COLLATE_SIN_TILDES), patDir))
                    || (d.EntreCalles != null && EF.Functions.Like(EF.Functions.Collate(d.EntreCalles, COLLATE_SIN_TILDES), patDir))
                    || (d.Localidad != null && EF.Functions.Like(EF.Functions.Collate(d.Localidad, COLLATE_SIN_TILDES), patDir))
                    || (d.Ciudad != null && EF.Functions.Like(EF.Functions.Collate(d.Ciudad, COLLATE_SIN_TILDES), patDir))
                    || (d.Telefono != null && d.Telefono.Contains(palabra)));
            }
            var dirHits = await dq
                .Select(d => new { d.ClienteId, d.Etiqueta, d.Direccion, d.Localidad })
                .Take(200)
                .ToListAsync();
            foreach (var d in dirHits)
            {
                var txt = ((d.Etiqueta != null ? d.Etiqueta + ": " : "") + d.Direccion + " " + (d.Localidad ?? "")).Trim();
                if (!dirPorCliente.ContainsKey(d.ClienteId)) dirPorCliente[d.ClienteId] = txt;
            }
            idsDir = dirPorCliente.Keys.ToList();

            // (2) Después, la ficha del cliente. Los que ya matchearon por dirección de entrega
            //     entran igual (idsDir), sin importar qué diga el resto de la ficha.
            if (palabras.Length >= 2)
            {
                foreach (var palabra in palabras)
                {
                    var t = palabra;                      // captura segura por iteración
                    var pat = "%" + CafePreventasController.EscaparLike(t) + "%";
                    var tDigits = new string(t.Where(char.IsDigit).ToArray());   // "15-5667788" -> "155667788"
                    int.TryParse(t, out var tNum);
                    query = query.Where(c =>              // cada .Where suma un AND: todas las palabras deben estar
                           idsDir.Contains(c.Id)
                        || EF.Functions.Like(EF.Functions.Collate(c.Nombre, COLLATE_SIN_TILDES), pat)
                        || (c.RazonSocial != null && EF.Functions.Like(EF.Functions.Collate(c.RazonSocial, COLLATE_SIN_TILDES), pat))
                        || (c.Direccion != null && EF.Functions.Like(EF.Functions.Collate(c.Direccion, COLLATE_SIN_TILDES), pat))
                        || (c.DomicilioEntrega != null && EF.Functions.Like(EF.Functions.Collate(c.DomicilioEntrega, COLLATE_SIN_TILDES), pat))
                        || (c.EntreCalles != null && EF.Functions.Like(EF.Functions.Collate(c.EntreCalles, COLLATE_SIN_TILDES), pat))
                        || (c.Localidad != null && EF.Functions.Like(EF.Functions.Collate(c.Localidad, COLLATE_SIN_TILDES), pat))
                        || (c.Ciudad != null && EF.Functions.Like(EF.Functions.Collate(c.Ciudad, COLLATE_SIN_TILDES), pat))
                        || (c.Email != null && c.Email.Contains(t))
                        || (c.Notas != null && EF.Functions.Like(EF.Functions.Collate(c.Notas, COLLATE_SIN_TILDES), pat))
                        // 2026-08-25: tambien telefono y CUIT/DNI aca (antes solo los miraba cuando se
                        // escribia UNA sola palabra, asi que "juan 1155667788" no encontraba nada).
                        || (c.Cuit != null && (c.Cuit.Contains(t) || (tDigits.Length >= 3 && c.Cuit.Replace("-", "").Replace(".", "").Replace(" ", "").Contains(tDigits))))
                        || (c.Telefono != null && (c.Telefono.Contains(t) || (tDigits.Length >= 3 && c.Telefono.Replace("-", "").Replace(" ", "").Replace("+", "").Contains(tDigits))))
                        || (c.Telefono2 != null && (c.Telefono2.Contains(t) || (tDigits.Length >= 3 && c.Telefono2.Replace("-", "").Replace(" ", "").Replace("+", "").Contains(tDigits))))
                        || (tNum > 0 && c.CodigoInterno == tNum));
                }
            }
            else
            {
                // Una sola palabra o un número: comportamiento de siempre (teléfono / CUIT / código, etc.)
                int.TryParse(q, out var qNum);
                var qDigits = new string(q.Where(char.IsDigit).ToArray());
                bool hasDigits = qDigits.Length >= 3;
                var patQ = "%" + CafePreventasController.EscaparLike(q) + "%";
                query = query.Where(c =>
                       idsDir.Contains(c.Id)
                    || EF.Functions.Like(EF.Functions.Collate(c.Nombre, COLLATE_SIN_TILDES), patQ)
                    || (c.RazonSocial != null && EF.Functions.Like(EF.Functions.Collate(c.RazonSocial, COLLATE_SIN_TILDES), patQ))
                    || (qNum > 0 && c.CodigoInterno == qNum)
                    || (c.Cuit != null && (c.Cuit.Contains(q) || (hasDigits && c.Cuit.Replace("-", "").Replace(".", "").Replace(" ", "").Contains(qDigits))))
                    || (c.Telefono != null && (c.Telefono.Contains(q) || (hasDigits && c.Telefono.Replace("-", "").Replace(" ", "").Replace("+", "").Contains(qDigits))))
                    || (c.Telefono2 != null && (c.Telefono2.Contains(q) || (hasDigits && c.Telefono2.Replace("-", "").Replace(" ", "").Replace("+", "").Contains(qDigits))))
                    || (c.Email != null && c.Email.Contains(q))
                    || (c.Direccion != null && EF.Functions.Like(EF.Functions.Collate(c.Direccion, COLLATE_SIN_TILDES), patQ))
                    || (c.DomicilioEntrega != null && EF.Functions.Like(EF.Functions.Collate(c.DomicilioEntrega, COLLATE_SIN_TILDES), patQ))
                    || (c.EntreCalles != null && EF.Functions.Like(EF.Functions.Collate(c.EntreCalles, COLLATE_SIN_TILDES), patQ))
                    || (c.Localidad != null && EF.Functions.Like(EF.Functions.Collate(c.Localidad, COLLATE_SIN_TILDES), patQ))
                    || (c.Ciudad != null && EF.Functions.Like(EF.Functions.Collate(c.Ciudad, COLLATE_SIN_TILDES), patQ))
                    || (c.Notas != null && EF.Functions.Like(EF.Functions.Collate(c.Notas, COLLATE_SIN_TILDES), patQ)));   // por si el DNI u otro dato quedó acá
            }
        }
        // 2026-08-21: ORDEN POR RELEVANCIA (antes era alfabetico puro). Con 9.000 clientes, cortar
        // alfabeticamente escondia al cliente buscado: tipear "sergio" traia las primeras 15
        // coincidencias por nombre y "SERGIO FERNANDEZ" quedaba afuera. Ahora primero salen los que
        // EMPIEZAN con lo tipeado, despues los que lo tienen al principio de alguna palabra.
        // OJO: el orden NO puede usar StartsWith(q) con la misma variable `q` del Contains del Where:
        // EF junta los dos LIKE en un solo parametro y el filtro pasa a ser "empieza con" (devuelve
        // casi nada). Por eso se arma con EF.Functions.Like y patrones propios.
        var prim = (q.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? q);
        var patronFrase = CafePreventasController.EscaparLike(q) + "%";
        var patronPrim = CafePreventasController.EscaparLike(prim) + "%";
        var patronPrimPalabra = "% " + CafePreventasController.EscaparLike(prim) + "%";
        var list = await query
            .OrderByDescending(c => EF.Functions.Like(c.Nombre, patronFrase))
            .ThenByDescending(c => EF.Functions.Like(c.Nombre, patronPrim))
            .ThenByDescending(c => EF.Functions.Like(c.Nombre, patronPrimPalabra))
            .ThenBy(c => c.Nombre)
            .Take(Math.Clamp(top, 1, 50))
            .Select(c => new { c.Id, c.Nombre, CodigoInterno = c.CodigoInterno.HasValue ? c.CodigoInterno.ToString() : null, c.Telefono, c.Direccion, c.Localidad })
            .ToListAsync();
        // La dirección de entrega que matcheó viaja aparte para poder mostrarla debajo del nombre.
        var result = list.Select(c => new
        {
            c.Id, c.Nombre, c.CodigoInterno, c.Telefono, c.Direccion, c.Localidad,
            DireccionEntrega = dirPorCliente.TryGetValue(c.Id, out var dtxt) ? dtxt : null
        }).ToList();
        return Ok(result);
    }

    /// <summary>
    /// GET /api/whatsapp/twilio/destinatarios-buscar?q=texto
    /// Buscador UNIFICADO de contactos para "Nueva conversacion": junta clientes del cafe,
    /// contactos de conversaciones de WhatsApp (nombre de perfil), compradores de MercadoLibre
    /// y la agenda de contactos de WhatsApp. Saca repetidos por numero.
    /// </summary>
    [HttpGet("destinatarios-buscar")]
    [Authorize]
    public async Task<IActionResult> BuscarDestinatarios([FromQuery] string q = "", [FromQuery] int top = 20, [FromQuery] string? linea = null)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2) return Ok(new List<object>());
        int cap = Math.Clamp(top, 1, 50);
        var acc = new List<(string Nombre, string? Tel, string Origen)>();

        // 1) Clientes del cafe
        int.TryParse(q, out var qNum);
        var patronDestEmpieza = CafePreventasController.EscaparLike(q) + "%";
        var patronDestPalabra = "% " + CafePreventasController.EscaparLike(q) + "%";
        acc.AddRange((await _db.CafeClientes.AsNoTracking()
            .Where(c => (c.Nombre.Contains(q) || (qNum > 0 && c.CodigoInterno == qNum) || (c.Telefono != null && c.Telefono.Contains(q)))
                        && c.Telefono != null && c.Telefono != "")
            // 2026-08-21: por relevancia, no alfabetico: si no, cortar en `cap` esconde al cliente
            // buscado cuando hay muchos homonimos (ver clientes-buscar).
            .OrderByDescending(c => EF.Functions.Like(c.Nombre, patronDestEmpieza))
            .ThenByDescending(c => EF.Functions.Like(c.Nombre, patronDestPalabra))
            .ThenBy(c => c.Nombre).Take(cap)
            .Select(c => new { c.Nombre, c.Telefono }).ToListAsync())
            .Select(c => (c.Nombre, (string?)c.Telefono, "Cliente")));

        // 2) Contactos de conversaciones de WhatsApp (por nombre de perfil o numero)
        acc.AddRange((await _db.WhatsAppTwilioMensajes.AsNoTracking()
            .Where(m => m.NombrePerfil != null && m.NombrePerfil != ""
                        && (m.NombrePerfil.Contains(q) || m.Numero.Contains(q)))
            .GroupBy(m => m.Numero)
            .Select(g => new { Numero = g.Key, Nombre = g.Max(x => x.NombrePerfil) })
            .Take(cap).ToListAsync())
            .Select(m => (m.Nombre ?? "", (string?)m.Numero, "WhatsApp")));

        // 3) Compradores de MercadoLibre (base "Telefonos")
        acc.AddRange((await _db.MeliClientes.AsNoTracking()
            .Where(c => c.Phone != null && c.Phone != ""
                        && ((c.ReceiverName != null && c.ReceiverName.Contains(q))
                            || (c.Nickname != null && c.Nickname.Contains(q))
                            || c.Phone.Contains(q)))
            .OrderByDescending(c => c.LastPurchaseAt).Take(cap)
            .Select(c => new { Nombre = c.ReceiverName ?? c.Nickname, c.Phone }).ToListAsync())
            .Select(c => (c.Nombre ?? "", (string?)c.Phone, "MercadoLibre")));

        // 4) Agenda de contactos de WhatsApp
        acc.AddRange((await _db.WhatsAppTwilioContactos.AsNoTracking()
            .Where(c => c.Nombre.Contains(q) || c.Numero.Contains(q))
            .Take(cap)
            .Select(c => new { c.Nombre, c.Numero }).ToListAsync())
            .Select(c => (c.Nombre, (string?)c.Numero, "Agenda")));

        // Normalizar + sacar repetidos por numero
        var vistos = new HashSet<string>();
        var items = new List<(string Nombre, string Numero, string Origen)>();
        foreach (var (Nombre, Tel, Origen) in acc)
        {
            var num = NormalizarNumeroWa(Tel);
            if (num.Length < 8) continue;                 // sin numero usable
            if (!vistos.Add(num)) continue;               // ya lo tenemos
            items.Add((string.IsNullOrWhiteSpace(Nombre) ? num : Nombre, num, Origen));
            if (items.Count >= cap) break;
        }

        // 2026-08-04: ¿le puedo escribir LIBRE? WhatsApp solo deja si el contacto nos escribió
        // en las ultimas 24hs. Marcamos "Disponible" a los que tienen un ENTRANTE reciente.
        // 2026-08-20: la ventana de 24 hs es POR LÍNEA, no por contacto. Si el que llama dice por
        // qué línea va a escribir (el reenvío lo sabe), se mira SOLO esa: antes un contacto que te
        // escribió por FRIKAF salía "🟢 le podés escribir" aunque le fueras a mandar por TRANSRADIO,
        // donde Meta lo iba a rechazar. Sin línea se mantiene el comportamiento de siempre.
        var nums = items.Select(i => i.Numero).ToList();
        var limite = DateTime.UtcNow.AddHours(-24);
        var lin = string.IsNullOrWhiteSpace(linea) ? null : linea.Trim();
        var disponibles = (await _db.WhatsAppTwilioMensajes.AsNoTracking()
            .Where(m => m.Direccion == "INCOMING" && m.CreatedAt >= limite && nums.Contains(m.Numero)
                        && (lin == null || m.LineaPhoneId == lin))
            .Select(m => m.Numero).Distinct().ToListAsync())
            .ToHashSet();

        var salida = items
            .Select(i => new { i.Nombre, i.Numero, i.Origen, Disponible = disponibles.Contains(i.Numero) })
            .ToList<object>();
        return Ok(salida);
    }

    /// <summary>Deja un telefono en formato WhatsApp (solo digitos, con codigo de pais).
    /// Por defecto asume ARGENTINA y completa 549. PERO si el numero ya vino con codigo de pais
    /// explicito ("+34…" de España, "0034…", etc.) lo RESPETA y NO le pega el 549 adelante.
    /// 2026-08-04: antes le metia 549 a cualquier numero → rompia los del exterior.</summary>
    private static string NormalizarNumeroWa(string? tel)
    {
        if (string.IsNullOrWhiteSpace(tel)) return "";
        var raw = tel.Trim();
        // ¿Trae codigo de pais explicito? El "+" o el prefijo internacional "00".
        bool traeCodigoPais = raw.StartsWith("+") || raw.StartsWith("00");
        var d = new string(raw.Where(char.IsDigit).ToArray());
        if (string.IsNullOrEmpty(d)) return "";
        if (d.StartsWith("00")) d = d.Substring(2);
        if (traeCodigoPais)
        {
            // Ya sabemos el pais. Si es argentino (+54) igual dejamos el 9 que WhatsApp exige.
            if (d.StartsWith("54") && !d.StartsWith("549")) return "549" + d.Substring(2);
            return d;   // ej España "34642265173" → tal cual, SIN 549
        }
        // Sin "+": asumimos que es un numero argentino local (ej "11 2252-5458").
        d = d.TrimStart('0');
        if (d.StartsWith("15")) d = d.Substring(2);
        if (d.StartsWith("549")) return d;
        if (d.StartsWith("54")) return "549" + d.Substring(2);
        return "549" + d;
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

    // 2026-08-05: poner la CATEGORÍA (rol) de un contacto directo desde la lista, sin abrir el chat.
    // Crea el contacto si no existía (con el nombre de perfil), o solo le cambia el rol si ya estaba.
    public record ContactoRolRequest(string Numero, string Rol, string? Nombre = null);

    [HttpPost("contacto-rol")]
    [Authorize]
    public async Task<IActionResult> SetContactoRol([FromBody] ContactoRolRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Numero) || string.IsNullOrWhiteSpace(req.Rol))
            return BadRequest(new { error = "Falta el número o la categoría" });
        // El número llega como lo guarda el sistema ("whatsapp:+549…" o "ig:…"). Se respeta tal cual
        // para que enganche con la conversación (el join es por Numero exacto).
        var numero = req.Numero.Trim();
        if (!numero.StartsWith("whatsapp:") && !numero.StartsWith("ig:")) numero = "whatsapp:" + numero;
        var rol = req.Rol.Trim();
        var c = await _db.WhatsAppTwilioContactos.FirstOrDefaultAsync(x => x.Numero == numero);
        if (c == null)
        {
            c = new WhatsAppTwilioContacto
            {
                Numero = numero,
                Nombre = string.IsNullOrWhiteSpace(req.Nombre) ? numero.Replace("whatsapp:", "").Replace("ig:", "") : req.Nombre!.Trim(),
                Rol = rol,
                Activo = true
            };
            _db.WhatsAppTwilioContactos.Add(c);
        }
        else
        {
            c.Rol = rol;
            c.Activo = true;
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ===== Vincular un chat a un cliente del sistema EN UN PASO (asociar fácil) =====
    public record VincularClienteRequest(string Numero, int? ClienteId);

    /// <summary>2026-08-03: POST contactos/vincular-cliente — asocia (o desasocia) un número de
    /// WhatsApp a un cliente del sistema en UN solo paso, sin pedir nombre ni rol. Si el contacto
    /// no existía, lo crea con el nombre del cliente y rol "cliente". Si ClienteId viene null,
    /// desvincula (deja el contacto pero sin cliente).</summary>
    [HttpPost("contactos/vincular-cliente")]
    [Authorize]
    public async Task<IActionResult> VincularCliente([FromBody] VincularClienteRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Numero))
            return BadRequest(new { error = "Falta el número" });
        var numero = req!.Numero.Trim();
        if (!numero.StartsWith("whatsapp:")) numero = "whatsapp:" + numero;

        string? cliNombre = null; string? cliCodigo = null;
        if (req.ClienteId.HasValue)
        {
            var cli = await _db.CafeClientes.AsNoTracking()
                .Where(x => x.Id == req.ClienteId.Value)
                .Select(x => new { x.Nombre, x.CodigoInterno })
                .FirstOrDefaultAsync();
            if (cli == null) return BadRequest(new { error = "No encontré ese cliente" });
            cliNombre = cli.Nombre;
            cliCodigo = cli.CodigoInterno?.ToString();
        }

        var c = await _db.WhatsAppTwilioContactos.FirstOrDefaultAsync(x => x.Numero == numero);

        // 2026-08-20: vincular ya NO reemplaza — SUMA. Un mismo telefono puede tener varias
        // razones sociales (ver WhatsAppContactoCliente). Para sacar una, va desvincular-cliente.
        if (req.ClienteId == null)
        {
            // Sin cliente = desvincular TODO este numero (comportamiento de siempre del boton).
            if (c != null) c.ClienteId = null;
            var todos = await _db.WhatsAppContactoClientes.Where(v => v.Numero == numero).ToListAsync();
            if (todos.Count > 0) _db.WhatsAppContactoClientes.RemoveRange(todos);
            var elegs = await _db.WhatsAppClientesElegidos.Where(e => e.Numero == numero).ToListAsync();
            if (elegs.Count > 0) _db.WhatsAppClientesElegidos.RemoveRange(elegs);
            await _db.SaveChangesAsync();
            return Ok(new { ok = true, clienteId = (int?)null });
        }

        if (c == null)
        {
            c = new WhatsAppTwilioContacto
            {
                Numero = numero,
                Nombre = string.IsNullOrWhiteSpace(cliNombre) ? numero.Replace("whatsapp:", "") : cliNombre!,
                Rol = "cliente",
                Activo = true,
                ClienteId = req.ClienteId
            };
            _db.WhatsAppTwilioContactos.Add(c);
        }
        else
        {
            // El "principal" (columna vieja) queda en el PRIMERO que se vinculo. Solo se completa
            // si estaba vacio, para no cambiarle el cliente a las pantallas que leen esa columna.
            c.ClienteId ??= req.ClienteId;
            if (string.IsNullOrWhiteSpace(c.Nombre) && !string.IsNullOrWhiteSpace(cliNombre))
                c.Nombre = cliNombre!;
        }

        // Lo sumamos a la lista del numero (si ya estaba, no se duplica).
        var yaEsta = await _db.WhatsAppContactoClientes
            .AnyAsync(v => v.Numero == numero && v.ClienteId == req.ClienteId.Value);
        if (!yaEsta)
        {
            var maxOrden = await _db.WhatsAppContactoClientes
                .Where(v => v.Numero == numero).Select(v => (int?)v.Orden).MaxAsync() ?? -1;
            _db.WhatsAppContactoClientes.Add(new WhatsAppContactoCliente
            {
                Numero = numero, ClienteId = req.ClienteId.Value, Orden = maxOrden + 1
            });
        }
        // El que acabas de vincular queda TILDADO para vos (es con el que vas a trabajar ahora).
        await SetElegidoAsync(numero, req.ClienteId.Value);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, clienteId = req.ClienteId, clienteNombre = cliNombre, clienteCodigo = cliCodigo });
    }

    // ===== 2026-08-20: varias razones sociales por telefono =====

    /// <summary>Quien esta eligiendo: la firma del operador del PIN (OSMAR/GERMAN/GABRIEL) si la hay,
    /// si no el usuario logueado ("user:{id}"). El tilde es de cada uno, no compartido.</summary>
    private Task<string> QuienEligeAsync()
    {
        var op = NormOp(Request.Headers["X-Operator-Name"].FirstOrDefault());
        if (!string.IsNullOrWhiteSpace(op)) return Task.FromResult(op!);
        var idStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        return Task.FromResult(int.TryParse(idStr, out var uid) ? $"user:{uid}" : "user:0");
    }

    /// <summary>Todas las razones sociales de un numero, en orden. El "principal" del contacto va
    /// primero aunque todavia no este en la tabla nueva (por si la migracion no corrio).</summary>
    private static List<int> ClientesDelNumero(string numero, int? principal, Dictionary<string, List<int>> vincMap)
    {
        var lista = vincMap.TryGetValue(numero, out var l) ? new List<int>(l) : new List<int>();
        if (principal.HasValue && !lista.Contains(principal.Value)) lista.Insert(0, principal.Value);
        return lista;
    }

    /// <summary>Con cual esta trabajando este operador AHORA: el que tildo (si sigue vinculado),
    /// si no el principal del contacto, si no el primero de la lista.</summary>
    private static int? ClienteEfectivo(string numero, int? principal, List<int> lista, Dictionary<string, int> elegidos)
    {
        if (elegidos.TryGetValue(numero, out var eleg) && lista.Contains(eleg)) return eleg;
        if (principal.HasValue) return principal;
        return lista.Count > 0 ? lista[0] : (int?)null;
    }

    /// <summary>Deja tildado un cliente para el operador actual (no hace SaveChanges).</summary>
    private async Task SetElegidoAsync(string numero, int clienteId)
    {
        var quien = await QuienEligeAsync();
        var fila = await _db.WhatsAppClientesElegidos
            .FirstOrDefaultAsync(e => e.Numero == numero && e.Quien == quien);
        if (fila == null)
            _db.WhatsAppClientesElegidos.Add(new WhatsAppClienteElegido
            { Numero = numero, Quien = quien, ClienteId = clienteId, UpdatedAt = DateTime.UtcNow });
        else
        {
            fila.ClienteId = clienteId;
            fila.UpdatedAt = DateTime.UtcNow;
        }
    }

    public record ElegirClienteRequest(string Numero, int ClienteId);

    /// <summary>2026-08-20: POST contactos/elegir-cliente — tildar con cual de las razones sociales
    /// del telefono estas trabajando en este momento. Es TUYO: otro operador puede tener tildada
    /// otra en la misma charla y no se pisan.</summary>
    [HttpPost("contactos/elegir-cliente")]
    [Authorize]
    public async Task<IActionResult> ElegirCliente([FromBody] ElegirClienteRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Numero)) return BadRequest(new { error = "Falta el número" });
        var numero = req!.Numero.Trim();
        if (!numero.StartsWith("whatsapp:")) numero = "whatsapp:" + numero;

        var vinculado = await _db.WhatsAppContactoClientes
            .AnyAsync(v => v.Numero == numero && v.ClienteId == req.ClienteId);
        if (!vinculado)
        {
            // Puede ser el "principal" viejo, que todavia no paso a la tabla nueva.
            var esPrincipal = await _db.WhatsAppTwilioContactos
                .AnyAsync(c => c.Numero == numero && c.ClienteId == req.ClienteId);
            if (!esPrincipal) return BadRequest(new { error = "Ese cliente no está vinculado a este chat" });
        }
        await SetElegidoAsync(numero, req.ClienteId);
        await _db.SaveChangesAsync();
        var nombre = await _db.CafeClientes.AsNoTracking()
            .Where(x => x.Id == req.ClienteId).Select(x => x.Nombre).FirstOrDefaultAsync();
        return Ok(new { ok = true, clienteId = req.ClienteId, clienteNombre = nombre });
    }

    /// <summary>2026-08-20: POST contactos/desvincular-cliente — sacar UNA razon social de la lista
    /// del telefono (las otras quedan). Si era la que estaba tildada, el tilde pasa a la primera
    /// que quede.</summary>
    [HttpPost("contactos/desvincular-cliente")]
    [Authorize]
    public async Task<IActionResult> DesvincularCliente([FromBody] ElegirClienteRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Numero)) return BadRequest(new { error = "Falta el número" });
        var numero = req!.Numero.Trim();
        if (!numero.StartsWith("whatsapp:")) numero = "whatsapp:" + numero;

        var filas = await _db.WhatsAppContactoClientes
            .Where(v => v.Numero == numero && v.ClienteId == req.ClienteId).ToListAsync();
        if (filas.Count > 0) _db.WhatsAppContactoClientes.RemoveRange(filas);

        var c = await _db.WhatsAppTwilioContactos.FirstOrDefaultAsync(x => x.Numero == numero);
        if (c != null && c.ClienteId == req.ClienteId)
        {
            // Si sacamos el principal, asciende la primera que quede (o queda sin cliente).
            var queda = await _db.WhatsAppContactoClientes
                .Where(v => v.Numero == numero && v.ClienteId != req.ClienteId)
                .OrderBy(v => v.Orden).ThenBy(v => v.Id)
                .Select(v => (int?)v.ClienteId).FirstOrDefaultAsync();
            c.ClienteId = queda;
        }
        // El que tenia tildada ESA razon social pasa a no tener tilde (agarra el principal solo).
        var elegs = await _db.WhatsAppClientesElegidos
            .Where(e => e.Numero == numero && e.ClienteId == req.ClienteId).ToListAsync();
        if (elegs.Count > 0) _db.WhatsAppClientesElegidos.RemoveRange(elegs);

        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>2026-08-03: GET contactos/sugerencia-cliente?numero=whatsapp:+549... — busca un
    /// cliente cuyo teléfono coincida con el número del chat, para ofrecer vincularlo de un toque.
    /// Compara por los últimos 8 dígitos (el "abonado"), que casi siempre coinciden aunque el
    /// teléfono del cliente esté guardado con o sin 54/9/15/código de área. Devuelve null si no hay.</summary>
    [HttpGet("contactos/sugerencia-cliente")]
    [Authorize]
    public async Task<IActionResult> SugerenciaCliente([FromQuery] string numero = "")
    {
        var digits = new string((numero ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length < 8) return Ok((object?)null);
        var tail = digits.Substring(digits.Length - 8);
        var match = await _db.CafeClientes.AsNoTracking()
            .Where(c => c.Telefono != null && c.Telefono != "" && c.Telefono.Contains(tail))
            .OrderBy(c => c.Nombre)
            .Select(c => new { c.Id, c.Nombre, CodigoInterno = c.CodigoInterno.HasValue ? c.CodigoInterno.ToString() : null })
            .FirstOrDefaultAsync();
        return Ok(match == null ? null : (object)match);
    }

    /// <summary>2026-08-03: GET contactos/numero-cliente/{clienteId} — devuelve el número de WhatsApp
    /// del chat VINCULADO a ese cliente (formato "whatsapp:+549…"), para poder enviarle el comprobante
    /// desde una venta aunque su ficha no tenga teléfono cargado. Si el cliente tiene más de un chat
    /// vinculado, elige el que tuvo actividad más reciente. Devuelve null si no hay ninguno.</summary>
    [HttpGet("contactos/numero-cliente/{clienteId:int}")]
    [Authorize]
    public async Task<IActionResult> NumeroDeCliente(int clienteId)
    {
        var numeros = await _db.WhatsAppTwilioContactos.AsNoTracking()
            .Where(c => c.ClienteId == clienteId && c.Numero != "")
            .Select(c => new { c.Numero, c.CreatedAt })
            .ToListAsync();
        if (numeros.Count == 0) return Ok((object?)null);

        // Elegimos el número con el mensaje más reciente; si ninguno tiene mensajes, el contacto más nuevo.
        var soloNums = numeros.Select(n => n.Numero).ToList();
        var ultimaActividad = await _db.WhatsAppTwilioMensajes.AsNoTracking()
            .Where(m => soloNums.Contains(m.Numero))
            .GroupBy(m => m.Numero)
            .Select(g => new { Numero = g.Key, Ult = g.Max(m => m.CreatedAt) })
            .ToListAsync();

        string elegido = ultimaActividad.Count > 0
            ? ultimaActividad.OrderByDescending(x => x.Ult).First().Numero
            : numeros.OrderByDescending(n => n.CreatedAt).First().Numero;

        return Ok(new { numero = elegido });
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

    // ===== Corregir el numero de una conversacion (cuando quedo mal cargado) =====
    public record CorregirNumeroRequest(string NumeroViejo, string NumeroNuevo);

    /// <summary>2026-08-04: POST conversaciones/corregir-numero — cambia el numero de un chat que quedo
    /// mal (ej un español al que se le pego el 549). MUEVE todos los mensajes y, si estaba en la agenda,
    /// tambien el contacto, al numero correcto. El chat en el celular del cliente no se toca.</summary>
    [HttpPost("conversaciones/corregir-numero")]
    [Authorize]
    public async Task<IActionResult> CorregirNumero([FromBody] CorregirNumeroRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.NumeroViejo) || string.IsNullOrWhiteSpace(req.NumeroNuevo))
            return BadRequest(new { error = "Faltan datos" });

        var digitsNuevo = NormalizarNumeroWa(req.NumeroNuevo);
        if (digitsNuevo.Length < 8)
            return BadRequest(new { error = "El numero nuevo no parece valido. Poné el número completo con código de país (ej +34 642265173)." });
        var numeroNuevoStd = "whatsapp:+" + digitsNuevo;

        if (numeroNuevoStd == req.NumeroViejo)
            return Ok(new { ok = true, numero = numeroNuevoStd, cambiados = 0 });

        var msgs = await _db.WhatsAppTwilioMensajes.Where(m => m.Numero == req.NumeroViejo).ToListAsync();
        if (msgs.Count == 0) return NotFound(new { error = "No hay mensajes de ese número" });
        foreach (var m in msgs) m.Numero = numeroNuevoStd;

        // Si el numero viejo ya existia en la agenda, moverlo tambien (salvo que el nuevo ya este cargado).
        var contViejo = await _db.WhatsAppTwilioContactos.FirstOrDefaultAsync(c => c.Numero == req.NumeroViejo);
        if (contViejo != null && !await _db.WhatsAppTwilioContactos.AnyAsync(c => c.Numero == numeroNuevoStd))
            contViejo.Numero = numeroNuevoStd;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Numero corregido: {Viejo} → {Nuevo} ({Count} mensajes)", req.NumeroViejo, numeroNuevoStd, msgs.Count);
        return Ok(new { ok = true, numero = numeroNuevoStd, cambiados = msgs.Count });
    }

    // ===== 2026-08-28: chats que ve DEPOSITO (configurables desde la pantalla de WhatsApp) =====
    // Antes la lista estaba escrita en el codigo (Web/Services/DepositoChats.cs) y sumar un chat
    // era tocar el programa. Ahora vive en AppSettings["whatsapp.deposito.chats"] como JSON y se
    // asigna desde el menu ⋮ del chat. Si la clave no existe, vale la lista historica de siempre.
    private const string KeyDepositoChats = "whatsapp.deposito.chats";

    public record DepositoChatDto(string Numero, string Linea, string Titulo, string LineaNombre);
    public record DepositoChatToggleRequest(string Numero, string Linea, string? Titulo, string? LineaNombre, bool Asignado);

    private static readonly DepositoChatDto[] DepositoChatsPorDefecto =
    {
        new("whatsapp:+5491158464160", "1195191513683780", "Gabriel Palanica", "FIJO TRANSRADIO")
    };

    /// <summary>Compara numeros/lineas por DIGITOS: "whatsapp:+549..." y "549..." son lo mismo.</summary>
    private static string DigitosDe(string? s)
        => string.IsNullOrEmpty(s) ? "" : new string(s.Where(char.IsDigit).ToArray());

    private async Task<List<DepositoChatDto>> LeerDepositoChatsAsync()
    {
        var fila = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == KeyDepositoChats);
        if (fila is null || string.IsNullOrWhiteSpace(fila.Value)) return DepositoChatsPorDefecto.ToList();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<DepositoChatDto>>(fila.Value) ?? new();
        }
        catch
        {
            _logger.LogWarning("La lista de chats de Deposito quedo ilegible; se usa la de por defecto.");
            return DepositoChatsPorDefecto.ToList();
        }
    }

    /// <summary>GET /deposito-chats — que chats ve Deposito. Lo lee cualquiera (Deposito tambien).</summary>
    [HttpGet("deposito-chats")]
    [Authorize]
    public async Task<IActionResult> GetDepositoChats() => Ok(await LeerDepositoChatsAsync());

    /// <summary>POST /deposito-chats — asigna o saca un chat de la lista. Deposito NO puede.</summary>
    [HttpPost("deposito-chats")]
    [Authorize]
    public async Task<IActionResult> SetDepositoChat([FromBody] DepositoChatToggleRequest req)
    {
        if (await EsDepositoAsync()) return Forbid();
        var num = DigitosDe(req.Numero);
        var lin = DigitosDe(req.Linea);
        // Sin linea no se puede: la lista identifica un chat por numero + linea nuestra.
        if (num.Length == 0 || lin.Length == 0)
            return BadRequest(new { error = "Falta el numero o la linea de este chat." });

        var lista = await LeerDepositoChatsAsync();
        lista.RemoveAll(c => DigitosDe(c.Numero) == num && DigitosDe(c.Linea) == lin);
        if (req.Asignado)
        {
            lista.Add(new DepositoChatDto(
                req.Numero.Trim(),
                req.Linea.Trim(),
                string.IsNullOrWhiteSpace(req.Titulo) ? req.Numero.Trim() : req.Titulo!.Trim(),
                (req.LineaNombre ?? "").Trim()));
        }

        var json = System.Text.Json.JsonSerializer.Serialize(lista);
        var fila = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == KeyDepositoChats);
        if (fila is null)
            _db.AppSettings.Add(new AppSetting { Key = KeyDepositoChats, Value = json, UpdatedAt = DateTime.UtcNow });
        else { fila.Value = json; fila.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
        return Ok(lista);
    }

    // ===== Reacciones a mensajes =====
    // 2026-07-23 (pedido Osmar): ademas de guardarse como etiqueta interna, si el mensaje entro por
    // la Cloud API (Canal=CLOUD, tiene wamid) la reaccion SE MANDA al WhatsApp del cliente — la ve
    // en su celu como una reaccion comun. Quitar la reaccion tambien se la saca al cliente.
    // OJO: WhatsApp permite UNA reaccion nuestra por mensaje: si marcas dos emojis, el cliente ve el ultimo.
    // 2026-08-28: Firma = abreviatura de QUIEN reacciona (oficina os/ger/ga del PIN; Deposito alex/walter...).
    // El toggle es por (mensaje + emoji + firma): asi dos personas pueden marcar el mismo emoji sin
    // borrarse la reaccion entre ellas. Sin firma (null) se comporta igual que siempre.
    public record ReaccionRequest(int MensajeId, string Emoji, string? Firma = null);

    /// <summary>POST /reacciones — toggle: si ya existe esa reaccion (mensaje+emoji+firma), la borra; sino la crea.</summary>
    [HttpPost("reacciones")]
    [Authorize]
    public async Task<IActionResult> ToggleReaccion([FromBody] ReaccionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Emoji)) return BadRequest();
        var firma = string.IsNullOrWhiteSpace(req.Firma) ? null : req.Firma.Trim();
        var q = _db.WhatsAppTwilioReacciones.Where(r => r.MensajeId == req.MensajeId && r.Emoji == req.Emoji);
        q = firma == null ? q.Where(r => r.Firma == null) : q.Where(r => r.Firma == firma);
        var existing = await q.FirstOrDefaultAsync();
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
                Firma = firma,
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
                // 2026-08-28: WhatsApp muestra UNA sola reaccion nuestra por mensaje, asi que le mandamos
                // siempre la ULTIMA que quedo puesta de nuestro lado (la nueva pisa a la anterior: 💻 -> 📦).
                // Al quitar: si todavia queda alguna, va esa; si no queda ninguna, emoji vacio (Meta la borra).
                var emojiParaElCliente = req.Emoji;
                if (removed)
                {
                    emojiParaElCliente = await _db.WhatsAppTwilioReacciones.AsNoTracking()
                        .Where(r => r.MensajeId == req.MensajeId && (r.UsuarioId == null || r.UsuarioId != -1))
                        .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
                        .Select(r => r.Emoji).FirstOrDefaultAsync() ?? "";
                }
                // 2026-07-23 (multi-línea): la reacción sale por la línea del propio mensaje.
                var sid = await _meta.SendReactionAsync(msg.Numero, msg.TwilioMessageSid, emojiParaElCliente, lineaPhoneId: msg.LineaPhoneId);
                enviadaAlCliente = sid != null && emojiParaElCliente.Length > 0;
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
    /// <summary>
    /// 2026-08-21: BUSCADOR GLOBAL — la palabra en los mensajes de TODAS las charlas, no solo
    /// en la que estás mirando. Pedido del dueño: "quiero buscar una palabra en todos los chats".
    ///
    /// Devuelve el mensaje encontrado con un pedacito de texto alrededor, de qué charla es y de
    /// qué línea, para poder saltar ahí de un toque.
    /// </summary>
    [HttpGet("buscar-mensajes")]
    [Authorize]
    public async Task<IActionResult> BuscarMensajes([FromQuery] string? q, [FromQuery] string? linea = null,
        [FromQuery] int limit = 60)
    {
        var texto = (q ?? "").Trim();
        if (texto.Length < 2) return Ok(new { total = 0, hits = new List<object>() });

        // Depósito ve SOLO sus chats: un buscador que mira todas las charlas no es para él.
        if (await EsDepositoAsync()) return Forbid();

        limit = Math.Clamp(limit, 1, 200);

        // Los comodines de SQL (% _ [) se escapan para que se busquen como texto común.
        var escapado = texto.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
        var patron = $"%{escapado}%";

        // La base es "case insensitive" pero SÍ distingue tildes, así que forzamos una
        // comparación sin tildes: buscar "camion" tiene que encontrar "camión".
        var qy = _db.WhatsAppTwilioMensajes.AsNoTracking()
            .Where(m => m.Cuerpo != null &&
                        EF.Functions.Like(EF.Functions.Collate(m.Cuerpo, "SQL_Latin1_General_CP1_CI_AI"), patron));

        if (!string.IsNullOrWhiteSpace(linea))
        {
            var lp = linea == "null" ? null : linea;
            qy = qy.Where(m => m.LineaPhoneId == lp);
        }

        var total = await qy.CountAsync();
        var hits = await qy
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .Select(m => new { m.Id, m.Numero, m.LineaPhoneId, m.NombrePerfil, m.Direccion, m.Cuerpo, m.CreatedAt, m.Canal })
            .ToListAsync();

        // Nombre de la agenda (si lo tiene) y nombre visible de cada línea.
        var numeros = hits.Select(h => h.Numero).Distinct().ToList();
        var contactos = await _db.WhatsAppTwilioContactos.AsNoTracking()
            .Where(c => numeros.Contains(c.Numero))
            .ToDictionaryAsync(c => c.Numero, c => c.Nombre);
        var lineasNombres = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith("whatsapp.linea."))
            .ToDictionaryAsync(s => s.Key.Substring("whatsapp.linea.".Length), s => s.Value);

        var res = hits.Select(h => new
        {
            id = h.Id,
            numero = h.Numero,
            linea = h.LineaPhoneId,
            lineaNombre = h.LineaPhoneId != null && lineasNombres.TryGetValue(h.LineaPhoneId, out var ln) ? ln : null,
            nombre = contactos.TryGetValue(h.Numero, out var nom) && !string.IsNullOrWhiteSpace(nom)
                ? nom
                : (string.IsNullOrWhiteSpace(h.NombrePerfil) ? h.Numero.Replace("whatsapp:", "") : h.NombrePerfil),
            direccion = h.Direccion,
            fecha = h.CreatedAt,
            canal = h.Canal,
            extracto = Extracto(h.Cuerpo, texto)
        }).ToList();

        return Ok(new { total, hits = res, tope = total > limit });
    }

    /// <summary>Un pedacito del mensaje alrededor de la palabra buscada, para el listado.</summary>
    private static string Extracto(string? cuerpo, string palabra)
    {
        var t = (cuerpo ?? "").Replace("\n", " ").Replace("\r", " ").Trim();
        if (t.Length <= 140) return t;
        var i = t.IndexOf(palabra, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return t.Substring(0, 140) + "…";
        var desde = Math.Max(0, i - 50);
        var largo = Math.Min(140, t.Length - desde);
        var trozo = t.Substring(desde, largo);
        return (desde > 0 ? "…" : "") + trozo + (desde + largo < t.Length ? "…" : "");
    }

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
                m.Procesado, m.RespuestaEnviada, m.CreatedAt, m.EstadoEntrega,
                // 2026-08-22: si Meta rechazo la entrega, el motivo en castellano para mostrarlo en el ⚠
                m.EntregaError,
                // 2026-08-18: el SID propio va al front para poder saltar al mensaje citado
                // cuando tocás la cajita gris de "respondiendo a…".
                m.TwilioMessageSid,
                m.ReplyToSid, m.OcultoDeposito,
                // 2026-08-19: mensaje anulado (se mandó una corrección). Se ve tachado de nuestro lado.
                m.AnuladoAt, m.AnuladoPor
            })
            .ToListAsync();
        msgs.Reverse();

        // 2026-08-07: ¿el que mira es Depósito? Si sí, los mensajes marcados como ocultos se le
        // devuelven SIN contenido (solo la marca), así ve "Mensaje ocultado" y el texto real ni le llega.
        var esDep = await EsDepositoAsync();

        // 2026-08-05: "responder citando". Para los mensajes que citan a otro (ReplyToSid), buscamos
        // el mensaje original por su TwilioMessageSid y armamos un preview (quién y qué) para la burbuja.
        var replySids = msgs.Where(m => m.ReplyToSid != null).Select(m => m.ReplyToSid!).Distinct().ToList();
        var citados = replySids.Count == 0
            ? new Dictionary<string, (string Direccion, string? Cuerpo, string? MediaUrl, string? MediaFilename)>()
            : await _db.WhatsAppTwilioMensajes.AsNoTracking()
                .Where(x => x.TwilioMessageSid != null && replySids.Contains(x.TwilioMessageSid))
                .Select(x => new { x.TwilioMessageSid, x.Direccion, x.Cuerpo, x.MediaUrl, x.MediaFilename })
                .ToDictionaryAsync(x => x.TwilioMessageSid!, x => (x.Direccion, x.Cuerpo, x.MediaUrl, x.MediaFilename));
        // Cargar reacciones de estos mensajes
        var ids = msgs.Select(m => m.Id).ToList();
        // 2026-08-05: EsCliente = la reacción la puso el CLIENTE (UsuarioId = -1 desde el webhook),
        // no nosotros. Sirve para mostrarla distinta en el chat.
        // 2026-08-28: se agrupa en memoria (son pocas filas) para poder devolver ademas las FIRMAS,
        // o sea quien puso cada emoji: "💻 os", "📦 alex". SQL no sabe juntar textos con coma.
        var reacFilas = await _db.WhatsAppTwilioReacciones.AsNoTracking()
            .Where(r => ids.Contains(r.MensajeId))
            .Select(r => new { r.MensajeId, r.Emoji, r.UsuarioId, r.Firma })
            .ToListAsync();
        var reacByMsg = reacFilas.GroupBy(r => r.MensajeId)
            .ToDictionary(g => g.Key, g => g.GroupBy(x => x.Emoji).Select(ge => new
            {
                Emoji = ge.Key,
                Count = ge.Count(),
                EsCliente = ge.Max(x => x.UsuarioId) == -1,
                Firmas = string.Join(" · ", ge.Select(x => x.Firma).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct())
            }).ToList());
        var result = msgs.Select(m =>
        {
            string? replyPreview = null;
            bool replyFromMe = false;
            if (m.ReplyToSid != null && citados.TryGetValue(m.ReplyToSid, out var orig))
            {
                replyFromMe = orig.Direccion == "OUTGOING";
                replyPreview = PreviewCitado(orig.Cuerpo, orig.MediaUrl, orig.MediaFilename);
            }
            var reacs = reacByMsg.TryGetValue(m.Id, out var rs) ? rs.Cast<object>().ToList() : new List<object>();
            // Para DEPÓSITO: si el mensaje está oculto, no le mandamos NADA del contenido (solo la marca).
            var blank = esDep && m.OcultoDeposito;
            return new
            {
                m.Id, m.Direccion, m.Numero, m.NombrePerfil,
                Cuerpo = blank ? null : m.Cuerpo,
                MediaUrl = blank ? null : m.MediaUrl,
                MediaFilename = blank ? null : m.MediaFilename,
                m.NumMedia, m.Procesado,
                RespuestaEnviada = blank ? null : m.RespuestaEnviada,
                m.CreatedAt, m.EstadoEntrega, m.EntregaError,
                Reacciones = blank ? new List<object>() : reacs,
                // 2026-08-18: Sid propio, para que la pantalla pueda saltar del mensaje citado al original.
                Sid = m.TwilioMessageSid,
                ReplyToSid = blank ? null : m.ReplyToSid,
                ReplyPreview = blank ? null : replyPreview,
                ReplyFromMe = !blank && replyFromMe,
                OcultoDeposito = m.OcultoDeposito,
                Anulado = m.AnuladoAt != null,
                AnuladoPor = m.AnuladoPor
            };
        }).ToList();
        return Ok(result);
    }

    /// <summary>2026-08-05: texto corto para la burbuja citada (responder citando). Si el mensaje
    /// original era un adjunto, muestra un ícono + nombre; si era texto, lo recorta.</summary>
    private static string PreviewCitado(string? cuerpo, string? mediaUrl, string? mediaFilename)
    {
        var texto = (cuerpo ?? "").Trim();
        if (string.IsNullOrEmpty(texto) && !string.IsNullOrEmpty(mediaUrl))
            texto = string.IsNullOrWhiteSpace(mediaFilename) ? "📎 Archivo" : $"📎 {mediaFilename}";
        else if (!string.IsNullOrEmpty(mediaUrl) && !string.IsNullOrEmpty(mediaFilename))
            texto = $"📎 {texto}";
        if (texto.Length > 90) texto = texto.Substring(0, 90) + "…";
        return texto;
    }

    /// <summary>2026-08-07: ¿el usuario logueado es de DEPÓSITO? (mismo criterio que el frontend:
    /// permiso "deposito" y NO "cafe"/"oficina"). El admin NO es depósito.</summary>
    private async Task<bool> EsDepositoAsync()
    {
        var idStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        if (!int.TryParse(idStr, out var uid)) return false;
        var user = await _db.Users.AsNoTracking().Include(u => u.RoleNav).FirstOrDefaultAsync(u => u.Id == uid);
        if (user == null) return false;
        var roleName = user.RoleNav?.Name ?? user.Role;
        if (string.Equals(roleName, "admin", StringComparison.OrdinalIgnoreCase)) return false;
        var perms = await _db.RolePermissions.AsNoTracking()
            .Where(rp => rp.RoleId == user.RoleId).Select(rp => rp.MenuKey).ToListAsync();
        return perms.Contains("deposito") && !perms.Contains("cafe") && !perms.Contains("oficina");
    }

    public record OcultarDepositoReq(bool Oculto);

    /// <summary>2026-08-07: marca/desmarca un mensaje como oculto para Depósito. Solo admin/oficina
    /// (Depósito no puede). El mensaje sigue visible para admin/oficina; a Depósito le aparece
    /// "Mensaje ocultado".</summary>
    [HttpPost("mensajes/{id:int}/ocultar-deposito")]
    [Authorize]
    public async Task<IActionResult> OcultarDeposito(int id, [FromBody] OcultarDepositoReq req)
    {
        if (await EsDepositoAsync()) return Forbid();
        var m = await _db.WhatsAppTwilioMensajes.FirstOrDefaultAsync(x => x.Id == id);
        if (m == null) return NotFound(new { error = "Mensaje no encontrado" });
        m.OcultoDeposito = req?.Oculto ?? true;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, oculto = m.OcultoDeposito });
    }

    public record AnularMensajeReq(bool Anular);

    /// <summary>2026-08-19: marca (o desmarca) un mensaje NUESTRO como ANULADO. No lo borra ni lo
    /// toca del lado del cliente —eso no se puede—: solo lo muestra tachado en nuestras pantallas,
    /// para que nadie del equipo siga trabajando sobre el precio o el dato equivocado.</summary>
    [HttpPost("mensajes/{id:int}/anular")]
    [Authorize]
    public async Task<IActionResult> AnularMensaje(int id, [FromBody] AnularMensajeReq req)
    {
        var m = await _db.WhatsAppTwilioMensajes.FirstOrDefaultAsync(x => x.Id == id);
        if (m == null) return NotFound(new { error = "Mensaje no encontrado" });
        if (m.Direccion != "OUTGOING")
            return BadRequest(new { error = "Solo se pueden anular los mensajes que mandamos nosotros" });
        if (req?.Anular ?? true)
        {
            m.AnuladoAt ??= DateTime.UtcNow;
            var quien = User?.Identity?.Name ?? "";
            m.AnuladoPor = quien.Length > 60 ? quien.Substring(0, 60) : (string.IsNullOrEmpty(quien) ? null : quien);
        }
        else { m.AnuladoAt = null; m.AnuladoPor = null; }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, anulado = m.AnuladoAt != null, anuladoPor = m.AnuladoPor });
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

    // ===== CATALOGOS — archivos permanentes (PDF/documentos/imagenes) =====
    // A diferencia de los uploads de "Mis subidos" (24h), estos quedan guardados para siempre.
    // Se guardan en el mismo volume /data/whatsapp-uploads (que NO se purga solo).

    public record CatalogoDto(int Id, string OriginalFilename, long SizeBytes, string ContentType, DateTime CreatedAt);

    /// <summary>POST /api/whatsapp/twilio/catalogo-upload — sube un catalogo permanente.</summary>
    [HttpPost("catalogo-upload")]
    [Authorize]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> CatalogoUpload([FromForm] IFormFile? file)
    {
        if (file == null || file.Length == 0) return BadRequest(new { error = "No se recibio archivo" });
        if (file.Length > 16 * 1024 * 1024) return BadRequest(new { error = "El archivo supera el limite de 16 MB que admite WhatsApp" });

        Directory.CreateDirectory(UploadsDir);
        var token = GenerarToken();
        var ext = Path.GetExtension(file.FileName);
        var stored = token + ext;
        using (var fs = System.IO.File.Create(Path.Combine(UploadsDir, stored))) await file.CopyToAsync(fs);

        var cat = new WhatsAppCatalogo
        {
            Token = token,
            OriginalFilename = file.FileName,
            StoredFilename = stored,
            ContentType = string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType,
            SizeBytes = new FileInfo(Path.Combine(UploadsDir, stored)).Length,
            CreatedAt = DateTime.UtcNow
        };
        _db.WhatsAppCatalogos.Add(cat);
        await _db.SaveChangesAsync();
        return Ok(new CatalogoDto(cat.Id, cat.OriginalFilename, cat.SizeBytes, cat.ContentType, cat.CreatedAt));
    }

    /// <summary>DELETE /api/whatsapp/twilio/catalogo/{id} — borra un catalogo (fila + archivo).</summary>
    [HttpDelete("catalogo/{id:int}")]
    [Authorize]
    public async Task<IActionResult> CatalogoDelete(int id)
    {
        var cat = await _db.WhatsAppCatalogos.FirstOrDefaultAsync(c => c.Id == id);
        if (cat == null) return NotFound(new { error = "Catalogo no encontrado" });
        try { var p = Path.Combine(UploadsDir, cat.StoredFilename); if (System.IO.File.Exists(p)) System.IO.File.Delete(p); } catch { }
        _db.WhatsAppCatalogos.Remove(cat);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
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
            // 2026-08-18: aviso en vivo para que aparezca al instante en las otras pantallas.
            await _waLive.AvisarAsync(req.Numero, lin, "OUTGOING");
            return Ok(new { ok = true, sid, id = msg.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando media WhatsApp");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    public record SendContactoRequest(string Numero, string ContactoNombre, string ContactoNumero, string? LineaPhoneId = null);

    /// <summary>POST /api/whatsapp/twilio/send-contacto — comparte una tarjeta de contacto en un chat (Meta Cloud API).</summary>
    [HttpPost("send-contacto")]
    [Authorize]
    public async Task<IActionResult> SendContacto([FromBody] SendContactoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Numero) || string.IsNullOrWhiteSpace(req.ContactoNumero))
            return BadRequest(new { error = "Faltan datos del contacto" });
        if (!_meta.IsConfigured)
            return StatusCode(503, new { error = "WhatsApp (Meta) no está configurado" });

        // Línea: la del pedido, o si no vino, la de la conversación (última entrante).
        var lineaConv = req.LineaPhoneId;
        if (string.IsNullOrWhiteSpace(lineaConv))
        {
            lineaConv = await _db.WhatsAppTwilioMensajes.AsNoTracking()
                .Where(x => x.Numero == req.Numero && x.Direccion == "INCOMING" && x.LineaPhoneId != null)
                .OrderByDescending(x => x.Id).Select(x => x.LineaPhoneId).FirstOrDefaultAsync();
        }

        try
        {
            var nombre = string.IsNullOrWhiteSpace(req.ContactoNombre) ? req.ContactoNumero : req.ContactoNombre.Trim();
            var wamid = await _meta.SendContactAsync(req.Numero, nombre, req.ContactoNumero, lineaPhoneId: lineaConv);
            if (string.IsNullOrEmpty(wamid))
                return StatusCode(502, new { error = "Meta rechazó el envío del contacto" });

            // Lo guardamos con el mismo marcador que los entrantes, para mostrarlo como tarjeta.
            var payloadCard = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new Dictionary<string, string> { ["n"] = nombre, ["t"] = MetaWhatsAppService.NormalizeTo(req.ContactoNumero) }
            });
            var msg = new WhatsAppTwilioMensaje
            {
                Direccion = "OUTGOING",
                Numero = req.Numero,
                Cuerpo = "CONTACTO_WA:" + payloadCard,
                LineaPhoneId = lineaConv,
                TwilioMessageSid = wamid,
                Canal = "CLOUD",
                Procesado = true,
                CreatedAt = DateTime.UtcNow
            };
            _db.WhatsAppTwilioMensajes.Add(msg);
            await _db.SaveChangesAsync();
            return Ok(new { ok = true, sid = wamid, id = msg.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando contacto WhatsApp");
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

    public record ServerFileDto(string Tipo, int Id, string Label, string? SubLabel, string? Info, DateTime Fecha, string? PreviewUrl = null, string? Icon = null);

    /// <summary>Emoji segun el tipo de archivo, para mostrar en la lista cuando no hay miniatura real.</summary>
    private static string IconoPorTipo(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return "📎";
        if (contentType.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase)) return "📄";
        if (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return "🎵";
        if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return "🎬";
        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "🖼️";
        if (contentType.Contains("word") || contentType.Contains("document")) return "📝";
        if (contentType.Contains("sheet") || contentType.Contains("excel")) return "📊";
        return "📎";
    }

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
                    u.CreatedAt,
                    // Miniatura real solo para imagenes: el endpoint /files/{token} las sirve "inline".
                    u.ContentType.StartsWith("image/") ? $"/api/whatsapp/twilio/files/{u.Token}" : null,
                    IconoPorTipo(u.ContentType)))
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
                    c.Fecha, null, "📄"))
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
                return new ServerFileDto("VENTA", v.Id, label, v.Cliente, info, v.Fecha, null, "📄");
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
                    "LISTA", l.Id, l.Nombre,
                    l.ClienteNav != null ? $"Cliente: {l.ClienteNav.Nombre}" : (l.TipoCliente ?? "General"),
                    l.NumeroLista != null ? $"Lista N° {l.NumeroLista}" : "",
                    l.UpdatedAt, null, "📄"))
                .ToListAsync();
            return Ok(list);
        }
        if (tipo == "CATALOGO")
        {
            // 2026-08-04: catalogos permanentes (PDF/documentos/imagenes). No expiran.
            var q = _db.WhatsAppCatalogos.AsQueryable();
            if (s != null) q = q.Where(c => c.OriginalFilename.Contains(s));
            var list = await q.OrderByDescending(c => c.CreatedAt).Take(take)
                .Select(c => new ServerFileDto(
                    "CATALOGO", c.Id, c.OriginalFilename,
                    $"{FormatSize(c.SizeBytes)} · {c.ContentType}",
                    null,
                    c.CreatedAt, null, IconoPorTipo(c.ContentType)))
                .ToListAsync();
            return Ok(list);
        }
        if (tipo == "ALQUILER")
        {
            // 2026-08-05: reservas de alquiler, para mandar el comprobante por el chat.
            var q = _db.AlqReservas.Include(r => r.ClienteNav).Where(r => r.Estado != "cancelado");
            if (s != null)
                q = q.Where(r => r.Numero.Contains(s)
                    || (r.ClienteNav != null && r.ClienteNav.Nombre.Contains(s))
                    || (r.DireccionEvento != null && r.DireccionEvento.Contains(s)));
            var list = await q.OrderByDescending(r => r.CreatedAt).Take(take)
                .Select(r => new ServerFileDto(
                    "ALQUILER", r.Id, $"Reserva {r.Numero}",
                    r.ClienteNav != null ? r.ClienteNav.Nombre : "—",
                    $"${r.MontoTotal:N0}",
                    r.CreatedAt, null, "🎪"))
                .ToListAsync();
            return Ok(list);
        }
        if (tipo == "VISITA")
        {
            // 2026-08-05: recibos de visita, para mandarlos por el chat.
            var q = _db.Visitas.AsQueryable();
            if (s != null)
                q = q.Where(v => v.ClienteNombre.Contains(s)
                    || (v.Direccion != null && v.Direccion.Contains(s))
                    || v.Descripcion.Contains(s));
            var list = await q.OrderByDescending(v => v.CreatedAt).Take(take)
                .Select(v => new ServerFileDto(
                    "VISITA", v.Id, $"Visita N° {v.Numero:0000}",
                    v.ClienteNombre,
                    v.Estado == "realizada" ? "Realizada" : "Pendiente",
                    v.CreatedAt, null, "📋"))
                .ToListAsync();
            return Ok(list);
        }
        return BadRequest(new { error = "Tipo no soportado. Validos: UPLOAD, COBRANZA, VENTA, LISTA, CATALOGO, ALQUILER, VISITA" });
    }

    public record SendServerFileRequest(string Numero, string Tipo, int Id, string? Caption, string? LineaPhoneId = null,
        // 2026-08-26: true = armá el archivo y devolveme su dirección, pero NO lo mandes. Lo usa el
        // programador de mensajes: deja el adjunto ya resuelto y el robot lo manda a la hora fijada,
        // por el mismo camino que un archivo subido con el clip (que ya funcionaba).
        bool SoloPreparar = false);

    /// <summary>POST /api/whatsapp/twilio/send-server-file
    /// Envía un archivo del servidor al WhatsApp del numero indicado.</summary>
    [HttpPost("send-server-file")]
    [Authorize]
    public async Task<IActionResult> SendServerFile([FromBody] SendServerFileRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Numero)) return BadRequest(new { error = "Numero obligatorio" });
        if (!_outbound.AnyConfigured) return StatusCode(503, new { error = "WhatsApp no configurado (ni Meta ni Twilio)" });

        // 2026-08-03: normalizamos el destino al MISMO formato que guarda la bandeja
        // ("whatsapp:+549…"). Si mandamos el número crudo de la ficha (ej "11 5994-5852"),
        // el mensaje abría una conversación nueva huérfana en vez de sumarse al chat del
        // cliente, y encima la Cloud API podía no entregarlo (le faltaba el 549).
        var numeroNorm = MetaWhatsAppService.ToInboxWhatsApp(req.Numero);

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
                    NumeroDestino = numeroNorm,
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
                    NumeroDestino = numeroNorm,
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
                    NumeroDestino = numeroNorm,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddHours(24)
                };
                _db.WhatsAppTwilioUploads.Add(up);
                await _db.SaveChangesAsync();
                mediaUrl = $"{Request.Scheme}://{Request.Host}/api/whatsapp/twilio/files/{token}{Path.GetExtension(stored)}";
                break;
            }
            case "CATALOGO":
            {
                // 2026-08-04: catalogo permanente. Reusa el archivo ya guardado creando un token
                // de descarga temporal (24h) para que Meta lo baje; el catalogo NO se toca.
                var cat = await _db.WhatsAppCatalogos.FirstOrDefaultAsync(c => c.Id == req.Id);
                if (cat == null) return NotFound(new { error = "Catalogo no encontrado" });
                var srcPath = Path.Combine(UploadsDir, cat.StoredFilename);
                if (!System.IO.File.Exists(srcPath)) return NotFound(new { error = "El archivo del catalogo no esta en el servidor" });
                filename = cat.OriginalFilename;

                Directory.CreateDirectory(UploadsDir);
                var token = GenerarToken();
                var stored = token + Path.GetExtension(cat.StoredFilename);
                System.IO.File.Copy(srcPath, Path.Combine(UploadsDir, stored), overwrite: true);
                var up = new WhatsAppTwilioUpload
                {
                    Token = token,
                    OriginalFilename = filename,
                    StoredFilename = stored,
                    ContentType = cat.ContentType,
                    SizeBytes = cat.SizeBytes,
                    NumeroDestino = numeroNorm,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddHours(24)
                };
                _db.WhatsAppTwilioUploads.Add(up);
                await _db.SaveChangesAsync();
                mediaUrl = $"{Request.Scheme}://{Request.Host}/api/whatsapp/twilio/files/{token}{Path.GetExtension(stored)}";
                break;
            }
            case "ALQUILER":
            {
                // 2026-08-05: reserva de alquiler. Reusa el MISMO PDF que descarga la pantalla de Reservas.
                var (bytes, fname) = await _alqReservasController.GenerarPdfBytesAsync(req.Id);
                if (bytes is null) return NotFound(new { error = "Reserva no encontrada" });
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
                    NumeroDestino = numeroNorm,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddHours(24)
                };
                _db.WhatsAppTwilioUploads.Add(up);
                await _db.SaveChangesAsync();
                mediaUrl = $"{Request.Scheme}://{Request.Host}/api/whatsapp/twilio/files/{token}{Path.GetExtension(stored)}";
                break;
            }
            case "VISITA":
            {
                // 2026-08-05: recibo de visita. Reusa el MISMO PDF del recibo publico por QR.
                var (bytes, fname) = await _visitasController.GenerarReciboPdfBytesAsync(req.Id);
                if (bytes is null) return NotFound(new { error = "Visita no encontrada" });
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
                    NumeroDestino = numeroNorm,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddHours(24)
                };
                _db.WhatsAppTwilioUploads.Add(up);
                await _db.SaveChangesAsync();
                mediaUrl = $"{Request.Scheme}://{Request.Host}/api/whatsapp/twilio/files/{token}{Path.GetExtension(stored)}";
                break;
            }
            default:
                return BadRequest(new { error = "Tipo no soportado. Validos: UPLOAD, COBRANZA, VENTA, LISTA, CATALOGO, ALQUILER, VISITA" });
        }

        // 2026-08-26: el que programa no quiere mandarlo ahora, solo dejarlo listo. El archivo ya
        // quedó escrito con su token; el vencimiento lo estira solo el controller de programados
        // (busca el upload por esta misma URL).
        if (req.SoloPreparar) return Ok(new { ok = true, preparado = true, mediaUrl, filename });

        try
        {
            var (sid, canal, lin) = await _outbound.SendMediaAsync(numeroNorm, mediaUrl, req.Caption, filename, req.LineaPhoneId);
            var msg = new WhatsAppTwilioMensaje
            {
                Direccion = "OUTGOING",
                Numero = numeroNorm,
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
