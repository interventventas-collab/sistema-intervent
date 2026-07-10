using System.Security.Claims;
using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-07-10: Motor de alertas configurables ("Mis Alertas"). Las alertas son COMPARTIDAS
/// por rol (admin / oficina / deposito) — no son por-usuario. Cada alerta guarda en Alcance
/// qué roles la ven; por default "admin,oficina" (deposito afuera por ahora). Así el usuario
/// las ve entre con el login que entre, mientras su rol esté incluido.
/// El robot MisAlertasBackgroundService es quien las dispara.
/// </summary>
[ApiController]
[Route("api/mis-alertas")]
[Authorize]
public class MisAlertasController : ControllerBase
{
    private readonly AppDbContext _db;
    public MisAlertasController(AppDbContext db) { _db = db; }

    private static readonly string[] TiposValidos = { "SHELL_BAJO", "BANCO_BAJO", "CHEQUE_VENCE", "FECHA_MES" };
    private static readonly string[] RolesValidos = { "admin", "oficina", "deposito" };

    private int? GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        return claim is not null && int.TryParse(claim.Value, out var id) ? id : null;
    }

    /// <summary>Bucket de rol del usuario actual (admin | oficina | deposito | otro), calculado
    /// igual que el frontend: admin si su rol es "admin"; si no, según sus permisos.</summary>
    private async Task<string> GetBucketAsync()
    {
        var uid = GetUserId();
        if (uid is null) return "otro";
        var user = await _db.Users.Include(u => u.RoleNav).FirstOrDefaultAsync(u => u.Id == uid.Value);
        if (user is null) return "otro";
        var roleName = user.RoleNav?.Name ?? user.Role;
        if (roleName.Equals("admin", StringComparison.OrdinalIgnoreCase)) return "admin";
        var perms = await _db.RolePermissions
            .Where(rp => rp.RoleId == user.RoleId)
            .Select(rp => rp.MenuKey)
            .ToListAsync();
        if (perms.Contains("oficina")) return "oficina";
        if (perms.Contains("deposito")) return "deposito";
        return "otro";
    }

    public record AlertaDto(int Id, string Tipo, decimal? Umbral, string Mensaje,
        bool CanalCampanita, bool CanalWhatsApp, bool CanalCorreo, bool Activa, List<string> Roles,
        bool EstaDisparada, bool Vista, string? UltimoDetalle, DateTime? DisparadaAt);

    public record AlertaUpsertRequest(string Tipo, decimal? Umbral, string Mensaje,
        bool CanalCampanita, bool CanalWhatsApp, bool CanalCorreo, bool Activa, List<string>? Roles);

    private static List<string> ParseRoles(string? alcance)
        => string.IsNullOrWhiteSpace(alcance)
            ? new List<string>()
            : alcance.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static AlertaDto Map(MisAlerta a) => new(
        a.Id, a.Tipo, a.Umbral, a.Mensaje,
        a.CanalCampanita, a.CanalWhatsApp, a.CanalCorreo, a.Activa, ParseRoles(a.Alcance),
        a.EstaDisparada, a.Vista, a.UltimoDetalle, a.DisparadaAt);

    /// <summary>Valida y normaliza los roles del selector. Devuelve el CSV a guardar o null si hay error.</summary>
    private static (string? alcance, string? error) NormalizarRoles(List<string>? roles)
    {
        var limpios = (roles ?? new List<string>())
            .Select(r => r?.Trim().ToLowerInvariant() ?? "")
            .Where(r => RolesValidos.Contains(r))
            .Distinct()
            .ToList();
        if (limpios.Count == 0) return (null, "Elegí al menos quién ve la alerta (Admin, Oficina o Depósito)");
        return (string.Join(",", limpios), null);
    }

    private static string? Validar(AlertaUpsertRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Tipo) || !TiposValidos.Contains(r.Tipo))
            return "Tipo de alerta inválido";
        if (string.IsNullOrWhiteSpace(r.Mensaje))
            return "El mensaje es obligatorio";
        if (r.Umbral is null || r.Umbral <= 0)
            return "Falta el valor (monto, días o día del mes)";
        if (r.Tipo == "FECHA_MES" && (r.Umbral < 1 || r.Umbral > 31))
            return "El día del mes tiene que estar entre 1 y 31";
        return null;
    }

    // ---------- CRUD (compartidas por rol) ----------
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var bucket = await GetBucketAsync();
        var rows = await _db.MisAlertas
            .Where(a => a.Alcance.Contains(bucket))
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();
        return Ok(rows.Select(Map));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AlertaUpsertRequest r)
    {
        var uid = GetUserId();
        if (uid is null) return Unauthorized();
        var err = Validar(r);
        if (err is not null) return BadRequest(new { error = err });
        var (alcance, errRoles) = NormalizarRoles(r.Roles);
        if (errRoles is not null) return BadRequest(new { error = errRoles });

        var a = new MisAlerta
        {
            UserId = uid.Value,
            Tipo = r.Tipo,
            Umbral = r.Umbral,
            Mensaje = r.Mensaje.Trim(),
            CanalCampanita = r.CanalCampanita,
            CanalWhatsApp = r.CanalWhatsApp,
            CanalCorreo = r.CanalCorreo,
            Activa = r.Activa,
            Alcance = alcance!
        };
        _db.MisAlertas.Add(a);
        await _db.SaveChangesAsync();
        return Ok(Map(a));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] AlertaUpsertRequest r)
    {
        var bucket = await GetBucketAsync();
        var a = await _db.MisAlertas.FirstOrDefaultAsync(x => x.Id == id && x.Alcance.Contains(bucket));
        if (a is null) return NotFound();
        var err = Validar(r);
        if (err is not null) return BadRequest(new { error = err });
        var (alcance, errRoles) = NormalizarRoles(r.Roles);
        if (errRoles is not null) return BadRequest(new { error = errRoles });

        // Si cambia la definicion, reseteamos el estado de disparo para que vuelva a evaluarse limpio.
        var redefinio = a.Tipo != r.Tipo || a.Umbral != r.Umbral;
        a.Tipo = r.Tipo;
        a.Umbral = r.Umbral;
        a.Mensaje = r.Mensaje.Trim();
        a.CanalCampanita = r.CanalCampanita;
        a.CanalWhatsApp = r.CanalWhatsApp;
        a.CanalCorreo = r.CanalCorreo;
        a.Activa = r.Activa;
        a.Alcance = alcance!;
        if (redefinio) { a.EstaDisparada = false; a.Vista = false; a.UltimoDetalle = null; }
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(a));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var bucket = await GetBucketAsync();
        var a = await _db.MisAlertas.FirstOrDefaultAsync(x => x.Id == id && x.Alcance.Contains(bucket));
        if (a is null) return NotFound();
        _db.MisAlertas.Remove(a);
        await _db.SaveChangesAsync();
        return Ok();
    }

    /// <summary>Prender/apagar rapido desde la lista (interruptor).</summary>
    [HttpPost("{id:int}/toggle")]
    public async Task<IActionResult> Toggle(int id)
    {
        var bucket = await GetBucketAsync();
        var a = await _db.MisAlertas.FirstOrDefaultAsync(x => x.Id == id && x.Alcance.Contains(bucket));
        if (a is null) return NotFound();
        a.Activa = !a.Activa;
        if (!a.Activa) { a.EstaDisparada = false; a.Vista = false; a.UltimoDetalle = null; }
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(a));
    }

    // ---------- Campanita de la topbar ----------
    public record AlertaDisparadaDto(int Id, string Tipo, string Mensaje, string? Detalle, DateTime? DisparadaAt, bool Vista);
    public record AlertasBellDto(int NoVistas, List<AlertaDisparadaDto> Disparadas);

    /// <summary>Alertas disparadas ahora que el rol del usuario puede ver (para la campanita).</summary>
    [HttpGet("disparadas")]
    public async Task<IActionResult> Disparadas()
    {
        var bucket = await GetBucketAsync();
        var rows = await _db.MisAlertas
            .Where(a => a.Activa && a.EstaDisparada && a.Alcance.Contains(bucket))
            .OrderByDescending(a => a.DisparadaAt)
            .ToListAsync();
        var lista = rows.Select(a => new AlertaDisparadaDto(a.Id, a.Tipo, a.Mensaje, a.UltimoDetalle, a.DisparadaAt, a.Vista)).ToList();
        return Ok(new AlertasBellDto(lista.Count(a => !a.Vista), lista));
    }

    /// <summary>Marca como vistas las alertas disparadas que el rol del usuario ve.</summary>
    [HttpPost("marcar-vistas")]
    public async Task<IActionResult> MarcarVistas()
    {
        var bucket = await GetBucketAsync();
        var rows = await _db.MisAlertas
            .Where(a => a.EstaDisparada && !a.Vista && a.Alcance.Contains(bucket))
            .ToListAsync();
        foreach (var a in rows) a.Vista = true;
        if (rows.Count > 0) await _db.SaveChangesAsync();
        return Ok(new { marcadas = rows.Count });
    }
}
