using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-08-05: estado de la foto de un producto de café A NIVEL SISTEMA.
/// Desde /cafe/preparacion el armador aprueba (✅) o reporta como errónea (❌) la foto de un
/// producto. Es por producto → apenas uno la marca, lo ven todos. NO toca la foto de MeLi.
/// </summary>
[ApiController]
[Route("api/cafe/producto-foto")]
[Authorize]
public class CafeProductoFotoController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafeProductoFotoController(AppDbContext db) { _db = db; }

    // Mismo destino que la subida por QR (volume files_data, persiste a los rebuilds).
    private const string FotosDir = "/data/files/producto-fotos";

    public record MarcarFotoRequest(string? Estado, string? Comentario);
    public record DesdeUrlRequest(string? Url);

    public record ProductoFotoDto(int CafeProductoId, string? Estado, string? Usuario,
        string? Comentario, string? FotoPropiaArchivo, DateTime UpdatedAt);

    public record TokenResp(string Token);

    /// <summary>Devuelve el estado de foto de todos los productos que tienen alguna marca.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar()
    {
        var lista = await _db.CafeProductoFotos
            .Select(f => new ProductoFotoDto(f.CafeProductoId, f.Estado, f.Usuario, f.Comentario, f.FotoPropiaArchivo, f.UpdatedAt))
            .ToListAsync();
        return Ok(lista);
    }

    /// <summary>Estado de la foto de UN producto (para sondear mientras el celu sube por QR).</summary>
    [HttpGet("{productoId:int}")]
    public async Task<IActionResult> Estado(int productoId)
    {
        var f = await _db.CafeProductoFotos.FirstOrDefaultAsync(x => x.CafeProductoId == productoId);
        if (f is null) return Ok(new ProductoFotoDto(productoId, null, null, null, null, DateTime.UtcNow));
        return Ok(new ProductoFotoDto(f.CafeProductoId, f.Estado, f.Usuario, f.Comentario, f.FotoPropiaArchivo, f.UpdatedAt));
    }

    /// <summary>Genera un token de un solo uso para subir la foto de este producto por QR (30 min).</summary>
    [HttpPost("{productoId:int}/token")]
    public async Task<IActionResult> CrearToken(int productoId)
    {
        var existeProd = await _db.CafeProductos.AnyAsync(p => p.Id == productoId);
        if (!existeProd) return NotFound(new { mensaje = "Producto no encontrado." });

        var token = Guid.NewGuid().ToString("N");
        _db.CafeProductoFotoTokens.Add(new CafeProductoFotoToken
        {
            Token = token,
            CafeProductoId = productoId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        });
        await _db.SaveChangesAsync();
        return Ok(new TokenResp(token));
    }

    /// <summary>Guarda bytes como foto propia del producto (APROBADA) y borra la anterior si había.</summary>
    private async Task<string> GuardarFotoPropiaAsync(int productoId, byte[] bytes, string? ext, string? usuario)
    {
        Directory.CreateDirectory(FotosDir);
        if (string.IsNullOrEmpty(ext) || ext.Length > 6) ext = ".jpg";
        var filename = $"prod-{productoId}-{Guid.NewGuid():N}{ext}";
        await System.IO.File.WriteAllBytesAsync(Path.Combine(FotosDir, filename), bytes);

        var foto = await _db.CafeProductoFotos.FirstOrDefaultAsync(f => f.CafeProductoId == productoId);
        var archivoViejo = foto?.FotoPropiaArchivo;
        if (foto is null) { foto = new CafeProductoFoto { CafeProductoId = productoId }; _db.CafeProductoFotos.Add(foto); }
        foto.FotoPropiaArchivo = filename;
        foto.FotoPropiaAt = DateTime.UtcNow;
        foto.Estado = "APROBADA";
        foto.Comentario = null;
        foto.Usuario = usuario;
        foto.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        if (!string.IsNullOrEmpty(archivoViejo) && archivoViejo != filename)
        {
            try { var old = Path.Combine(FotosDir, archivoViejo); if (System.IO.File.Exists(old)) System.IO.File.Delete(old); }
            catch { /* best-effort */ }
        }
        return filename;
    }

    /// <summary>Sube la foto propia DIRECTO desde la compu (sin QR). Solo imagen, máx 10 MB.</summary>
    [HttpPost("{productoId:int}/subir")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> Subir(int productoId, IFormFile file)
    {
        if (!await _db.CafeProductos.AnyAsync(p => p.Id == productoId)) return NotFound(new { mensaje = "Producto no encontrado." });
        if (file is null || file.Length == 0) return BadRequest(new { mensaje = "No se recibió ninguna foto." });
        if (file.Length > 10 * 1024 * 1024) return BadRequest(new { mensaje = "La foto es muy grande (máx 10 MB)." });
        if (!file.ContentType.StartsWith("image/")) return BadRequest(new { mensaje = "El archivo tiene que ser una imagen." });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var usuario = HttpContext.User?.Identity?.Name;
        var archivo = await GuardarFotoPropiaAsync(productoId, ms.ToArray(), Path.GetExtension(file.FileName), usuario);
        return Ok(new ProductoFotoDto(productoId, "APROBADA", usuario, null, archivo, DateTime.UtcNow));
    }

    /// <summary>Sube la foto propia bajando la imagen de un LINK (URL) pegado en la compu.</summary>
    [HttpPost("{productoId:int}/desde-url")]
    public async Task<IActionResult> DesdeUrl(int productoId, [FromBody] DesdeUrlRequest req)
    {
        if (!await _db.CafeProductos.AnyAsync(p => p.Id == productoId)) return NotFound(new { mensaje = "Producto no encontrado." });
        var url = (req?.Url ?? "").Trim();
        if (string.IsNullOrEmpty(url) || !(url.StartsWith("http://") || url.StartsWith("https://")))
            return BadRequest(new { mensaje = "Pegá un link válido (que empiece con http)." });
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return BadRequest(new { mensaje = "No pude descargar esa imagen (el link no responde)." });
            var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!ct.StartsWith("image/")) return BadRequest(new { mensaje = "Ese link no es una imagen." });
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0) return BadRequest(new { mensaje = "La imagen vino vacía." });
            if (bytes.Length > 10 * 1024 * 1024) return BadRequest(new { mensaje = "La imagen es muy grande (máx 10 MB)." });
            var ext = ct switch { "image/png" => ".png", "image/gif" => ".gif", "image/webp" => ".webp", _ => ".jpg" };
            var usuario = HttpContext.User?.Identity?.Name;
            var archivo = await GuardarFotoPropiaAsync(productoId, bytes, ext, usuario);
            return Ok(new ProductoFotoDto(productoId, "APROBADA", usuario, null, archivo, DateTime.UtcNow));
        }
        catch (Exception ex) { return BadRequest(new { mensaje = "No pude traer esa imagen: " + ex.Message }); }
    }

    /// <summary>
    /// Marca la foto de un producto. Estado válido: "APROBADA" | "REPORTADA".
    /// Si Estado viene vacío/null, se LIMPIA la marca (vuelve a "sin marcar").
    /// </summary>
    [HttpPost("{productoId:int}")]
    public async Task<IActionResult> Marcar(int productoId, [FromBody] MarcarFotoRequest req)
    {
        var estado = (req?.Estado ?? "").Trim().ToUpperInvariant();
        if (estado != "APROBADA" && estado != "REPORTADA" && estado != "")
            return BadRequest(new { mensaje = "Estado inválido. Usá APROBADA, REPORTADA o vacío para limpiar." });

        // El producto tiene que existir (evita basura por ids inventados).
        var existeProd = await _db.CafeProductos.AnyAsync(p => p.Id == productoId);
        if (!existeProd) return NotFound(new { mensaje = "Producto no encontrado." });

        var usuario = HttpContext.User?.Identity?.Name;
        var reg = await _db.CafeProductoFotos.FirstOrDefaultAsync(f => f.CafeProductoId == productoId);

        if (estado == "")
        {
            // Limpiar: si había registro, lo borramos.
            // Al limpiar la marca: si NO hay foto propia, borramos la fila; si la hay, la conservamos.
            var propia = reg?.FotoPropiaArchivo;
            if (reg is not null)
            {
                if (string.IsNullOrEmpty(reg.FotoPropiaArchivo)) _db.CafeProductoFotos.Remove(reg);
                else { reg.Estado = null; reg.Usuario = usuario; reg.Comentario = null; reg.UpdatedAt = DateTime.UtcNow; }
            }
            await _db.SaveChangesAsync();
            return Ok(new ProductoFotoDto(productoId, null, null, null, propia, DateTime.UtcNow));
        }

        if (reg is null)
        {
            reg = new CafeProductoFoto { CafeProductoId = productoId };
            _db.CafeProductoFotos.Add(reg);
        }
        reg.Estado = estado;
        reg.Usuario = usuario;
        reg.Comentario = string.IsNullOrWhiteSpace(req?.Comentario) ? null : req!.Comentario!.Trim();
        reg.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new ProductoFotoDto(reg.CafeProductoId, reg.Estado, reg.Usuario, reg.Comentario, reg.FotoPropiaArchivo, reg.UpdatedAt));
    }
}
