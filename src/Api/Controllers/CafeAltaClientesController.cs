using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Alta de clientes por enlace público: el cliente carga sus datos desde un link (sin login),
/// queda como PENDIENTE, y el operador lo revisa y lo da de alta de verdad.
///
/// Endpoints PÚBLICOS (AllowAnonymous, protegidos por un token compartido):
///   GET  api/cafe/alta-clientes/publica/{token}/init      -> valida token, devuelve nombre del negocio
///   GET  api/cafe/alta-clientes/publica/{token}/padron    -> consulta ARCA por CUIT (autocompleta)
///   POST api/cafe/alta-clientes/publica/{token}           -> el cliente envía su alta (queda pendiente)
///
/// Endpoints de ADMIN (requieren login):
///   GET  api/cafe/alta-clientes/link                      -> devuelve/genera el enlace para compartir
///   POST api/cafe/alta-clientes/regenerar-link            -> genera un token nuevo (invalida el viejo)
///   GET  api/cafe/alta-clientes/pendientes                -> lista de altas pendientes
///   GET  api/cafe/alta-clientes/pendientes/count          -> cantidad pendiente (para la campanita)
///   POST api/cafe/alta-clientes/{id}/aprobar              -> crea el cliente real
///   POST api/cafe/alta-clientes/{id}/rechazar             -> descarta la solicitud
/// </summary>
[ApiController]
[Route("api/cafe/alta-clientes")]
public class CafeAltaClientesController : ControllerBase
{
    private const string TokenKey = "cafe.alta_clientes.token";
    private static readonly string[] IvaValidos = { "CF", "RI", "MO", "EX" };

    private readonly AppDbContext _db;
    private readonly GoogleMapsLinkResolverService _mapsResolver;
    private readonly ArcaPadronService _padron;
    private readonly TelegramService _telegram;

    public CafeAltaClientesController(AppDbContext db, GoogleMapsLinkResolverService mapsResolver,
        ArcaPadronService padron, TelegramService telegram)
    {
        _db = db;
        _mapsResolver = mapsResolver;
        _padron = padron;
        _telegram = telegram;
    }

    // ─────────────────────────── Token compartido ───────────────────────────

    /// <summary>Devuelve el token actual; si no existe todavía, genera uno y lo guarda.</summary>
    private async Task<string> GetOrCreateTokenAsync()
    {
        var s = await _db.AppSettings.FindAsync(TokenKey);
        if (s is not null && !string.IsNullOrWhiteSpace(s.Value)) return s.Value;

        var token = NuevoToken();
        if (s is null)
            _db.AppSettings.Add(new AppSetting { Key = TokenKey, Value = token, UpdatedAt = DateTime.UtcNow });
        else { s.Value = token; s.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
        return token;
    }

    private static string NuevoToken() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")[..8];

    private async Task<bool> TokenValidoAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var s = await _db.AppSettings.FindAsync(TokenKey);
        return s is not null && !string.IsNullOrWhiteSpace(s.Value)
            && string.Equals(s.Value, token, StringComparison.Ordinal);
    }

    // ─────────────────────────── PÚBLICO (cliente) ───────────────────────────

    [HttpGet("publica/{token}/init")]
    [AllowAnonymous]
    public async Task<IActionResult> Init(string token)
    {
        if (!await TokenValidoAsync(token))
            return NotFound(new { ok = false, mensaje = "El enlace no es válido o fue dado de baja. Pedí uno nuevo." });

        var negocio = (await _db.CafeSettings.FirstOrDefaultAsync())?.NegocioNombre;
        if (string.IsNullOrWhiteSpace(negocio))
            negocio = (await _db.AppSettings.FindAsync("BrandName"))?.Value;
        return Ok(new { ok = true, negocioNombre = negocio ?? "" });
    }

    /// <summary>Consulta el padrón ARCA por CUIT para autocompletar razón social, IVA y domicilio.</summary>
    [HttpGet("publica/{token}/padron")]
    [AllowAnonymous]
    public async Task<IActionResult> Padron(string token, [FromQuery] string cuit)
    {
        if (!await TokenValidoAsync(token))
            return NotFound(new { ok = false });
        if (string.IsNullOrWhiteSpace(cuit))
            return BadRequest(new { ok = false, error = "Falta el CUIT o DNI" });
        // Acepta CUIT (11 díg) o DNI (7-8 díg): si es DNI, resuelve el CUIT real contra el padrón.
        var result = await _padron.ConsultarFlexibleAsync(cuit.Trim());
        return Ok(result);
    }

    public class AltaPublicaRequest
    {
        public string? NombreFantasia { get; set; }
        public string? RazonSocial { get; set; }
        public string? Cuit { get; set; }
        public string? CondicionIva { get; set; }
        public string? ContactoNombre { get; set; }
        public string? Telefono { get; set; }
        public string? Email { get; set; }
        public string? DireccionFiscal { get; set; }
        public string? Direccion { get; set; }
        public string? EntreCalles { get; set; }
        public string? Localidad { get; set; }
        public string? MapeoLink { get; set; }
        public string? Comentarios { get; set; }
    }

    [HttpPost("publica/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> Enviar(string token, [FromBody] AltaPublicaRequest req)
    {
        if (!await TokenValidoAsync(token))
            return NotFound(new { ok = false, mensaje = "El enlace no es válido o fue dado de baja." });

        if (string.IsNullOrWhiteSpace(req.NombreFantasia))
            return BadRequest(new { ok = false, mensaje = "Poné el nombre de tu comercio." });
        if (string.IsNullOrWhiteSpace(req.Telefono))
            return BadRequest(new { ok = false, mensaje = "Poné un teléfono de contacto." });

        var alta = new CafeClienteAlta
        {
            NombreFantasia = req.NombreFantasia.Trim(),
            RazonSocial = Norm(req.RazonSocial),
            Cuit = Norm(req.Cuit),
            CondicionIva = NormIva(req.CondicionIva),
            ContactoNombre = Norm(req.ContactoNombre),
            Telefono = req.Telefono.Trim(),
            Email = Norm(req.Email),
            DireccionFiscal = Norm(req.DireccionFiscal),
            Direccion = Norm(req.Direccion),
            EntreCalles = Norm(req.EntreCalles),
            Localidad = Norm(req.Localidad),
            MapeoLink = Norm(req.MapeoLink),
            Comentarios = Norm(req.Comentarios),
            Estado = "pendiente",
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeClienteAltas.Add(alta);
        await _db.SaveChangesAsync();

        // Aviso integrado a "Mis Alertas": campanita 🔔 + historial + Telegram (configurable).
        // Nunca rompe el envío del cliente si algo del aviso falla.
        try { await NotificarNuevaAltaAsync(alta); } catch { }

        return Ok(new { ok = true, mensaje = "¡Listo! Recibimos tus datos. Te vamos a dar de alta en breve." });
    }

    // ─────────────────────────── ADMIN ───────────────────────────

    [HttpGet("link")]
    [Authorize]
    public async Task<IActionResult> GetLink()
    {
        var token = await GetOrCreateTokenAsync();
        return Ok(new { token, ruta = $"/alta-cliente/{token}" });
    }

    [HttpPost("regenerar-link")]
    [Authorize]
    public async Task<IActionResult> RegenerarLink()
    {
        var s = await _db.AppSettings.FindAsync(TokenKey);
        var token = NuevoToken();
        if (s is null)
            _db.AppSettings.Add(new AppSetting { Key = TokenKey, Value = token, UpdatedAt = DateTime.UtcNow });
        else { s.Value = token; s.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
        return Ok(new { token, ruta = $"/alta-cliente/{token}" });
    }

    [HttpGet("pendientes")]
    [Authorize]
    public async Task<IActionResult> Pendientes()
    {
        var list = await _db.CafeClienteAltas
            .Where(a => a.Estado == "pendiente")
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();
        return Ok(list);
    }

    [HttpGet("pendientes/count")]
    [Authorize]
    public async Task<IActionResult> PendientesCount()
    {
        var n = await _db.CafeClienteAltas.CountAsync(a => a.Estado == "pendiente");
        return Ok(new { count = n });
    }

    public class AprobarRequest
    {
        // El operador puede corregir los datos antes de dar de alta.
        public string? NombreFantasia { get; set; }
        public string? RazonSocial { get; set; }
        public string? Cuit { get; set; }
        public string? CondicionIva { get; set; }
        public string? ContactoNombre { get; set; }
        public string? Telefono { get; set; }
        public string? Email { get; set; }
        public string? DireccionFiscal { get; set; }
        public string? Direccion { get; set; }
        public string? EntreCalles { get; set; }
        public string? Localidad { get; set; }
        public string? MapeoLink { get; set; }
        public string? Comentarios { get; set; }
        public string? Tipo { get; set; }          // BAR | OTRO
        public string? Operador { get; set; }
    }

    [HttpPost("{id:int}/aprobar")]
    [Authorize]
    public async Task<IActionResult> Aprobar(int id, [FromBody] AprobarRequest req)
    {
        var alta = await _db.CafeClienteAltas.FindAsync(id);
        if (alta is null) return NotFound(new { error = "No se encontró la solicitud" });
        if (alta.Estado == "aprobado")
            return BadRequest(new { error = "Esta solicitud ya fue dada de alta" });

        var nombre = Norm(req.NombreFantasia) ?? alta.NombreFantasia;
        if (string.IsNullOrWhiteSpace(nombre))
            return BadRequest(new { error = "El nombre es obligatorio" });

        var tipo = string.Equals(req.Tipo, "BAR", StringComparison.OrdinalIgnoreCase) ? "BAR" : "OTRO";

        // Junta el nombre de contacto y los comentarios del cliente en las Notas internas.
        var contacto = Norm(req.ContactoNombre) ?? alta.ContactoNombre;
        var comentarios = Norm(req.Comentarios) ?? alta.Comentarios;
        var notas = string.Join("\n", new[]
        {
            string.IsNullOrWhiteSpace(contacto) ? null : $"Contacto: {contacto}",
            string.IsNullOrWhiteSpace(comentarios) ? null : $"Comentario del cliente: {comentarios}"
        }.Where(x => x != null));

        // Domicilio fiscal (para facturar) y de entrega (dónde recibe) son distintos:
        //  - fiscal  = lo que trajo ARCA (DireccionFiscal). Si no hay, usa el de entrega.
        //  - entrega = lo que escribió el cliente (Direccion). Si no hay, usa el fiscal.
        var fiscal = Norm(req.DireccionFiscal) ?? alta.DireccionFiscal;
        var entrega = Norm(req.Direccion) ?? alta.Direccion;

        var cliente = new CafeCliente
        {
            Codigo = await GenerarCodigoAsync(),
            Nombre = nombre.Trim(),
            RazonSocial = Norm(req.RazonSocial) ?? alta.RazonSocial,
            Tipo = tipo,
            Cuit = Norm(req.Cuit) ?? alta.Cuit,
            Telefono = Norm(req.Telefono) ?? alta.Telefono,
            Email = Norm(req.Email) ?? alta.Email,
            Direccion = fiscal ?? entrega,
            EntreCalles = Norm(req.EntreCalles) ?? alta.EntreCalles,
            Localidad = Norm(req.Localidad) ?? alta.Localidad,
            CondicionIvaDefault = NormIva(req.CondicionIva) ?? alta.CondicionIva,
            DomicilioEntrega = entrega ?? fiscal,
            MapeoLink = Norm(req.MapeoLink) ?? alta.MapeoLink,
            Notas = string.IsNullOrWhiteSpace(notas) ? null : notas,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        if (!string.IsNullOrEmpty(cliente.MapeoLink))
        {
            try
            {
                var coords = await _mapsResolver.TryResolverCoordenadasAsync(cliente.MapeoLink);
                if (coords.HasValue) { cliente.MapeoLat = coords.Value.lat; cliente.MapeoLng = coords.Value.lng; }
            }
            catch { /* si el link no resuelve, se guarda igual sin coords */ }
        }

        _db.CafeClientes.Add(cliente);
        await _db.SaveChangesAsync();

        alta.Estado = "aprobado";
        alta.ClienteIdCreado = cliente.Id;
        alta.ProcesadoPor = Norm(req.Operador);
        alta.ProcesadoAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { ok = true, clienteId = cliente.Id, codigo = cliente.Codigo });
    }

    public class MarcarCargadaRequest
    {
        public int ClienteId { get; set; }
        public string? Operador { get; set; }
    }

    /// <summary>Marca la solicitud como ya cargada (cuando el operador la terminó de cargar
    /// desde la pantalla de Clientes con el modal normal). No crea el cliente — ya lo creó
    /// el flujo de Clientes; acá solo la sacamos de "pendientes" y la enlazamos al cliente creado.</summary>
    [HttpPost("{id:int}/marcar-cargada")]
    [Authorize]
    public async Task<IActionResult> MarcarCargada(int id, [FromBody] MarcarCargadaRequest req)
    {
        var alta = await _db.CafeClienteAltas.FindAsync(id);
        if (alta is null) return NotFound(new { error = "No se encontró la solicitud" });
        if (alta.Estado != "aprobado")
        {
            alta.Estado = "aprobado";
            alta.ClienteIdCreado = req.ClienteId > 0 ? req.ClienteId : (int?)null;
            alta.ProcesadoPor = Norm(req.Operador);
            alta.ProcesadoAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        return Ok(new { ok = true });
    }

    public class RechazarRequest
    {
        public string? Motivo { get; set; }
        public string? Operador { get; set; }
    }

    [HttpPost("{id:int}/rechazar")]
    [Authorize]
    public async Task<IActionResult> Rechazar(int id, [FromBody] RechazarRequest req)
    {
        var alta = await _db.CafeClienteAltas.FindAsync(id);
        if (alta is null) return NotFound(new { error = "No se encontró la solicitud" });
        if (alta.Estado == "aprobado")
            return BadRequest(new { error = "Esta solicitud ya fue dada de alta" });
        alta.Estado = "rechazado";
        alta.MotivoRechazo = Norm(req.Motivo);
        alta.ProcesadoPor = Norm(req.Operador);
        alta.ProcesadoAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Dispara el aviso "ALTA_CLIENTE" en Mis Alertas: campanita (si está tildada),
    /// fila en el historial y Telegram (si está tildado). Mismo mecanismo que FICHADA/VENTA_MELI.</summary>
    private async Task NotificarNuevaAltaAsync(CafeClienteAlta alta)
    {
        var alerta = await _db.MisAlertas.FirstOrDefaultAsync(x => x.Tipo == "ALTA_CLIENTE");
        if (alerta is null || !alerta.Activa) return;
        if (!alerta.CanalCampanita && !alerta.CanalTelegram) return;

        var detalle = $"{alta.NombreFantasia}" +
                      (string.IsNullOrWhiteSpace(alta.Localidad) ? "" : $" · {alta.Localidad}") +
                      $" · Tel {alta.Telefono}";

        // Telegram (si está tildado y el bot está vinculado).
        bool enviadoTg = false;
        if (alerta.CanalTelegram)
        {
            var texto = $"🆕 <b>Nuevo cliente para dar de alta</b>\n" +
                        $"🏪 {alta.NombreFantasia}\n" +
                        (string.IsNullOrWhiteSpace(alta.Cuit) ? "" : $"🧾 CUIT {alta.Cuit}\n") +
                        $"📞 {alta.Telefono}\n" +
                        (string.IsNullOrWhiteSpace(alta.Localidad) ? "" : $"📍 {alta.Localidad}\n") +
                        $"Revisalo en Café → Altas de clientes.";
            var (ok, _) = await _telegram.SendMessageAsync(texto);
            enviadoTg = ok;
        }

        // Campanita (si está tildada): queda encendida hasta que la mirás.
        if (alerta.CanalCampanita)
        {
            alerta.EstaDisparada = true;
            alerta.Vista = false;
            alerta.DisparadaAt = DateTime.UtcNow;
            alerta.UltimoDetalle = detalle;
            alerta.UpdatedAt = DateTime.UtcNow;
        }

        // Historial: una fila por alta recibida.
        _db.MisAlertasHistorial.Add(new MisAlertaHistorial
        {
            AlertaId = alerta.Id,
            Tipo = "ALTA_CLIENTE",
            Mensaje = string.IsNullOrWhiteSpace(alerta.Mensaje) ? "Nuevo cliente para dar de alta" : alerta.Mensaje,
            Detalle = detalle,
            Alcance = string.IsNullOrWhiteSpace(alerta.Alcance) ? "admin,oficina" : alerta.Alcance,
            PorTelegram = alerta.CanalTelegram,
            EnviadoTelegram = enviadoTg
        });
        await _db.SaveChangesAsync();
    }

    // ─────────────────────────── helpers ───────────────────────────

    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? NormIva(string? s)
    {
        var v = s?.Trim().ToUpperInvariant();
        return !string.IsNullOrEmpty(v) && IvaValidos.Contains(v) ? v : null;
    }

    /// <summary>Siguiente código secuencial de cliente (mismo criterio que CafeClientesController).</summary>
    private async Task<string> GenerarCodigoAsync()
    {
        var codigos = await _db.CafeClientes
            .Where(c => c.Codigo != null)
            .Select(c => c.Codigo!)
            .ToListAsync();
        int max = 0;
        foreach (var s in codigos)
            if (int.TryParse(s, out var n) && n > max) max = n;
        var siguiente = max + 1;
        return siguiente < 10000 ? siguiente.ToString("D4") : siguiente.ToString();
    }
}
