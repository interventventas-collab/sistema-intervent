using System.Text.Json;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// Imagenes de marca (logos, fondos de cards, etc) que el usuario sube
/// desde la app y persisten en el volume Docker /data/files/branding/.
/// La idea es que NO haya que tocar ningun path manualmente — todo sube
/// y baja por API.
///
/// Cada imagen se identifica por una "key" (ej: "cafe-frikaf-bg", "logo-frikaf").
/// Si la key se sube de nuevo, se reemplaza. Si no existe, GET devuelve 404
/// y el frontend cae al estilo por default.
/// </summary>
[ApiController]
[Route("api/branding")]
[Authorize]
public class BrandingController : ControllerBase
{
    private readonly AuditLogService _audit;
    // Carpeta dentro del volume files_data que ya esta montado en /data/files
    private static readonly string BrandingRoot = "/data/files/branding";
    private static readonly string[] AllowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
    private const long MaxBytes = 10L * 1024 * 1024; // 10 MB

    public BrandingController(AuditLogService audit) { _audit = audit; }

    /// <summary>
    /// Sube/reemplaza una imagen de marca. Borra la anterior si tenia otra extension.
    /// La key se sanitiza para que sea solo letras, numeros y guiones (sin / .. etc).
    /// </summary>
    [HttpPost("upload/{key}")]
    [RequestSizeLimit(20L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 20L * 1024 * 1024)]
    public async Task<IActionResult> Upload(string key, IFormFile file)
    {
        var safeKey = SanitizeKey(key);
        if (string.IsNullOrEmpty(safeKey)) return BadRequest(new { error = "Key invalida" });
        if (file is null || file.Length == 0) return BadRequest(new { error = "No se envio ningun archivo" });
        if (file.Length > MaxBytes) return BadRequest(new { error = $"Archivo demasiado grande (max {MaxBytes / 1024 / 1024} MB)" });

        var ext = Path.GetExtension(file.FileName)?.ToLowerInvariant() ?? "";
        if (!AllowedExtensions.Contains(ext))
            return BadRequest(new { error = $"Formato no permitido. Usa: {string.Join(", ", AllowedExtensions)}" });

        Directory.CreateDirectory(BrandingRoot);

        // Borrar archivos viejos con la misma key (cualquier extension)
        foreach (var existing in Directory.EnumerateFiles(BrandingRoot, $"{safeKey}.*"))
        {
            try { System.IO.File.Delete(existing); } catch { /* siguiente */ }
        }

        var dest = Path.Combine(BrandingRoot, $"{safeKey}{ext}");
        await using (var fs = System.IO.File.Create(dest))
        {
            await file.CopyToAsync(fs);
        }

        await _audit.LogAsync("Branding", safeKey, "BRANDING_UPLOAD",
            JsonSerializer.Serialize(new { size = file.Length, ext }), null);

        return Ok(new { ok = true, key = safeKey, size = file.Length, ext, url = $"/api/branding/{safeKey}" });
    }

    /// <summary>Sirve la imagen guardada para una key, o 404 si no existe.</summary>
    [HttpGet("{key}")]
    [AllowAnonymous]  // Permite usar la URL como background-image desde el navegador sin auth header
    public IActionResult Get(string key)
    {
        var safeKey = SanitizeKey(key);
        if (string.IsNullOrEmpty(safeKey)) return NotFound();
        if (!Directory.Exists(BrandingRoot)) return NotFound();

        var match = Directory.EnumerateFiles(BrandingRoot, $"{safeKey}.*").FirstOrDefault();
        if (match is null) return NotFound();

        var ext = Path.GetExtension(match).ToLowerInvariant();
        var mime = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "application/octet-stream"
        };

        // Cache corto en el navegador para que se note el cambio rapido tras un upload
        Response.Headers["Cache-Control"] = "public, max-age=10";
        return PhysicalFile(match, mime);
    }

    [HttpDelete("{key}")]
    public async Task<IActionResult> Delete(string key)
    {
        var safeKey = SanitizeKey(key);
        if (string.IsNullOrEmpty(safeKey)) return BadRequest(new { error = "Key invalida" });
        if (!Directory.Exists(BrandingRoot)) return NotFound();

        var any = false;
        foreach (var f in Directory.EnumerateFiles(BrandingRoot, $"{safeKey}.*"))
        {
            try { System.IO.File.Delete(f); any = true; } catch { }
        }
        if (any)
            await _audit.LogAsync("Branding", safeKey, "BRANDING_DELETE", null, null);
        return any ? Ok(new { ok = true }) : NotFound();
    }

    private static string SanitizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        var allowed = key.Trim().ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
            .ToArray();
        return new string(allowed);
    }
}
