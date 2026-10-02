using Api.Data;
using Api.Models;
using Api.Services;
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
        string? Comentario, string? FotoPropiaArchivo, DateTime UpdatedAt, bool TieneOriginal = false);

    public record TokenResp(string Token);

    /// <summary>Devuelve el estado de foto de todos los productos que tienen alguna marca.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar()
    {
        var lista = await _db.CafeProductoFotos
            .Select(f => new ProductoFotoDto(f.CafeProductoId, f.Estado, f.Usuario, f.Comentario, f.FotoPropiaArchivo, f.UpdatedAt, f.FotoOriginalArchivo != null))
            .ToListAsync();
        return Ok(lista);
    }

    /// <summary>Estado de la foto de UN producto (para sondear mientras el celu sube por QR).</summary>
    [HttpGet("{productoId:int}")]
    public async Task<IActionResult> Estado(int productoId)
    {
        var f = await _db.CafeProductoFotos.FirstOrDefaultAsync(x => x.CafeProductoId == productoId);
        if (f is null) return Ok(new ProductoFotoDto(productoId, null, null, null, null, DateTime.UtcNow));
        return Ok(new ProductoFotoDto(f.CafeProductoId, f.Estado, f.Usuario, f.Comentario, f.FotoPropiaArchivo, f.UpdatedAt, f.FotoOriginalArchivo != null));
    }

    public record FotoPropiaSkuDto(string Sku, int CafeProductoId, string Nombre, string Archivo);

    /// <summary>2026-10-01: fotos propias de los productos con esos SKU (separados por coma). La usa
    /// Publicaciones para ofrecer "usar la foto propia" en la publicación de ese producto.</summary>
    [HttpGet("por-sku")]
    public async Task<IActionResult> PorSku([FromQuery] string? skus)
    {
        var lista = (skus ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
        if (lista.Count == 0) return Ok(new List<FotoPropiaSkuDto>());
        var res = await (from p in _db.CafeProductos
                         join f in _db.CafeProductoFotos on p.Id equals f.CafeProductoId
                         where p.Sku != null && lista.Contains(p.Sku) && f.FotoPropiaArchivo != null
                         select new FotoPropiaSkuDto(p.Sku!, p.Id, p.Nombre, f.FotoPropiaArchivo!))
                        .ToListAsync();
        return Ok(res);
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

    /// <summary>Guarda bytes como foto propia del producto (APROBADA) y borra la anterior si había.
    /// <paramref name="original"/>: archivo (ya en FotosDir) de la foto con la tela chroma de la que salió;
    /// null = se subió tal cual, y la original chroma que hubiera de antes se borra.</summary>
    private async Task<string> GuardarFotoPropiaAsync(int productoId, byte[] bytes, string? ext, string? usuario, string? original = null)
    {
        Directory.CreateDirectory(FotosDir);
        if (string.IsNullOrEmpty(ext) || ext.Length > 6) ext = ".jpg";
        var filename = $"prod-{productoId}-{Guid.NewGuid():N}{ext}";
        await System.IO.File.WriteAllBytesAsync(Path.Combine(FotosDir, filename), bytes);

        var foto = await _db.CafeProductoFotos.FirstOrDefaultAsync(f => f.CafeProductoId == productoId);
        var archivoViejo = foto?.FotoPropiaArchivo;
        var originalViejo = foto?.FotoOriginalArchivo;
        if (foto is null) { foto = new CafeProductoFoto { CafeProductoId = productoId }; _db.CafeProductoFotos.Add(foto); }
        foto.FotoPropiaArchivo = filename;
        foto.FotoPropiaAt = DateTime.UtcNow;
        foto.FotoOriginalArchivo = original;
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
        if (!string.IsNullOrEmpty(originalViejo) && originalViejo != original)
        {
            try { var old = Path.Combine(FotosDir, originalViejo); if (System.IO.File.Exists(old)) System.IO.File.Delete(old); }
            catch { /* best-effort */ }
        }
        return filename;
    }

    /// <summary>2026-09-28: QUITA la foto propia del producto (borra el archivo). Vuelve a verse la de
    /// MercadoLibre. No toca nada en MeLi.</summary>
    [HttpDelete("{productoId:int}/propia")]
    public async Task<IActionResult> QuitarPropia(int productoId)
    {
        var foto = await _db.CafeProductoFotos.FirstOrDefaultAsync(f => f.CafeProductoId == productoId);
        if (foto is null || string.IsNullOrEmpty(foto.FotoPropiaArchivo))
            return Ok(new ProductoFotoDto(productoId, foto?.Estado, foto?.Usuario, foto?.Comentario, null, DateTime.UtcNow));
        var archivo = foto.FotoPropiaArchivo;
        var original = foto.FotoOriginalArchivo;
        foto.FotoPropiaArchivo = null;
        foto.FotoPropiaAt = null;
        foto.FotoOriginalArchivo = null;
        // La marca APROBADA la puso la subida de la foto propia; la de MeLi vuelve a quedar "sin marcar".
        foto.Estado = null;
        foto.Comentario = null;
        foto.Usuario = HttpContext.User?.Identity?.Name;
        foto.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        foreach (var a in new[] { archivo, original })
        {
            if (string.IsNullOrEmpty(a)) continue;
            try { var path = Path.Combine(FotosDir, a); if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
            catch { /* best-effort */ }
        }
        return Ok(new ProductoFotoDto(productoId, null, foto.Usuario, null, null, foto.UpdatedAt));
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

    // ───────────── 2026-10-01: Fotos chroma (fondo verde/azul → blanco para MeLi) ─────────────
    // Flujo: el celu SUBE la foto una vez (queda en tmp/ con un id) → se ve antes/después → si mueve la
    // tolerancia se REPROCESA por id (no se vuelve a mandar la foto por los datos del celu) → CONFIRMAR
    // la guarda como foto propia del producto y guarda la original al lado.

    private static string TmpDir => Path.Combine(FotosDir, "tmp");

    public record ChromaOpcionesReq(string? TempId, string? Color, int? Tolerancia, int? Suavizado);
    public record ChromaResp(string TempId, string? Antes, string? Despues, string? ColorUsado, string? Aviso, string? Error);

    private static FotoChromaService.Opciones Opciones(string? color, int? tol, int? suav) =>
        new(string.IsNullOrWhiteSpace(color) ? "auto" : color, tol ?? 50, suav ?? 40);

    /// <summary>Ruta del archivo temporal. El id es un Guid "N" (32 hex) — cualquier otra cosa se rechaza.</summary>
    private static string? BuscarTmp(string? tempId)
    {
        if (string.IsNullOrEmpty(tempId) || tempId.Length != 32 || !tempId.All(Uri.IsHexDigit)) return null;
        if (!Directory.Exists(TmpDir)) return null;
        return Directory.EnumerateFiles(TmpDir, tempId + ".*").FirstOrDefault();
    }

    private static void LimpiarTmpViejos()
    {
        try
        {
            if (!Directory.Exists(TmpDir)) return;
            foreach (var f in Directory.EnumerateFiles(TmpDir))
                if (System.IO.File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)) System.IO.File.Delete(f);
        }
        catch { /* best-effort */ }
    }

    private static ChromaResp Procesar(string tempId, byte[] bytes, FotoChromaService.Opciones op, bool conAntes)
    {
        var r = FotoChromaService.Procesar(bytes, op);
        string? antes = null;
        if (conAntes)
        {
            var mini = FotoChromaService.Miniatura(bytes);
            if (mini is not null) antes = "data:image/jpeg;base64," + Convert.ToBase64String(mini);
        }
        var despues = r.Jpeg is null ? null : "data:image/jpeg;base64," + Convert.ToBase64String(r.Jpeg);
        return new ChromaResp(tempId, antes, despues, r.ColorUsado, r.Aviso, r.Error);
    }

    /// <summary>Sube la foto con tela chroma y devuelve antes/después. No guarda nada en el producto.</summary>
    [HttpPost("chroma/subir")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<IActionResult> ChromaSubir(IFormFile file, [FromForm] string? color, [FromForm] int? tolerancia, [FromForm] int? suavizado)
    {
        if (file is null || file.Length == 0) return BadRequest(new { mensaje = "No se recibió ninguna foto." });
        if (file.Length > 25 * 1024 * 1024) return BadRequest(new { mensaje = "La foto es muy grande (máx 25 MB)." });
        if (!file.ContentType.StartsWith("image/")) return BadRequest(new { mensaje = "El archivo tiene que ser una imagen." });

        LimpiarTmpViejos();
        Directory.CreateDirectory(TmpDir);
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext) || ext.Length > 6 || ext.Any(c => !char.IsLetterOrDigit(c) && c != '.')) ext = ".jpg";
        var tempId = Guid.NewGuid().ToString("N");
        await System.IO.File.WriteAllBytesAsync(Path.Combine(TmpDir, tempId + ext), bytes);

        return Ok(Procesar(tempId, bytes, Opciones(color, tolerancia, suavizado), conAntes: true));
    }

    /// <summary>Vuelve a procesar la foto ya subida con otro color/tolerancia/suavizado.</summary>
    [HttpPost("chroma/procesar")]
    public async Task<IActionResult> ChromaProcesar([FromBody] ChromaOpcionesReq req)
    {
        var path = BuscarTmp(req?.TempId);
        if (path is null) return NotFound(new { mensaje = "La foto ya no está (pasó más de un día). Volvé a sacarla." });
        var bytes = await System.IO.File.ReadAllBytesAsync(path);
        return Ok(Procesar(req!.TempId!, bytes, Opciones(req.Color, req.Tolerancia, req.Suavizado), conAntes: false));
    }

    /// <summary>Guarda la foto procesada como foto propia del producto (y la original al lado).</summary>
    [HttpPost("{productoId:int}/chroma/confirmar")]
    public async Task<IActionResult> ChromaConfirmar(int productoId, [FromBody] ChromaOpcionesReq req)
    {
        if (!await _db.CafeProductos.AnyAsync(p => p.Id == productoId)) return NotFound(new { mensaje = "Producto no encontrado." });
        var path = BuscarTmp(req?.TempId);
        if (path is null) return NotFound(new { mensaje = "La foto ya no está (pasó más de un día). Volvé a sacarla." });

        var bytes = await System.IO.File.ReadAllBytesAsync(path);
        // Se reprocesa acá (no se confía en la imagen que tiene la pantalla) con las mismas opciones.
        var r = FotoChromaService.Procesar(bytes, Opciones(req!.Color, req.Tolerancia, req.Suavizado));
        if (r.Jpeg is null) return BadRequest(new { mensaje = r.Error ?? "No se pudo procesar la foto." });

        var original = $"prod-{productoId}-orig-{Guid.NewGuid():N}{Path.GetExtension(path)}";
        System.IO.File.Move(path, Path.Combine(FotosDir, original));

        var usuario = HttpContext.User?.Identity?.Name;
        var archivo = await GuardarFotoPropiaAsync(productoId, r.Jpeg, ".jpg", usuario, original);
        return Ok(new ProductoFotoDto(productoId, "APROBADA", usuario, null, archivo, DateTime.UtcNow, true));
    }

    /// <summary>Vuelve a abrir la foto ORIGINAL guardada de un producto para reprocesarla (la copia a tmp/).</summary>
    [HttpPost("{productoId:int}/chroma/reabrir")]
    public async Task<IActionResult> ChromaReabrir(int productoId)
    {
        var foto = await _db.CafeProductoFotos.FirstOrDefaultAsync(f => f.CafeProductoId == productoId);
        var orig = foto?.FotoOriginalArchivo;
        var origPath = string.IsNullOrEmpty(orig) ? null : Path.Combine(FotosDir, orig);
        if (origPath is null || !System.IO.File.Exists(origPath))
            return NotFound(new { mensaje = "Este producto no tiene guardada la foto original con la tela." });

        LimpiarTmpViejos();
        Directory.CreateDirectory(TmpDir);
        var tempId = Guid.NewGuid().ToString("N");
        var bytes = await System.IO.File.ReadAllBytesAsync(origPath);
        await System.IO.File.WriteAllBytesAsync(Path.Combine(TmpDir, tempId + Path.GetExtension(origPath)), bytes);
        return Ok(Procesar(tempId, bytes, Opciones(null, null, null), conAntes: true));
    }

    // ───────────── 2026-10-01: GALERÍA de fotos chroma ─────────────
    // Se sacan muchas seguidas sin elegir producto; cada una queda procesada. Después: a un producto,
    // a una publicación de MeLi (eso lo hace la pantalla con el mismo PUT de fotos de Publicaciones y
    // acá sólo se anota), descargar o borrar. Archivos "gal-..." en FotosDir (los sirve el /img público).

    public record GaleriaDto(int Id, string? Archivo, string? ColorUsado, string? Aviso, string? Error,
        string Color, int Tolerancia, int Suavizado, string? Usuario, DateTime CreatedAt,
        int? ProductoId, string? ProductoSku, string? ProductoNombre, List<string> Publicaciones);
    public record GaleriaOpcionesReq(string? Color, int? Tolerancia, int? Suavizado);
    public record GaleriaVistaPreviaResp(string? Antes, string? Despues, string? ColorUsado, string? Aviso, string? Error);
    public record GaleriaAProductoReq(int ProductoId);
    public record GaleriaMarcarPublicacionReq(List<int> Ids, string Mla);
    public record PublicacionBuscadaDto(string MeliItemId, string Titulo, string? Sku, string Estado,
        string? Thumbnail, string? Cuenta, bool DeCatalogo);

    private static List<string> ListaPublis(string? s) =>
        (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private async Task<List<GaleriaDto>> GaleriaDtosAsync(IQueryable<CafeFotoGaleria> q)
    {
        var filas = await (from g in q
                           join p in _db.CafeProductos on g.ProductoId equals p.Id into pj
                           from p in pj.DefaultIfEmpty()
                           orderby g.CreatedAt descending
                           select new { g, Sku = p == null ? null : p.Sku, Nombre = p == null ? null : p.Nombre })
                          .Take(400).ToListAsync();
        return filas.Select(x => new GaleriaDto(x.g.Id, x.g.Archivo, x.g.ColorUsado, x.g.Aviso, x.g.Error,
            x.g.Color, x.g.Tolerancia, x.g.Suavizado, x.g.Usuario, x.g.CreatedAt,
            x.g.ProductoId, x.Sku, x.Nombre, ListaPublis(x.g.Publicaciones))).ToList();
    }

    private static Task ProcesarGaleriaAsync(CafeFotoGaleria g) => FotoGaleriaService.ProcesarAsync(g);

    /// <summary>Todas las fotos de la galería, las más nuevas primero.</summary>
    [HttpGet("galeria")]
    public async Task<IActionResult> GaleriaListar() => Ok(await GaleriaDtosAsync(_db.CafeFotoGaleria));

    /// <summary>Sube UNA foto a la galería y la procesa. El celu manda varias de a una.</summary>
    [HttpPost("galeria/subir")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<IActionResult> GaleriaSubir(IFormFile file, [FromForm] string? color, [FromForm] int? tolerancia, [FromForm] int? suavizado)
    {
        var (g, error) = await FotoGaleriaService.SubirAsync(_db, file, Opciones(color, tolerancia, suavizado), HttpContext.User?.Identity?.Name);
        if (g is null) return BadRequest(new { mensaje = error });
        return Ok((await GaleriaDtosAsync(_db.CafeFotoGaleria.Where(x => x.Id == g.Id))).First());
    }

    public record FotosLinkResp(string Token);

    /// <summary>2026-10-02: link PÚBLICO del celu para sacar fotos (sin usuario ni clave). Lo muestra el
    /// botón 📷 Fotos como QR. Si todavía no existe, se crea.</summary>
    [HttpGet("galeria/link")]
    public async Task<IActionResult> GaleriaLink() => Ok(new FotosLinkResp((await FotoGaleriaService.LinkTokenAsync(_db, crear: true))!));

    /// <summary>Cambia el link del celu (el viejo deja de andar). Sólo admin: es la "llave" del link.</summary>
    [HttpPost("galeria/link/cambiar")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> GaleriaLinkCambiar() => Ok(new FotosLinkResp(await FotoGaleriaService.CambiarLinkAsync(_db)));

    /// <summary>Retoque: cómo quedaría con otras opciones. NO guarda.</summary>
    [HttpPost("galeria/{id:int}/vista-previa")]
    public async Task<IActionResult> GaleriaVistaPrevia(int id, [FromBody] GaleriaOpcionesReq req, [FromQuery] bool conAntes = false)
    {
        var g = await _db.CafeFotoGaleria.FindAsync(id);
        if (g is null) return NotFound(new { mensaje = "Esa foto ya no está en la galería." });
        var bytes = await System.IO.File.ReadAllBytesAsync(Path.Combine(FotosDir, g.OriginalArchivo));
        var r = Procesar("", bytes, Opciones(req?.Color, req?.Tolerancia, req?.Suavizado), conAntes);
        return Ok(new GaleriaVistaPreviaResp(r.Antes, r.Despues, r.ColorUsado, r.Aviso, r.Error));
    }

    /// <summary>Retoque: guarda la foto procesada con las opciones nuevas.</summary>
    [HttpPost("galeria/{id:int}/retocar")]
    public async Task<IActionResult> GaleriaRetocar(int id, [FromBody] GaleriaOpcionesReq req)
    {
        var g = await _db.CafeFotoGaleria.FindAsync(id);
        if (g is null) return NotFound(new { mensaje = "Esa foto ya no está en la galería." });
        var op = Opciones(req?.Color, req?.Tolerancia, req?.Suavizado);
        g.Color = op.Color ?? "auto"; g.Tolerancia = op.Tolerancia; g.Suavizado = op.Suavizado;
        await ProcesarGaleriaAsync(g);
        await _db.SaveChangesAsync();
        if (g.Archivo is null) return BadRequest(new { mensaje = g.Error ?? "No se pudo procesar la foto." });
        return Ok((await GaleriaDtosAsync(_db.CafeFotoGaleria.Where(x => x.Id == g.Id))).First());
    }

    /// <summary>La pone como FOTO PROPIA del producto (reemplaza la que tuviera). La galería la conserva.</summary>
    [HttpPost("galeria/{id:int}/a-producto")]
    public async Task<IActionResult> GaleriaAProducto(int id, [FromBody] GaleriaAProductoReq req)
    {
        var g = await _db.CafeFotoGaleria.FindAsync(id);
        if (g is null) return NotFound(new { mensaje = "Esa foto ya no está en la galería." });
        if (string.IsNullOrEmpty(g.Archivo)) return BadRequest(new { mensaje = "Esta foto no se pudo procesar: retocala primero." });
        if (!await _db.CafeProductos.AnyAsync(p => p.Id == req.ProductoId)) return NotFound(new { mensaje = "Producto no encontrado." });

        // Copias propias: si después se borra de la galería, el producto no pierde la foto.
        var bytes = await System.IO.File.ReadAllBytesAsync(Path.Combine(FotosDir, g.Archivo));
        var original = $"prod-{req.ProductoId}-orig-{Guid.NewGuid():N}{Path.GetExtension(g.OriginalArchivo)}";
        System.IO.File.Copy(Path.Combine(FotosDir, g.OriginalArchivo), Path.Combine(FotosDir, original));
        var usuario = HttpContext.User?.Identity?.Name;
        var archivo = await GuardarFotoPropiaAsync(req.ProductoId, bytes, ".jpg", usuario, original);

        g.ProductoId = req.ProductoId;
        g.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ProductoFotoDto(req.ProductoId, "APROBADA", usuario, null, archivo, DateTime.UtcNow, true));
    }

    /// <summary>Anota que esas fotos se subieron a esa publicación (la subida la hace la pantalla).</summary>
    [HttpPost("galeria/marcar-publicacion")]
    public async Task<IActionResult> GaleriaMarcarPublicacion([FromBody] GaleriaMarcarPublicacionReq req)
    {
        var mla = (req?.Mla ?? "").Trim().ToUpperInvariant();
        if (mla.Length is < 5 or > 30) return BadRequest(new { mensaje = "Publicación inválida." });
        var ids = req!.Ids ?? new();
        var filas = await _db.CafeFotoGaleria.Where(g => ids.Contains(g.Id)).ToListAsync();
        foreach (var g in filas)
        {
            var l = ListaPublis(g.Publicaciones);
            if (!l.Contains(mla)) l.Add(mla);
            // Si no entra, se quedan las últimas.
            while (l.Count > 1 && string.Join(",", l).Length > 400) l.RemoveAt(0);
            g.Publicaciones = string.Join(",", l);
            g.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Descarga: una sola → el JPG; varias → un ZIP. ids separados por coma.</summary>
    [HttpGet("galeria/descargar")]
    public async Task<IActionResult> GaleriaDescargar([FromQuery] string? ids)
    {
        var lista = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n > 0).Distinct().Take(200).ToList();
        var filas = await (from g in _db.CafeFotoGaleria
                           where lista.Contains(g.Id) && g.Archivo != null
                           join p in _db.CafeProductos on g.ProductoId equals p.Id into pj
                           from p in pj.DefaultIfEmpty()
                           select new { g.Id, g.Archivo, Sku = p == null ? null : p.Sku })
                          .ToListAsync();
        if (filas.Count == 0) return NotFound(new { mensaje = "No hay fotos para descargar." });

        string Nombre(int id, string? sku)
        {
            var baseNom = string.IsNullOrWhiteSpace(sku) ? $"foto-{id}" : $"{sku}-{id}";
            return new string(baseNom.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray()) + ".jpg";
        }

        if (filas.Count == 1)
        {
            var f = filas[0];
            return PhysicalFile(Path.Combine(FotosDir, f.Archivo!), "image/jpeg", Nombre(f.Id, f.Sku));
        }

        var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            foreach (var f in filas)
            {
                var path = Path.Combine(FotosDir, f.Archivo!);
                if (!System.IO.File.Exists(path)) continue;
                // Los JPG ya vienen comprimidos: guardarlos sin volver a comprimir es más rápido.
                var e = zip.CreateEntry(Nombre(f.Id, f.Sku), System.IO.Compression.CompressionLevel.NoCompression);
                await using var es = e.Open();
                await using var fs = System.IO.File.OpenRead(path);
                await fs.CopyToAsync(es);
            }
        }
        ms.Position = 0;
        return File(ms, "application/zip", $"fotos-{DateTime.UtcNow.AddHours(-3):yyyy-MM-dd-HHmm}.zip");
    }

    /// <summary>Borra la foto de la galería (no toca productos ni publicaciones donde ya se usó).</summary>
    [HttpDelete("galeria/{id:int}")]
    public async Task<IActionResult> GaleriaBorrar(int id)
    {
        var g = await _db.CafeFotoGaleria.FindAsync(id);
        if (g is null) return Ok(new { ok = true });
        _db.CafeFotoGaleria.Remove(g);
        await _db.SaveChangesAsync();
        foreach (var a in new[] { g.Archivo, g.OriginalArchivo })
        {
            if (string.IsNullOrEmpty(a)) continue;
            try { var f = Path.Combine(FotosDir, a); if (System.IO.File.Exists(f)) System.IO.File.Delete(f); }
            catch { /* best-effort */ }
        }
        return Ok(new { ok = true });
    }

    /// <summary>Busca publicaciones de MeLi por n° (MLA), SKU o palabras del título. Para "Agregar a una publicación".</summary>
    [HttpGet("galeria/publicaciones")]
    public async Task<IActionResult> GaleriaBuscarPublicaciones([FromQuery] string? q)
    {
        var texto = (q ?? "").Trim();
        if (texto.Length < 2) return Ok(new List<PublicacionBuscadaDto>());
        var soloNumero = new string(texto.Where(char.IsDigit).ToArray());
        var palabras = texto.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(5).ToList();

        var query = _db.MeliItems.AsQueryable();
        if (soloNumero.Length >= 6 && (texto.StartsWith("MLA", StringComparison.OrdinalIgnoreCase) || soloNumero.Length == texto.Length))
            query = query.Where(i => i.MeliItemId.Contains(soloNumero));
        else
            foreach (var w in palabras)
                query = query.Where(i => i.Title.Contains(w) || (i.Sku != null && i.Sku.Contains(w)));

        var filas = await (from i in query
                           join a in _db.MeliAccounts on i.MeliAccountId equals a.Id into aj
                           from a in aj.DefaultIfEmpty()
                           orderby (i.Status == "active" ? 0 : 1), i.SoldQuantity descending
                           select new { i.MeliItemId, i.Title, i.Sku, i.Status, i.Thumbnail, Cuenta = a == null ? null : a.Nickname, i.CatalogListing })
                          .Take(80).ToListAsync();
        // Una fila por publicación (las variantes repiten el MLA).
        var res = filas.GroupBy(f => f.MeliItemId).Select(gr => gr.First())
            .Take(30)
            .Select(f => new PublicacionBuscadaDto(f.MeliItemId, f.Title, f.Sku, f.Status, f.Thumbnail, f.Cuenta, f.CatalogListing))
            .ToList();
        return Ok(res);
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
