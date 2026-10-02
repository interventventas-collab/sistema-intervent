using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-10-02 — Link PÚBLICO del celu para sacar fotos de productos con tela verde/azul (como el link de los
/// repartidores: sin usuario ni clave). Sólo deja SUBIR fotos a la galería y ver las que se sacaron hoy.
/// Retocar, ponerla en un producto, en una publicación de MeLi o descargarla queda dentro del sistema.
/// El token vive en AppSettings ("fotos.link.token"); el admin lo cambia desde el sistema si se filtra.
/// </summary>
[ApiController]
[Route("api/public/fotos")]
[AllowAnonymous]
public class FotosLinkPublicController : ControllerBase
{
    private readonly AppDbContext _db;
    public FotosLinkPublicController(AppDbContext db) { _db = db; }

    /// <summary>Lo que se guarda en Usuario para las fotos del link ("📱 ALEXIS").</summary>
    private const string Prefijo = "📱 ";

    public record FotoLinkDto(int Id, string? Archivo, string? Error, string? Quien, DateTime CreatedAt);

    private static FotoLinkDto Dto(Models.CafeFotoGaleria g) =>
        new(g.Id, g.Archivo, g.Error, g.Usuario?.StartsWith(Prefijo) == true ? g.Usuario[Prefijo.Length..] : g.Usuario, g.CreatedAt);

    /// <summary>Fotos sacadas con el link en las últimas 12 horas (para que el celu vea que salieron bien).</summary>
    [HttpGet("{token}")]
    public async Task<IActionResult> Hoy(string token)
    {
        if (!await FotoGaleriaService.LinkValidoAsync(_db, token))
            return NotFound(new { mensaje = "Este link ya no funciona. Pedí el nuevo (botón 📷 Fotos del sistema)." });
        var desde = DateTime.UtcNow.AddHours(-12);
        var filas = await _db.CafeFotoGaleria
            .Where(g => g.CreatedAt >= desde && g.Usuario != null && g.Usuario.StartsWith(Prefijo))
            .OrderByDescending(g => g.CreatedAt).Take(60).ToListAsync();
        return Ok(filas.Select(Dto).ToList());
    }

    /// <summary>Sube UNA foto (el celu manda varias de a una). Se procesa con las opciones automáticas.</summary>
    [HttpPost("{token}/subir")]
    [RequestSizeLimit(25 * 1024 * 1024)]
    public async Task<IActionResult> Subir(string token, IFormFile file, [FromForm] string? quien)
    {
        if (!await FotoGaleriaService.LinkValidoAsync(_db, token))
            return NotFound(new { mensaje = "Este link ya no funciona. Pedí el nuevo (botón 📷 Fotos del sistema)." });
        var nombre = new string((quien ?? "").Trim().Where(c => char.IsLetterOrDigit(c) || c == ' ').Take(30).ToArray()).Trim().ToUpperInvariant();
        if (nombre.Length == 0) return BadRequest(new { mensaje = "Elegí quién sos antes de sacar fotos." });

        var (g, error) = await FotoGaleriaService.SubirAsync(_db, file, new FotoChromaService.Opciones(), Prefijo + nombre);
        if (g is null) return BadRequest(new { mensaje = error });
        return Ok(Dto(g));
    }
}
