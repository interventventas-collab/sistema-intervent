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
            var (sid, canal) = await _outbound.SendTextAsync(numero, texto);
            _db.WhatsAppTwilioMensajes.Add(new WhatsAppTwilioMensaje
            {
                Direccion = "OUTGOING",
                Numero = numero,
                Cuerpo = texto,
                TwilioMessageSid = sid,
                Canal = canal,
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
        var sid = await EnviarYRegistrarAsync(numero, MenuRolTexto);
        if (sid == null) return StatusCode(500, new { error = "No se pudo enviar el menú (ver logs)" });
        return Ok(new { ok = true, sid });
    }

    public record SendRequest(string Numero, string Mensaje);

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
            var (sid, canal) = await _outbound.SendTextAsync(req.Numero, req.Mensaje);
            var msg = new WhatsAppTwilioMensaje
            {
                Direccion = "OUTGOING",
                Numero = req.Numero,
                Cuerpo = req.Mensaje,
                TwilioMessageSid = sid,
                Canal = canal,
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

    /// <summary>GET /api/whatsapp/twilio/conversaciones — lista numeros agrupados con ultimo mensaje.
    /// Si el numero esta en WhatsApp_TwilioContactos, devuelve NombreContacto + Rol (prevalece sobre NombrePerfil de WhatsApp).</summary>
    [HttpGet("conversaciones")]
    [Authorize]
    public async Task<IActionResult> Conversaciones()
    {
        var conv = await _db.WhatsAppTwilioMensajes
            .AsNoTracking()
            .GroupBy(m => m.Numero)
            .Select(g => new
            {
                Numero = g.Key,
                NombrePerfil = g.OrderByDescending(m => m.CreatedAt).Where(m => m.Direccion == "INCOMING").Select(m => m.NombrePerfil).FirstOrDefault(),
                UltimoMensaje = g.OrderByDescending(m => m.CreatedAt).Select(m => m.Cuerpo).FirstOrDefault(),
                UltimoDireccion = g.OrderByDescending(m => m.CreatedAt).Select(m => m.Direccion).FirstOrDefault(),
                UltimoAt = g.Max(m => m.CreatedAt),
                Total = g.Count()
            })
            .ToListAsync();
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
                x.Total
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
                // Al quitar mandamos emoji vacio (Meta la saca del celu del cliente)
                var sid = await _meta.SendReactionAsync(msg.Numero, msg.TwilioMessageSid, removed ? "" : req.Emoji);
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
    public async Task<IActionResult> Mensajes([FromQuery] string? numero, [FromQuery] int top = 200)
    {
        var q = _db.WhatsAppTwilioMensajes.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(numero)) q = q.Where(m => m.Numero == numero);
        var msgs = await q
            .OrderByDescending(m => m.CreatedAt)
            .Take(Math.Clamp(top, 1, 500))
            .Select(m => new
            {
                m.Id, m.Direccion, m.Numero, m.NombrePerfil,
                m.Cuerpo, m.MediaUrl, m.NumMedia,
                m.Procesado, m.RespuestaEnviada, m.CreatedAt
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
            m.MediaUrl, m.NumMedia, m.Procesado, m.RespuestaEnviada, m.CreatedAt,
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

        var up = new WhatsAppTwilioUpload
        {
            Token = token,
            OriginalFilename = file.FileName,
            StoredFilename = stored,
            ContentType = string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType,
            SizeBytes = file.Length,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        };
        _db.WhatsAppTwilioUploads.Add(up);
        await _db.SaveChangesAsync();

        // La extension va en la URL para que el chat sepa si mostrar vista previa de imagen.
        var url = $"{Request.Scheme}://{Request.Host}/api/whatsapp/twilio/files/{token}{ext}";
        return Ok(new UploadResp(token, url, up.OriginalFilename, up.SizeBytes, up.ContentType, up.ExpiresAt));
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

    public record SendMediaRequest(string Numero, string MediaUrl, string? Caption, string? OriginalFilename);

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
            var (sid, canal) = await _outbound.SendMediaAsync(req.Numero, req.MediaUrl, req.Caption, req.OriginalFilename);
            var msg = new WhatsAppTwilioMensaje
            {
                Direccion = "OUTGOING",
                Numero = req.Numero,
                Cuerpo = req.Caption ?? "",
                MediaUrl = req.MediaUrl,
                NumMedia = 1,
                TwilioMessageSid = sid,
                Canal = canal,
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
            var list = await q.OrderByDescending(v => v.Fecha).Take(take)
                .Select(v => new ServerFileDto(
                    "VENTA", v.Id, $"{(v.TipoComprobante ?? "X")} {v.Numero}",
                    !string.IsNullOrWhiteSpace(v.ClienteRazonSocialSnapshot) ? v.ClienteRazonSocialSnapshot : (v.ClienteNombreSnapshot ?? "—"),
                    $"${v.Total:N0}",
                    v.Fecha))
                .ToListAsync();
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

    public record SendServerFileRequest(string Numero, string Tipo, int Id, string? Caption);

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
            var (sid, canal) = await _outbound.SendMediaAsync(req.Numero, mediaUrl, req.Caption, filename);
            var msg = new WhatsAppTwilioMensaje
            {
                Direccion = "OUTGOING",
                Numero = req.Numero,
                Cuerpo = req.Caption ?? "",
                MediaUrl = mediaUrl,
                NumMedia = 1,
                TwilioMessageSid = sid,
                Canal = canal,
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
