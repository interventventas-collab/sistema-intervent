using System.Security.Claims;
using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>Avisos/novedades que se muestran a los usuarios (ej: el globito de bienvenida con los
/// atajos nuevos). Cada usuario puede ocultar un aviso ("No volver a mostrar") y queda guardado
/// por su cuenta, así no le vuelve a aparecer en ningún dispositivo. 2026-07-08.</summary>
[ApiController]
[Route("api/notices")]
[Authorize]
public class NoticesController : ControllerBase
{
    private readonly AppDbContext _db;
    public NoticesController(AppDbContext db) { _db = db; }

    // Aviso vigente: atajos de teclado + 3 botones. Se muestra hasta la fecha de corte (1 mes)
    // o hasta que el usuario lo oculte.
    private const string WelcomeKey = "novedad-atajos-2026-07";
    private static readonly DateTime WelcomeUntil = new DateTime(2026, 8, 8);

    private int? GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        return claim is not null && int.TryParse(claim.Value, out var id) ? id : null;
    }

    /// <summary>¿Hay que mostrarle el globito de bienvenida a este usuario?</summary>
    [HttpGet("welcome")]
    public async Task<IActionResult> GetWelcome()
    {
        if (DateTime.UtcNow.Date > WelcomeUntil.Date) return Ok(new { show = false });
        var uid = GetUserId();
        if (uid is null) return Ok(new { show = false });
        var dismissed = await _db.UserNoticeDismissals
            .AnyAsync(d => d.UserId == uid.Value && d.NoticeKey == WelcomeKey);
        return Ok(new { show = !dismissed });
    }

    /// <summary>El usuario tildó "No volver a mostrar" — se guarda por su cuenta.</summary>
    [HttpPost("welcome/dismiss")]
    public async Task<IActionResult> DismissWelcome()
    {
        var uid = GetUserId();
        if (uid is null) return Unauthorized();
        var exists = await _db.UserNoticeDismissals
            .AnyAsync(d => d.UserId == uid.Value && d.NoticeKey == WelcomeKey);
        if (!exists)
        {
            _db.UserNoticeDismissals.Add(new UserNoticeDismissal { UserId = uid.Value, NoticeKey = WelcomeKey });
            await _db.SaveChangesAsync();
        }
        return Ok(new { ok = true });
    }
}
