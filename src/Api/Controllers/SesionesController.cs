using System.Security.Claims;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-09-17 — "Sesiones abiertas": quién está adentro del sistema, desde qué aparato y desde
/// dónde, con el botón para echarlo.
///
/// Solo admin. No es una pantalla de consulta inofensiva: desde acá se le corta el trabajo a
/// cualquiera, así que el permiso es el mismo que el de Usuarios.
/// </summary>
[ApiController]
[Route("api/sesiones")]
[Authorize]
public class SesionesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly SesionesService _sesiones;

    public SesionesController(AppDbContext db, SesionesService sesiones)
    {
        _db = db; _sesiones = sesiones;
    }

    public record SesionDto(
        int Id,
        string Nombre,
        string Tipo,
        string Dispositivo,
        string? Apodo,
        string Lugar,
        string? Ip,
        DateTime EntroAr,
        DateTime UltimaActividadAr,
        DateTime ExpiraAr,
        bool EsLaMia,
        bool AparatoNuevo,
        int? UserId,
        // Solo en el historial
        DateTime? CerradaAr,
        string? CerradaPor,
        string? CerradaMotivo);

    public record ListadoDto(List<SesionDto> Abiertas, List<SesionDto> Cerradas);

    /// <summary>Hora argentina para mostrar. El sistema guarda todo en UTC.</summary>
    private static DateTime Ar(DateTime utc) => utc.AddHours(-3);

    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] int diasHistorial = 7)
    {
        if (!EsAdmin()) return Forbid();

        var redes = await _sesiones.RedesConocidasAsync();
        var miJti = User.FindFirst(SesionesService.ClaimJti)?.Value;
        var ahora = DateTime.UtcNow;
        var desde = ahora.AddDays(-Math.Clamp(diasHistorial, 1, 90));

        var filas = await _db.UserSessions.AsNoTracking()
            .Where(s => s.CerradaAt == null || s.CerradaAt >= desde)
            .OrderByDescending(s => s.UltimaActividadAt)
            .ToListAsync();

        SesionDto Mapear(UserSession s) => new(
            s.Id, s.Nombre, s.Tipo, s.Dispositivo, s.Apodo,
            SesionesService.Lugar(s.IpUltima ?? s.IpCreacion, redes),
            s.IpUltima ?? s.IpCreacion,
            Ar(s.CreatedAt), Ar(s.UltimaActividadAt), Ar(s.ExpiraAt),
            s.Jti == miJti, s.AparatoNuevo, s.UserId,
            s.CerradaAt is null ? null : Ar(s.CerradaAt.Value), s.CerradaPor, s.CerradaMotivo);

        // Una sesion vencida no esta "abierta" aunque nadie la haya cerrado a mano.
        var abiertas = filas.Where(s => s.CerradaAt == null && s.ExpiraAt > ahora)
                            .Select(Mapear).ToList();
        var cerradas = filas.Where(s => s.CerradaAt != null || s.ExpiraAt <= ahora)
                            .OrderByDescending(s => s.CerradaAt ?? s.ExpiraAt)
                            .Select(Mapear).ToList();

        return Ok(new ListadoDto(abiertas, cerradas));
    }

    [HttpPost("{id:int}/cerrar")]
    public async Task<IActionResult> Cerrar(int id)
    {
        if (!EsAdmin()) return Forbid();
        var ok = await _sesiones.CerrarAsync(id, Quien(), UserSession.MotivoEchada);
        if (!ok) return NotFound(new { message = "Esa sesion ya no estaba abierta" });
        return Ok(new { ok = true });
    }

    /// <summary>Echa a un usuario de todos los aparatos de una.</summary>
    [HttpPost("usuario/{userId:int}/cerrar-todas")]
    public async Task<IActionResult> CerrarTodas(int userId)
    {
        if (!EsAdmin()) return Forbid();
        var n = await _sesiones.CerrarTodasDelUsuarioAsync(userId, Quien(), UserSession.MotivoEchada);
        return Ok(new { ok = true, cerradas = n });
    }

    public record ApodoRequest(string? Apodo);

    /// <summary>El apodo del aparato ("La compu del deposito"). El navegador nunca dice el nombre
    /// real del aparato, asi que esto se carga a mano y queda pegado a ese aparato.</summary>
    [HttpPut("{id:int}/apodo")]
    public async Task<IActionResult> PonerApodo(int id, [FromBody] ApodoRequest req)
    {
        if (!EsAdmin()) return Forbid();

        var s = await _db.UserSessions.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound(new { message = "Sesion no encontrada" });

        var apodo = string.IsNullOrWhiteSpace(req.Apodo) ? null : req.Apodo.Trim();
        if (apodo is { Length: > 80 }) apodo = apodo[..80];

        // El apodo es del APARATO, no de la sesion: se lo ponemos a todas las filas de ese aparato
        // para que no se pierda cuando esa persona vuelva a entrar.
        var hermanas = await _db.UserSessions
            .Where(x => x.Nombre == s.Nombre && x.Tipo == s.Tipo && x.Huella == s.Huella)
            .ToListAsync();
        foreach (var h in hermanas) h.Apodo = apodo;

        await _db.SaveChangesAsync();
        return Ok(new { ok = true, apodo });
    }

    // ------------------------------------------------------------------
    // Redes conocidas (Oficina / Deposito / Afuera)
    // ------------------------------------------------------------------

    [HttpGet("redes")]
    public async Task<IActionResult> Redes()
    {
        if (!EsAdmin()) return Forbid();
        return Ok(new
        {
            redes = await _sesiones.RedesConocidasAsync(),
            // Para que no tenga que adivinar que numero poner: le mostramos desde donde esta
            // entrando el AHORA. Si esta en la oficina, ese es el numero de la oficina.
            miIp = SesionesService.IpDe(HttpContext)
        });
    }

    public record RedesRequest(List<SesionesService.RedConocida> Redes);

    [HttpPut("redes")]
    public async Task<IActionResult> GuardarRedes([FromBody] RedesRequest req)
    {
        if (!EsAdmin()) return Forbid();
        await _sesiones.GuardarRedesConocidasAsync(req.Redes ?? new());
        return Ok(new { ok = true });
    }

    private bool EsAdmin() => User.FindFirst(ClaimTypes.Role)?.Value == "admin";
    private string Quien() => User.Identity?.Name ?? "administracion";
}
