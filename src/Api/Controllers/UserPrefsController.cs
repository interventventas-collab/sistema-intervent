using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

// 2026-08-13: preferencias por usuario (clave-valor). Reusa la tabla AppSettings con clave
// namespaced por usuario (userpref:{userId}:{key}) para NO tener que crear tablas nuevas
// (que en prod hay que aplicar a mano). Sirve, por ejemplo, para recordar qué columnas
// muestra cada usuario en la pantalla de Publicaciones.
[ApiController]
[Route("api/user-prefs")]
[Authorize]
public class UserPrefsController : ControllerBase
{
    private readonly AppDbContext _db;
    public UserPrefsController(AppDbContext db) { _db = db; }

    private int? CurrentUserId()
    {
        var c = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("sub")?.Value;
        return int.TryParse(c, out var id) ? id : (int?)null;
    }

    public record SetPrefRequest(string? Value);

    [HttpGet("{key}")]
    public async Task<IActionResult> Get(string key)
    {
        var uid = CurrentUserId();
        if (uid is null) return Unauthorized();
        var k = $"userpref:{uid}:{key}";
        var s = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == k);
        return Ok(new { value = s?.Value });
    }

    [HttpPut("{key}")]
    public async Task<IActionResult> Set(string key, [FromBody] SetPrefRequest req)
    {
        var uid = CurrentUserId();
        if (uid is null) return Unauthorized();
        var k = $"userpref:{uid}:{key}";
        var s = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == k);
        if (s is null)
        {
            s = new AppSetting { Key = k, Value = req.Value ?? "", UpdatedAt = DateTime.UtcNow };
            _db.AppSettings.Add(s);
        }
        else
        {
            s.Value = req.Value ?? "";
            s.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
