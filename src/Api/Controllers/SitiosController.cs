using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>2026-06-19: Marcas / Sitios. CRUD admin de marcas con dominios, logo y datos
/// de contacto. Endpoints publicos para que las landings estaticas (frikaf, etc.)
/// resuelvan su data por host. Reemplaza al FrikafController anterior (que solo
/// guardaba urls de imagenes por sku).</summary>
[ApiController]
[Route("api")]
public class SitiosController : ControllerBase
{
    private const string LogoFolder = "/srv/landing-frikaf/img";

    private readonly AppDbContext _db;
    public SitiosController(AppDbContext db) { _db = db; }

    public record SitioDto(int Id, string Nombre, string Slug, string? Dominios,
        string? LogoUrl, string? Eyebrow, string? Frase,
        string? WhatsApp, string? WhatsApp2, string? Instagram, string? Facebook,
        string? ColorPrimario, string? ColorAcento, bool IsActive);

    public record SitioUpsertRequest(string Nombre, string Slug, string? Dominios,
        string? LogoUrl, string? Eyebrow, string? Frase,
        string? WhatsApp, string? WhatsApp2, string? Instagram, string? Facebook,
        string? ColorPrimario, string? ColorAcento, bool IsActive);

    private static SitioDto Map(CafeSitio s) => new(
        s.Id, s.Nombre, s.Slug, s.Dominios, s.LogoUrl, s.Eyebrow, s.Frase,
        s.WhatsApp, s.WhatsApp2, s.Instagram, s.Facebook,
        s.ColorPrimario, s.ColorAcento, s.IsActive);

    // ---------- Admin endpoints ----------
    [HttpGet("sitios")]
    [Authorize]
    public async Task<IActionResult> List()
    {
        var rows = await _db.CafeSitios.OrderBy(x => x.Nombre).ToListAsync();
        return Ok(rows.Select(Map));
    }

    [HttpGet("sitios/{id:int}")]
    [Authorize]
    public async Task<IActionResult> Get(int id)
    {
        var s = await _db.CafeSitios.FindAsync(id);
        return s is null ? NotFound() : Ok(Map(s));
    }

    [HttpPost("sitios")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Create([FromBody] SitioUpsertRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Nombre) || string.IsNullOrWhiteSpace(r.Slug))
            return BadRequest(new { error = "Nombre y slug obligatorios" });
        var slug = r.Slug.Trim().ToLowerInvariant();
        if (await _db.CafeSitios.AnyAsync(x => x.Slug == slug))
            return Conflict(new { error = "Slug ya existe" });

        var sitio = new CafeSitio
        {
            Nombre = r.Nombre.Trim(),
            Slug = slug,
            Dominios = string.IsNullOrWhiteSpace(r.Dominios) ? null : r.Dominios.Trim(),
            LogoUrl = r.LogoUrl,
            Eyebrow = r.Eyebrow,
            Frase = r.Frase,
            WhatsApp = r.WhatsApp,
            WhatsApp2 = r.WhatsApp2,
            Instagram = r.Instagram,
            Facebook = r.Facebook,
            ColorPrimario = r.ColorPrimario,
            ColorAcento = r.ColorAcento,
            IsActive = r.IsActive
        };
        _db.CafeSitios.Add(sitio);
        await _db.SaveChangesAsync();
        return Ok(Map(sitio));
    }

    [HttpPut("sitios/{id:int}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Update(int id, [FromBody] SitioUpsertRequest r)
    {
        var s = await _db.CafeSitios.FindAsync(id);
        if (s is null) return NotFound();
        if (string.IsNullOrWhiteSpace(r.Nombre) || string.IsNullOrWhiteSpace(r.Slug))
            return BadRequest(new { error = "Nombre y slug obligatorios" });
        var slug = r.Slug.Trim().ToLowerInvariant();
        if (await _db.CafeSitios.AnyAsync(x => x.Slug == slug && x.Id != id))
            return Conflict(new { error = "Slug ya existe en otro sitio" });

        s.Nombre = r.Nombre.Trim();
        s.Slug = slug;
        s.Dominios = string.IsNullOrWhiteSpace(r.Dominios) ? null : r.Dominios.Trim();
        s.LogoUrl = r.LogoUrl;
        s.Eyebrow = r.Eyebrow;
        s.Frase = r.Frase;
        s.WhatsApp = r.WhatsApp;
        s.WhatsApp2 = r.WhatsApp2;
        s.Instagram = r.Instagram;
        s.Facebook = r.Facebook;
        s.ColorPrimario = r.ColorPrimario;
        s.ColorAcento = r.ColorAcento;
        s.IsActive = r.IsActive;
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(s));
    }

    [HttpDelete("sitios/{id:int}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var s = await _db.CafeSitios.FindAsync(id);
        if (s is null) return NotFound();
        _db.CafeSitios.Remove(s);
        await _db.SaveChangesAsync();
        return Ok();
    }

    // ---------- Upload de imagen (logo, foto, lo que sea) ----------
    [HttpPost("sitios/upload")]
    [Authorize]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> Upload([FromForm] IFormFile file, [FromForm] string? slug)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Archivo vacio" });
        var allowed = new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg" };
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowed.Contains(ext))
            return BadRequest(new { error = "Formato no permitido. Use PNG, JPG, WEBP, GIF o SVG." });

        Directory.CreateDirectory(LogoFolder);
        var safeSlug = string.IsNullOrWhiteSpace(slug) ? "img" : new string(slug.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        var ts = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var filename = $"{safeSlug}-{ts}{ext}";
        var path = Path.Combine(LogoFolder, filename);
        using (var fs = System.IO.File.Create(path))
            await file.CopyToAsync(fs);

        // URL relativa que Caddy sirve desde el bloque de cada dominio Frikaf,
        // ademas la exponemos en /uploads del dominio admin (handle nuevo en Caddyfile).
        return Ok(new { url = $"/uploads/{filename}", filename });
    }

    [HttpGet("sitios/uploads")]
    [Authorize]
    public IActionResult ListUploads()
    {
        if (!Directory.Exists(LogoFolder)) return Ok(Array.Empty<object>());
        var files = new DirectoryInfo(LogoFolder).GetFiles()
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new { url = $"/uploads/{f.Name}", filename = f.Name, sizeKb = f.Length / 1024, created = f.CreationTimeUtc });
        return Ok(files);
    }

    [HttpDelete("sitios/uploads/{filename}")]
    [Authorize(Roles = "admin")]
    public IActionResult DeleteUpload(string filename)
    {
        var safe = Path.GetFileName(filename);
        var path = Path.Combine(LogoFolder, safe);
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        return Ok();
    }

    // ---------- Endpoint publico para que la landing pregunte "quien soy" ----------
    [HttpGet("sitios/by-host/{host}")]
    [AllowAnonymous]
    public async Task<IActionResult> ByHost(string host)
    {
        host = host.Trim().ToLowerInvariant();
        if (host.StartsWith("www.")) host = host.Substring(4);
        var rows = await _db.CafeSitios.Where(s => s.IsActive).ToListAsync();
        var match = rows.FirstOrDefault(s =>
            !string.IsNullOrEmpty(s.Dominios) &&
            s.Dominios.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim().ToLowerInvariant())
                .Any(d => d == host || d == $"www.{host}"));
        if (match is null) return NotFound();
        return Ok(Map(match));
    }

    // ---------- Compat: URLs personalizadas por SKU (heredado del FrikafController) ----------
    private const string CustomUrlsFile = "/srv/landing-frikaf/custom-urls.json";

    public record UrlUpdateRequest(string Sku, string? Url);

    [HttpGet("frikaf/urls")]
    [AllowAnonymous]
    public IActionResult GetUrls()
    {
        if (!System.IO.File.Exists(CustomUrlsFile)) return Content("{}", "application/json");
        return Content(System.IO.File.ReadAllText(CustomUrlsFile), "application/json");
    }

    [HttpPost("frikaf/urls")]
    [AllowAnonymous]
    public IActionResult SetUrl([FromBody] UrlUpdateRequest req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Sku))
            return BadRequest(new { error = "SKU vacio" });
        Dictionary<string, string> dict;
        try
        {
            dict = System.IO.File.Exists(CustomUrlsFile)
                ? (JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(CustomUrlsFile)) ?? new())
                : new();
        }
        catch { dict = new(); }

        var sku = req.Sku.Trim();
        if (string.IsNullOrWhiteSpace(req.Url)) dict.Remove(sku);
        else dict[sku] = req.Url.Trim();

        var dir = Path.GetDirectoryName(CustomUrlsFile);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(CustomUrlsFile, JsonSerializer.Serialize(dict));
        return Ok(new { count = dict.Count });
    }
}
