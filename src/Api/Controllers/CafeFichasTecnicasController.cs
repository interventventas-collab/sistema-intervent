using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Api.Controllers;

/// <summary>
/// 2026-10-30: fichas técnicas de café. Una ficha por café (o suelta, sin producto), con fotos del
/// café y de la etiqueta, datos de origen, rueda de sabores, balance y, en los blends, cada parte
/// con sus propios datos. La pantalla /cafe/fichas-tecnicas y la ficha del producto usan esto mismo.
/// </summary>
[ApiController]
[Route("api/cafe/fichas-tecnicas")]
[Authorize]
public class CafeFichasTecnicasController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CafeFichaTecnicaPdfService _pdf;
    public CafeFichasTecnicasController(AppDbContext db, CafeFichaTecnicaPdfService pdf) { _db = db; _pdf = pdf; }

    // Mismo lugar que las fotos propias de productos: así las sirve /api/public/producto-foto/img/.
    public const string FotosDir = "/data/files/producto-fotos";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public record ComponenteDto(string? Pais, decimal? Pct, string? Region, string? Variedad,
        string? Proceso, string? Finca, string? Altura);

    public record FichaTecnicaDto(
        int Id, int? CafeProductoId, string? ProductoNombre, string? ProductoSku,
        string Nombre, string Tipo, string? Especie, string? Perfil,
        string? Pais, string? Region, string? Finca, string? Altura, string? Variedad, string? Proceso,
        string? Tueste, string? IdealPara, string? Puntaje,
        string? Descripcion, string? FraganciaAroma, string? Sabor, string? Retrogusto,
        List<string> Rueda, decimal? Cuerpo, decimal? Acidez, decimal? Dulzor,
        List<ComponenteDto> Componentes, List<string> NoAplica,
        string? FotoCafeUrl, string? FotoEtiquetaUrl, string? UpdatedBy, DateTime UpdatedAt);

    public record GuardarFichaRequest(
        int? CafeProductoId, string? Nombre, string? Tipo, string? Especie, string? Perfil,
        string? Pais, string? Region, string? Finca, string? Altura, string? Variedad, string? Proceso,
        string? Tueste, string? IdealPara, string? Puntaje,
        string? Descripcion, string? FraganciaAroma, string? Sabor, string? Retrogusto,
        List<string>? Rueda, decimal? Cuerpo, decimal? Acidez, decimal? Dulzor,
        List<ComponenteDto>? Componentes, List<string>? NoAplica);

    public record CrearFichaRequest(int? CafeProductoId, string? Nombre, int? DuplicarDeId);

    public record ProductoCafeDto(int Id, string Nombre, string? Sku, int? FichaId);

    private static string? Img(string? archivo) =>
        string.IsNullOrEmpty(archivo) ? null : $"/api/public/producto-foto/img/{archivo}";

    private static List<string> Lista(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? new() : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static List<ComponenteDto> LeerComponentes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<ComponenteDto>>(json, Json) ?? new(); }
        catch { return new(); }
    }

    private static FichaTecnicaDto ToDto(CafeFichaTecnica f, CafeProducto? p) => new(
        f.Id, f.CafeProductoId, p?.Nombre, p?.Sku,
        f.Nombre, f.Tipo, f.Especie, f.Perfil,
        f.Pais, f.Region, f.Finca, f.Altura, f.Variedad, f.Proceso,
        f.Tueste, f.IdealPara, f.Puntaje,
        f.Descripcion, f.FraganciaAroma, f.Sabor, f.Retrogusto,
        Lista(f.Rueda), f.Cuerpo, f.Acidez, f.Dulzor,
        LeerComponentes(f.ComponentesJson), Lista(f.NoAplica),
        Img(f.FotoCafeArchivo), Img(f.FotoEtiquetaArchivo), f.UpdatedBy, f.UpdatedAt);

    private async Task<FichaTecnicaDto> DtoAsync(CafeFichaTecnica f)
    {
        var p = f.CafeProductoId is int pid ? await _db.CafeProductos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid) : null;
        return ToDto(f, p);
    }

    /// <summary>Todas las fichas activas, ordenadas por nombre.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar()
    {
        var fichas = await _db.CafeFichasTecnicas.AsNoTracking().Where(f => f.IsActive).OrderBy(f => f.Nombre).ToListAsync();
        var ids = fichas.Where(f => f.CafeProductoId.HasValue).Select(f => f.CafeProductoId!.Value).ToList();
        var prods = await _db.CafeProductos.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        return Ok(fichas.Select(f => ToDto(f, f.CafeProductoId is int pid && prods.TryGetValue(pid, out var p) ? p : null)));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Obtener(int id)
    {
        var f = await _db.CafeFichasTecnicas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
        return f is null ? NotFound(new { error = "La ficha no existe." }) : Ok(await DtoAsync(f));
    }

    /// <summary>La ficha de un producto (204 si todavía no tiene).</summary>
    [HttpGet("por-producto/{productoId:int}")]
    public async Task<IActionResult> PorProducto(int productoId)
    {
        var f = await _db.CafeFichasTecnicas.AsNoTracking().FirstOrDefaultAsync(x => x.CafeProductoId == productoId && x.IsActive);
        return f is null ? NoContent() : Ok(await DtoAsync(f));
    }

    /// <summary>Cafés activos del sistema, para enlazar una ficha a su producto.</summary>
    [HttpGet("productos")]
    public async Task<IActionResult> Productos()
    {
        var fichas = await _db.CafeFichasTecnicas.AsNoTracking().Where(f => f.IsActive && f.CafeProductoId != null)
            .Select(f => new { f.Id, P = f.CafeProductoId!.Value }).ToListAsync();
        var porProd = fichas.GroupBy(x => x.P).ToDictionary(g => g.Key, g => g.First().Id);
        var prods = await _db.CafeProductos.AsNoTracking().Where(p => p.IsActive && p.Categoria == "CAFE")
            .OrderBy(p => p.Nombre).Select(p => new { p.Id, p.Nombre, p.Sku }).ToListAsync();
        return Ok(prods.Select(p => new ProductoCafeDto(p.Id, p.Nombre, p.Sku, porProd.TryGetValue(p.Id, out var fid) ? fid : null)));
    }

    /// <summary>Crea una ficha nueva: vacía, para un producto (trae su nombre) o copiando otra como base.</summary>
    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearFichaRequest req)
    {
        CafeProducto? prod = null;
        if (req.CafeProductoId is int pid)
        {
            prod = await _db.CafeProductos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid);
            if (prod is null) return BadRequest(new { error = "El producto no existe." });
            var ya = await _db.CafeFichasTecnicas.AsNoTracking().FirstOrDefaultAsync(f => f.CafeProductoId == pid && f.IsActive);
            if (ya is not null) return Ok(await DtoAsync(ya)); // ya tenía: se abre esa
        }

        var f = new CafeFichaTecnica();
        if (req.DuplicarDeId is int dup)
        {
            var b = await _db.CafeFichasTecnicas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == dup);
            if (b is null) return BadRequest(new { error = "La ficha base no existe." });
            f = new CafeFichaTecnica
            {
                Tipo = b.Tipo, Especie = b.Especie, Perfil = b.Perfil, Pais = b.Pais, Region = b.Region, Finca = b.Finca,
                Altura = b.Altura, Variedad = b.Variedad, Proceso = b.Proceso, Tueste = b.Tueste, IdealPara = b.IdealPara,
                Puntaje = b.Puntaje, Descripcion = b.Descripcion, FraganciaAroma = b.FraganciaAroma, Sabor = b.Sabor,
                Retrogusto = b.Retrogusto, Rueda = b.Rueda, Cuerpo = b.Cuerpo, Acidez = b.Acidez, Dulzor = b.Dulzor,
                ComponentesJson = b.ComponentesJson, NoAplica = b.NoAplica,
                // Las fotos no se copian: cada café tiene las suyas.
            };
            f.Nombre = $"{b.Nombre} (copia)";
        }
        if (prod is not null) { f.CafeProductoId = prod.Id; f.Nombre = prod.Nombre; }
        if (!string.IsNullOrWhiteSpace(req.Nombre)) f.Nombre = req.Nombre.Trim();
        if (string.IsNullOrWhiteSpace(f.Nombre)) f.Nombre = "Café nuevo";
        f.UpdatedBy = User?.Identity?.Name;
        _db.CafeFichasTecnicas.Add(f);
        await _db.SaveChangesAsync();
        return Ok(await DtoAsync(f));
    }

    private static string? T(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }

    private static decimal? Bal(decimal? v) => v is null ? null : Math.Clamp(Math.Round(v.Value * 2) / 2, 0, 5);

    /// <summary>Guarda la ficha completa (la pantalla la manda entera en cada cambio: se guarda sola).</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Guardar(int id, [FromBody] GuardarFichaRequest r)
    {
        var f = await _db.CafeFichasTecnicas.FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
        if (f is null) return NotFound(new { error = "La ficha no existe." });

        if (r.CafeProductoId != f.CafeProductoId)
        {
            if (r.CafeProductoId is int pid)
            {
                if (!await _db.CafeProductos.AnyAsync(p => p.Id == pid)) return BadRequest(new { error = "El producto no existe." });
                var otra = await _db.CafeFichasTecnicas.AsNoTracking().FirstOrDefaultAsync(x => x.CafeProductoId == pid && x.IsActive && x.Id != id);
                if (otra is not null) return BadRequest(new { error = $"Ese producto ya tiene su ficha (\"{otra.Nombre}\")." });
            }
            f.CafeProductoId = r.CafeProductoId;
        }

        f.Nombre = T(r.Nombre, 150) ?? f.Nombre;
        f.Tipo = r.Tipo == "BLEND" ? "BLEND" : "ORIGEN";
        f.Especie = T(r.Especie, 30);
        f.Perfil = T(r.Perfil, 30)?.ToUpperInvariant();
        f.Pais = T(r.Pais, 80); f.Region = T(r.Region, 150); f.Finca = T(r.Finca, 150);
        f.Altura = T(r.Altura, 80); f.Variedad = T(r.Variedad, 150); f.Proceso = T(r.Proceso, 150);
        f.Tueste = T(r.Tueste, 60); f.IdealPara = T(r.IdealPara, 150); f.Puntaje = T(r.Puntaje, 20);
        f.Descripcion = T(r.Descripcion, 2000); f.FraganciaAroma = T(r.FraganciaAroma, 300);
        f.Sabor = T(r.Sabor, 300); f.Retrogusto = T(r.Retrogusto, 300);
        f.Rueda = r.Rueda is { Count: > 0 } ? T(string.Join(",", r.Rueda.Distinct()), 300) : null;
        f.Cuerpo = Bal(r.Cuerpo); f.Acidez = Bal(r.Acidez); f.Dulzor = Bal(r.Dulzor);
        f.ComponentesJson = r.Componentes is { Count: > 0 } ? JsonSerializer.Serialize(r.Componentes, Json) : null;
        f.NoAplica = r.NoAplica is { Count: > 0 } ? T(string.Join(",", r.NoAplica.Distinct()), 400) : null;
        f.UpdatedBy = User?.Identity?.Name;
        f.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(await DtoAsync(f));
    }

    /// <summary>Borra la ficha (queda guardada como inactiva, no se pierde).</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Borrar(int id)
    {
        var f = await _db.CafeFichasTecnicas.FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
        if (f is null) return NotFound(new { error = "La ficha no existe." });
        f.IsActive = false;
        f.UpdatedBy = User?.Identity?.Name;
        f.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Sube (o cambia) la foto del café o de la etiqueta. tipo = "cafe" | "etiqueta".</summary>
    [HttpPost("{id:int}/foto/{tipo}")]
    [RequestSizeLimit(15 * 1024 * 1024)]
    public async Task<IActionResult> SubirFoto(int id, string tipo, IFormFile file)
    {
        if (tipo is not ("cafe" or "etiqueta")) return BadRequest(new { error = "Tipo de foto inválido." });
        var f = await _db.CafeFichasTecnicas.FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
        if (f is null) return NotFound(new { error = "La ficha no existe." });
        if (file is null || file.Length == 0) return BadRequest(new { error = "No se recibió ninguna imagen." });
        if (file.Length > 15 * 1024 * 1024) return BadRequest(new { error = "La imagen es muy grande (máx 15 MB)." });
        if (!file.ContentType.StartsWith("image/")) return BadRequest(new { error = "El archivo tiene que ser una imagen." });

        Directory.CreateDirectory(FotosDir);
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".gif")) ext = ".jpg";
        var nombre = $"ficha-{id}-{tipo}-{Guid.NewGuid():N}{ext}";
        await using (var fs = new FileStream(Path.Combine(FotosDir, nombre), FileMode.Create))
            await file.CopyToAsync(fs);

        var viejo = tipo == "cafe" ? f.FotoCafeArchivo : f.FotoEtiquetaArchivo;
        if (tipo == "cafe") f.FotoCafeArchivo = nombre; else f.FotoEtiquetaArchivo = nombre;
        f.UpdatedBy = User?.Identity?.Name;
        f.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        BorrarArchivo(viejo);
        return Ok(await DtoAsync(f));
    }

    [HttpDelete("{id:int}/foto/{tipo}")]
    public async Task<IActionResult> QuitarFoto(int id, string tipo)
    {
        if (tipo is not ("cafe" or "etiqueta")) return BadRequest(new { error = "Tipo de foto inválido." });
        var f = await _db.CafeFichasTecnicas.FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
        if (f is null) return NotFound(new { error = "La ficha no existe." });
        var viejo = tipo == "cafe" ? f.FotoCafeArchivo : f.FotoEtiquetaArchivo;
        if (tipo == "cafe") f.FotoCafeArchivo = null; else f.FotoEtiquetaArchivo = null;
        f.UpdatedBy = User?.Identity?.Name;
        f.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        BorrarArchivo(viejo);
        return Ok(await DtoAsync(f));
    }

    private static void BorrarArchivo(string? archivo)
    {
        if (string.IsNullOrEmpty(archivo)) return;
        try { var p = Path.Combine(FotosDir, archivo); if (System.IO.File.Exists(p)) System.IO.File.Delete(p); }
        catch { /* best-effort */ }
    }

    /// <summary>PDF de la ficha (A4, siempre el mismo diseño). ?descargar=1 lo baja en vez de abrirlo.</summary>
    [HttpGet("{id:int}/pdf")]
    public async Task<IActionResult> Pdf(int id, [FromQuery] int? descargar)
    {
        var f = await _db.CafeFichasTecnicas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
        if (f is null) return NotFound(new { error = "La ficha no existe." });
        var bytes = _pdf.Generar(f);
        var nombre = $"Ficha tecnica - {string.Concat(f.Nombre.Split(Path.GetInvalidFileNameChars()))}.pdf";
        return descargar == 1 ? File(bytes, "application/pdf", nombre) : File(bytes, "application/pdf");
    }
}
