using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-08-26: alarmas del reloj. Cada uno ve y maneja SOLO las suyas — el dueño nunca
/// manda a qué alarmas quiere acceder, se deducen acá de quién está logueado y de qué PIN
/// está firmado. Ver <see cref="Alarma"/> para la regla completa de a quién pertenece cada una.
/// </summary>
[ApiController]
[Route("api/alarmas")]
[Authorize]
public class AlarmasController : ControllerBase
{
    private readonly AppDbContext _db;
    public AlarmasController(AppDbContext db) { _db = db; }

    public record AlarmaDto(int Id, DateTime Cuando, string? Nota, string Sonido, string Estado,
        string? CreadaPor, bool Vencida);
    public record CrearRequest(DateTime Cuando, string? Nota, string? Sonido);

    /// <summary>GET — mis alarmas pendientes. Las "vencidas" son las que YA tienen que sonar:
    /// incluye las que se pasaron mientras esta persona no estaba (por eso no se pierden).</summary>
    [HttpGet]
    public async Task<IActionResult> Mias()
    {
        var duenio = DuenioActual();
        var ahora = DateTime.UtcNow;
        var filas = await _db.Alarmas.AsNoTracking()
            .Where(a => a.Duenio == duenio && a.Estado == Alarma.EstadoPendiente)
            .OrderBy(a => a.Cuando)
            .Take(50)
            .ToListAsync();

        return Ok(new
        {
            duenio,
            quien = NombreLindo(duenio),
            alarmas = filas.Select(a => ToDto(a, ahora)).ToList()
        });
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearRequest req)
    {
        var cuando = AUtc(req.Cuando);
        if (cuando < DateTime.UtcNow.AddMinutes(-1))
            return BadRequest(new { error = "Esa hora ya pasó. Elegí un horario que venga." });
        if (cuando > DateTime.UtcNow.AddDays(60))
            return BadRequest(new { error = "No se puede poner una alarma a más de 60 días." });

        var duenio = DuenioActual();
        var fila = new Alarma
        {
            Duenio = duenio,
            Cuando = cuando,
            Nota = string.IsNullOrWhiteSpace(req.Nota) ? null : req.Nota.Trim(),
            Sonido = string.IsNullOrWhiteSpace(req.Sonido) ? "despertador" : req.Sonido.Trim(),
            Estado = Alarma.EstadoPendiente,
            CreadaPor = NombreLindo(duenio),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Alarmas.Add(fila);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, alarma = ToDto(fila, DateTime.UtcNow) });
    }

    /// <summary>POST /{id}/apagar — sonó y la atendieron (o la frenaron). Deja de sonar.</summary>
    [HttpPost("{id:int}/apagar")]
    public async Task<IActionResult> Apagar(int id)
    {
        var fila = await MiaAsync(id);
        if (fila == null) return NotFound(new { error = "No existe esa alarma" });
        fila.Estado = Alarma.EstadoApagada;
        fila.ApagadaAt = DateTime.UtcNow;
        fila.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>DELETE — borrarla antes de que suene.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Borrar(int id)
    {
        var fila = await MiaAsync(id);
        if (fila == null) return NotFound(new { error = "No existe esa alarma" });
        _db.Alarmas.Remove(fila);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Solo se puede tocar lo propio: se filtra por dueño, no por Id suelto.</summary>
    private Task<Alarma?> MiaAsync(int id)
    {
        var duenio = DuenioActual();
        return _db.Alarmas.FirstOrDefaultAsync(a => a.Id == id && a.Duenio == duenio)!;
    }

    /// <summary>
    /// De quién es la alarma que se está creando o mirando.
    ///
    /// Los usuarios con rol admin son los que comparten pantalla entre los tres hermanos, así que
    /// ahí manda el PIN (la persona). Cualquier otra pantalla (DEPOSITO, CONTADORA) lleva UNA sola
    /// lista por cuenta, aunque adentro firmen distintas personas con su PIN.
    /// </summary>
    private string DuenioActual()
    {
        var usuario = (User.Identity?.Name ?? "?").Trim().ToUpperInvariant();
        var esAdmin = User.IsInRole("admin");
        var op = Request.Headers["X-Operator-Name"].FirstOrDefault()?.Trim().ToUpperInvariant();

        if (esAdmin && !string.IsNullOrWhiteSpace(op)) return "op:" + op;
        return "cuenta:" + usuario;
    }

    /// <summary>"op:OSMAR" -> "OSMAR"; "cuenta:DEPOSITO" -> "DEPOSITO".</summary>
    private static string NombreLindo(string duenio)
    {
        var i = duenio.IndexOf(':');
        return i >= 0 ? duenio[(i + 1)..] : duenio;
    }

    private static DateTime AUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc)
    };

    /// <summary>Las fechas salen marcadas como UTC ("Z") para que la pantalla las pase a hora
    /// argentina. Si salieran peladas, el navegador las tomaría como locales.</summary>
    private static AlarmaDto ToDto(Alarma a, DateTime ahora) => new(
        a.Id, AUtc(a.Cuando), a.Nota, a.Sonido, a.Estado, a.CreadaPor, AUtc(a.Cuando) <= ahora);
}
