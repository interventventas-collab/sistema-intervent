using System.Security.Cryptography;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-11-18: botón "Listas" de la barra de arriba (pedido del dueño). Junta las listas de precios
/// personalizadas (PDF armado en el momento, con precios de HOY) y los catálogos subidos (los mismos
/// del WhatsApp del sistema) para verlos, descargarlos, mandarlos por mail o compartir un link.
/// No guarda nada nuevo: reusa el PDF de CafeListasCustomController, los archivos de
/// WhatsApp_Catalogos y el envío de mail de EnvioComprobanteService (config SMTP de Integraciones).
/// </summary>
[ApiController]
[Route("api/listas-catalogos")]
[Authorize]
public class ListasCatalogosController : ControllerBase
{
    private const string UploadsDir = "/data/whatsapp-uploads";
    private readonly AppDbContext _db;
    private readonly CafeListasCustomController _listas;
    private readonly EnvioComprobanteService _envio;
    private readonly ILogger<ListasCatalogosController> _log;

    public ListasCatalogosController(AppDbContext db, CafeListasCustomController listas, EnvioComprobanteService envio,
        ILogger<ListasCatalogosController> log)
    {
        _db = db; _listas = listas; _envio = envio; _log = log;
    }

    public record ItemDto(string Tipo, int Id, string Nombre, string Detalle, DateTime Fecha, string Extension);
    public record TodoDto(List<ItemDto> Listas, List<ItemDto> Catalogos);

    /// <summary>GET /api/listas-catalogos — las listas activas y los catálogos subidos.</summary>
    [HttpGet]
    public async Task<ActionResult<TodoDto>> Todo()
    {
        var listas = await _db.CafeListasPreciosCustom.Include(l => l.ClienteNav)
            .Where(l => l.IsActive).OrderBy(l => l.Nombre)
            .Select(l => new { l.Id, l.Nombre, l.NumeroLista, l.TipoCliente, Cliente = l.ClienteNav != null ? l.ClienteNav.Nombre : null, l.UpdatedAt })
            .ToListAsync();
        var cats = await _db.WhatsAppCatalogos.OrderByDescending(c => c.CreatedAt).ToListAsync();

        return new TodoDto(
            listas.Select(l => new ItemDto("LISTA", l.Id, l.Nombre,
                string.Join(" · ", new[]
                {
                    string.IsNullOrWhiteSpace(l.NumeroLista) ? null : $"Lista N° {l.NumeroLista}",
                    l.Cliente != null ? $"Cliente: {l.Cliente}" : (string.IsNullOrWhiteSpace(l.TipoCliente) ? "General" : l.TipoCliente),
                }.Where(x => x != null)),
                l.UpdatedAt, "pdf")).ToList(),
            cats.Select(c => new ItemDto("CATALOGO", c.Id, NombreSinExtension(c.OriginalFilename),
                Tamano(c.SizeBytes), c.CreatedAt, Extension(c.OriginalFilename, c.ContentType))).ToList());
    }

    /// <summary>GET /api/listas-catalogos/{tipo}/{id}/archivo?descargar=1 — sin descargar se abre en el navegador.</summary>
    [HttpGet("{tipo}/{id:int}/archivo")]
    public async Task<IActionResult> Archivo(string tipo, int id, [FromQuery] int descargar = 0)
    {
        var a = await TraerAsync(tipo, id);
        if (a is null) return NotFound(new { error = "No se encontró" });
        if (descargar == 1) return File(a.Value.Bytes, a.Value.ContentType, a.Value.Nombre);
        Response.Headers["Content-Disposition"] = $"inline; filename*=UTF-8''{Uri.EscapeDataString(a.Value.Nombre)}";
        return File(a.Value.Bytes, a.Value.ContentType);
    }

    public record MailReq(string Para, string? Mensaje);

    /// <summary>POST /api/listas-catalogos/{tipo}/{id}/mail — manda el archivo adjunto.</summary>
    [HttpPost("{tipo}/{id:int}/mail")]
    public async Task<IActionResult> Mail(string tipo, int id, [FromBody] MailReq req)
    {
        var para = (req.Para ?? "").Trim();
        if (!System.Net.Mail.MailAddress.TryCreate(para, out _))
            return BadRequest(new { error = "Ese mail no parece válido." });
        var a = await TraerAsync(tipo, id);
        if (a is null) return NotFound(new { error = "No se encontró" });

        var esLista = tipo.Equals("LISTA", StringComparison.OrdinalIgnoreCase);
        var asunto = esLista ? $"Lista de precios {NombreSinExtension(a.Value.Nombre)}" : $"Catálogo {NombreSinExtension(a.Value.Nombre)}";
        var cuerpo = string.IsNullOrWhiteSpace(req.Mensaje)
            ? (esLista ? "Hola, te paso nuestra lista de precios actualizada." : "Hola, te paso nuestro catálogo.")
            : req.Mensaje.Trim();
        try
        {
            var (ok, err) = await _envio.EnviarEmailConAdjuntoAsync(para, asunto, cuerpo, a.Value.Bytes, a.Value.Nombre,
                contentType: a.Value.ContentType);
            if (!ok) return BadRequest(new { error = err ?? "No se pudo mandar el mail." });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Mail de {Tipo} {Id} a {Para} falló", tipo, id, para);
            return BadRequest(new { error = "El servidor de mail no lo aceptó: " + ex.Message });
        }
        return Ok(new { ok = true });
    }

    /// <summary>POST /api/listas-catalogos/{tipo}/{id}/link — link público que dura 7 días.
    /// Usa la misma tabla/carpeta de los adjuntos de WhatsApp (se sirve en /api/whatsapp/twilio/files/...).</summary>
    [HttpPost("{tipo}/{id:int}/link")]
    public async Task<IActionResult> Link(string tipo, int id)
    {
        var a = await TraerAsync(tipo, id);
        if (a is null) return NotFound(new { error = "No se encontró" });
        Directory.CreateDirectory(UploadsDir);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').Replace("=", "");
        var ext = Path.GetExtension(a.Value.Nombre);
        var stored = token + ext;
        await System.IO.File.WriteAllBytesAsync(Path.Combine(UploadsDir, stored), a.Value.Bytes);
        _db.WhatsAppTwilioUploads.Add(new WhatsAppTwilioUpload
        {
            Token = token,
            OriginalFilename = a.Value.Nombre,
            StoredFilename = stored,
            ContentType = a.Value.ContentType,
            SizeBytes = a.Value.Bytes.Length,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
        });
        await _db.SaveChangesAsync();
        return Ok(new { url = $"{Request.Scheme}://{Request.Host}/api/whatsapp/twilio/files/{token}{ext}" });
    }

    public record MailSugeridoDto(string Cliente, string Email);

    /// <summary>GET /api/listas-catalogos/mails?q= — clientes con mail cargado en la ficha.</summary>
    [HttpGet("mails")]
    public async Task<List<MailSugeridoDto>> Mails([FromQuery] string? q)
    {
        var s = (q ?? "").Trim();
        if (s.Length < 2) return new();
        return await _db.CafeClientes
            .Where(c => c.IsActive && c.Email != null && c.Email != ""
                        && (c.Nombre.Contains(s) || (c.RazonSocial != null && c.RazonSocial.Contains(s)) || c.Email.Contains(s)))
            .OrderBy(c => c.Nombre).Take(8)
            .Select(c => new MailSugeridoDto(c.Nombre, c.Email!))
            .ToListAsync();
    }

    // ───────── helpers ─────────

    private async Task<(byte[] Bytes, string Nombre, string ContentType)?> TraerAsync(string tipo, int id)
    {
        if (tipo.Equals("LISTA", StringComparison.OrdinalIgnoreCase))
        {
            var (bytes, nombre) = await _listas.GenerarPdfBytesAsync(id);
            if (bytes is null) return null;
            return (bytes, string.IsNullOrWhiteSpace(nombre) ? $"Lista {id}.pdf" : nombre, "application/pdf");
        }
        if (tipo.Equals("CATALOGO", StringComparison.OrdinalIgnoreCase))
        {
            var c = await _db.WhatsAppCatalogos.FirstOrDefaultAsync(x => x.Id == id);
            if (c is null) return null;
            var path = Path.Combine(UploadsDir, c.StoredFilename);
            if (!System.IO.File.Exists(path)) return null;
            return (await System.IO.File.ReadAllBytesAsync(path), LimpiarNombre(c.OriginalFilename),
                    string.IsNullOrWhiteSpace(c.ContentType) ? "application/octet-stream" : c.ContentType);
        }
        return null;
    }

    /// <summary>"cremuccino FKF .pdf.pdf" → "cremuccino FKF.pdf".</summary>
    private static string LimpiarNombre(string n)
    {
        var ext = Path.GetExtension(n);
        return NombreSinExtension(n) + ext;
    }

    private static string NombreSinExtension(string n)
    {
        var s = n ?? "";
        while (Path.HasExtension(s) && Path.GetExtension(s).Length <= 5) s = Path.GetFileNameWithoutExtension(s);
        return s.Trim().TrimEnd('.').Trim();
    }

    private static string Extension(string n, string? ct)
    {
        var e = Path.GetExtension(n ?? "").TrimStart('.').ToLowerInvariant();
        if (e != "") return e;
        return ct?.Contains("pdf") == true ? "pdf" : "arch";
    }

    private static string Tamano(long b) => b >= 1024 * 1024
        ? $"{b / 1024d / 1024d:0.0} MB".Replace('.', ',')
        : $"{Math.Max(1, b / 1024)} KB";
}
