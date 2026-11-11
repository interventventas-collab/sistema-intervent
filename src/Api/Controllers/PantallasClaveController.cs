using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace Api.Controllers;

/// <summary>
/// 2026-11-11: CLAVE EN PANTALLAS (pedido del dueño: "poder agregarles clave" a los botones grandes;
/// eligió que la clave la pida la PANTALLA, se entre por donde se entre: botón, menú o link).
/// Cada pantalla protegida es una fila de AppSettings "PantallaClave:{dirección}" con el hash BCrypt.
/// La web pide la lista de direcciones protegidas (sin los hashes) y, antes de mostrar una, pide la
/// clave y la verifica acá. Ojo: protege la PANTALLA, no los datos de la API que usa por detrás.
/// </summary>
[ApiController]
[Route("api/pantallas-clave")]
[Authorize]
public class PantallasClaveController : ControllerBase
{
    private const string PREFIJO = "PantallaClave:";
    private readonly AppDbContext _db;

    // Freno simple contra probar claves a lo loco: 8 intentos fallidos por usuario cada 5 minutos.
    private static readonly ConcurrentDictionary<string, (int N, DateTime Desde)> _fallos = new();

    public PantallasClaveController(AppDbContext db) => _db = db;

    public static string Normalizar(string? href)
    {
        var h = (href ?? "").Trim();
        var q = h.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) h = h[..q];
        h = "/" + h.Trim('/');
        return h.ToLowerInvariant();
    }

    /// <summary>Direcciones que piden clave (sin las claves).</summary>
    [HttpGet]
    public async Task<ActionResult<List<string>>> Lista()
    {
        var keys = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith(PREFIJO))
            .Select(s => s.Key).ToListAsync();
        return keys.Select(k => k[PREFIJO.Length..]).ToList();
    }

    public record VerificarReq(string Href, string Clave);

    [HttpPost("verificar")]
    public async Task<IActionResult> Verificar([FromBody] VerificarReq req)
    {
        var quien = User.Identity?.Name ?? HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?";
        if (_fallos.TryGetValue(quien, out var f) && f.N >= 8 && DateTime.UtcNow - f.Desde < TimeSpan.FromMinutes(5))
            return StatusCode(429, new { error = "Demasiados intentos. Esperá 5 minutos." });

        var fila = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == PREFIJO + Normalizar(req.Href));
        if (fila == null) return Ok(new { ok = true });   // ya no tiene clave
        var ok = !string.IsNullOrEmpty(req.Clave) && BCrypt.Net.BCrypt.Verify(req.Clave.Trim(), fila.Value);
        if (ok) _fallos.TryRemove(quien, out _);
        else _fallos.AddOrUpdate(quien, (1, DateTime.UtcNow),
            (_, v) => DateTime.UtcNow - v.Desde > TimeSpan.FromMinutes(5) ? (1, DateTime.UtcNow) : (v.N + 1, v.Desde));
        return Ok(new { ok });
    }

    public record PonerReq(string Href, string? Clave);

    /// <summary>Pone (Clave con texto) o saca (Clave vacía) la clave de una pantalla. Solo admin.</summary>
    [HttpPut]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Poner([FromBody] PonerReq req)
    {
        var key = PREFIJO + Normalizar(req.Href);
        if (key.Length > 100) return BadRequest(new { error = "Dirección demasiado larga" });
        var fila = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (string.IsNullOrWhiteSpace(req.Clave))
        {
            if (fila != null) { _db.AppSettings.Remove(fila); await _db.SaveChangesAsync(); }
            return Ok(new { ok = true });
        }
        if (req.Clave.Trim().Length < 4) return BadRequest(new { error = "La clave tiene que tener al menos 4 caracteres" });
        var hash = BCrypt.Net.BCrypt.HashPassword(req.Clave.Trim());
        if (fila == null) _db.AppSettings.Add(new AppSetting { Key = key, Value = hash, UpdatedAt = DateTime.UtcNow });
        else { fila.Value = hash; fila.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
