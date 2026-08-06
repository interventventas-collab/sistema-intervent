using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-08-05 (Paso 3) — lado PÚBLICO (sin login). El celu del depósito escanea el QR y abre
/// /subir-foto-producto/{token}, saca o elige una foto y la sube. Se guarda como FOTO PROPIA del
/// producto (a nivel sistema; NO toca la foto de MercadoLibre). El token es de un solo uso (30 min).
/// </summary>
[ApiController]
[Route("api/public/producto-foto")]
[AllowAnonymous]
public class ProductoFotoPublicController : ControllerBase
{
    private readonly AppDbContext _db;
    public ProductoFotoPublicController(AppDbContext db) { _db = db; }

    // Vive en el volume files_data (montado en /data/files en dev y prod), así persiste a los rebuilds.
    private const string FotosDir = "/data/files/producto-fotos";

    public record InfoResp(bool Ok, string? ProductoNombre, string? Sku, bool YaSubida, string? FotoUrl, string? Motivo);

    /// <summary>Datos del producto para mostrar en el celu (nombre, sku, si ya tiene foto propia).</summary>
    [HttpGet("{token}")]
    public async Task<IActionResult> Info(string token)
    {
        var t = await _db.CafeProductoFotoTokens.FirstOrDefaultAsync(x => x.Token == token);
        if (t is null) return Ok(new InfoResp(false, null, null, false, null, "no_existe"));
        if (t.ExpiresAt < DateTime.UtcNow) return Ok(new InfoResp(false, null, null, false, null, "vencido"));

        var prod = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == t.CafeProductoId);
        if (prod is null) return Ok(new InfoResp(false, null, null, false, null, "sin_producto"));

        var foto = await _db.CafeProductoFotos.FirstOrDefaultAsync(f => f.CafeProductoId == t.CafeProductoId);
        var url = string.IsNullOrEmpty(foto?.FotoPropiaArchivo) ? null : $"/api/public/producto-foto/img/{foto!.FotoPropiaArchivo}";
        return Ok(new InfoResp(true, prod.Nombre, prod.Sku, !string.IsNullOrEmpty(foto?.FotoPropiaArchivo), url, null));
    }

    /// <summary>Sube la foto desde el celu. Solo imagen, máx 10 MB. Marca el token como usado.</summary>
    [HttpPost("{token}")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> Subir(string token, IFormFile file)
    {
        var t = await _db.CafeProductoFotoTokens.FirstOrDefaultAsync(x => x.Token == token);
        if (t is null) return NotFound(new { error = "Link inválido." });
        if (t.ExpiresAt < DateTime.UtcNow) return BadRequest(new { error = "El link venció. Pedí uno nuevo desde la compu." });
        if (file is null || file.Length == 0) return BadRequest(new { error = "No se recibió ninguna foto." });
        if (file.Length > 10 * 1024 * 1024) return BadRequest(new { error = "La foto es muy grande (máx 10 MB)." });
        if (!file.ContentType.StartsWith("image/")) return BadRequest(new { error = "El archivo tiene que ser una imagen." });

        Directory.CreateDirectory(FotosDir);
        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || ext.Length > 6) ext = ".jpg";
        var filename = $"prod-{t.CafeProductoId}-{Guid.NewGuid():N}{ext}";
        var fullPath = Path.Combine(FotosDir, filename);
        using (var fs = new FileStream(fullPath, FileMode.Create))
            await file.CopyToAsync(fs);

        // Upsert del registro de foto del producto. La foto propia se toma como buena → APROBADA,
        // así se muestra directo (sin ojito) en el tablero de preparación.
        var foto = await _db.CafeProductoFotos.FirstOrDefaultAsync(f => f.CafeProductoId == t.CafeProductoId);
        var archivoViejo = foto?.FotoPropiaArchivo;
        if (foto is null)
        {
            foto = new CafeProductoFoto { CafeProductoId = t.CafeProductoId };
            _db.CafeProductoFotos.Add(foto);
        }
        foto.FotoPropiaArchivo = filename;
        foto.FotoPropiaAt = DateTime.UtcNow;
        foto.Estado = "APROBADA";
        foto.Comentario = null;
        foto.UpdatedAt = DateTime.UtcNow;

        t.UsedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Borramos la foto vieja del disco (si había una propia anterior) para no acumular basura.
        if (!string.IsNullOrEmpty(archivoViejo) && archivoViejo != filename)
        {
            try { var old = Path.Combine(FotosDir, archivoViejo); if (System.IO.File.Exists(old)) System.IO.File.Delete(old); }
            catch { /* best-effort */ }
        }

        return Ok(new { ok = true });
    }

    /// <summary>Sirve la imagen guardada. Pública (la usan el celu y el tablero).</summary>
    [HttpGet("img/{filename}")]
    public IActionResult Img(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename.Contains("..") || filename.Contains("/") || filename.Contains("\\"))
            return NotFound();
        var path = Path.Combine(FotosDir, filename);
        if (!System.IO.File.Exists(path)) return NotFound();
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        var contentType = ext switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
        return PhysicalFile(path, contentType);
    }
}
