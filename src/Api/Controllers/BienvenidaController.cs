using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-10-29: ventanita de bienvenida. Se abre sola la primera vez que cada persona (operador del
/// PIN) entra en el día; después queda un globito ☀ para volver a abrirla. Los bloques los arma
/// <see cref="BienvenidaService"/>.
/// </summary>
[ApiController]
[Route("api/bienvenida")]
[Authorize]
public class BienvenidaController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly BienvenidaService _service;

    public BienvenidaController(AppDbContext db, BienvenidaService service)
    {
        _db = db;
        _service = service;
    }

    private static string Norm(string? persona) => (persona ?? "").Trim().ToUpperInvariant();

    public record BienvenidaDto(bool AbrirSola, List<BienvenidaBloqueDto> Bloques);

    /// <summary>Los bloques de esta persona + si hoy todavía no se le abrió sola.</summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string persona)
    {
        var p = Norm(persona);
        if (p.Length == 0) return BadRequest(new { error = "Falta la persona" });
        var hoy = PanoramaService.AhoraAr().Date;
        var vista = await _db.BienvenidaVistas.AsNoTracking().FirstOrDefaultAsync(v => v.Persona == p);
        var abrirSola = vista is null || vista.UltimaFecha < hoy;
        return Ok(new BienvenidaDto(abrirSola, await _service.ArmarAsync(p)));
    }

    /// <summary>Marca que hoy ya se le abrió sola (no vuelve a saltar hasta mañana).</summary>
    [HttpPost("vista")]
    public async Task<IActionResult> MarcarVista([FromQuery] string persona)
    {
        var p = Norm(persona);
        if (p.Length == 0) return BadRequest(new { error = "Falta la persona" });
        var hoy = PanoramaService.AhoraAr().Date;
        var vista = await _db.BienvenidaVistas.FirstOrDefaultAsync(v => v.Persona == p);
        if (vista is null) _db.BienvenidaVistas.Add(new BienvenidaVista { Persona = p, UltimaFecha = hoy });
        else vista.UltimaFecha = hoy;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    public record RecordatorioDto(int Id, string Texto, DateTime? Fecha, string? Para, string De, bool Hecho);

    /// <summary>Lo que le toca ver a la persona: lo suyo + lo "para todos" con fecha hasta hoy (o sin
    /// fecha) y lo que vence en los próximos 7 días. Los tildados hoy siguen apareciendo (tachados).</summary>
    [HttpGet("recordatorios")]
    public async Task<IActionResult> Recordatorios([FromQuery] string persona)
    {
        var p = Norm(persona);
        var hoy = PanoramaService.AhoraAr().Date;
        var hasta = hoy.AddDays(7);
        var inicioHoyUtc = hoy.AddHours(3);
        var lista = await _db.BienvenidaRecordatorios.AsNoTracking()
            .Where(r => (r.Para == null || r.Para == p)
                && (!r.Hecho || r.HechoAt >= inicioHoyUtc)
                && (r.Fecha == null || r.Fecha <= hasta))
            .OrderBy(r => r.Hecho).ThenBy(r => r.Fecha ?? hoy).ThenBy(r => r.Id)
            .Select(r => new RecordatorioDto(r.Id, r.Texto, r.Fecha, r.Para, r.De, r.Hecho))
            .ToListAsync();
        return Ok(lista);
    }

    public record NuevoRecordatorioRequest(string Texto, DateTime? Fecha, string? Para, string De);

    [HttpPost("recordatorios")]
    public async Task<IActionResult> Agregar([FromBody] NuevoRecordatorioRequest req)
    {
        var texto = (req.Texto ?? "").Trim();
        if (texto.Length == 0) return BadRequest(new { error = "Escribí algo" });
        if (texto.Length > 500) texto = texto[..500];
        var para = Norm(req.Para);
        var r = new BienvenidaRecordatorio
        {
            Texto = texto,
            Fecha = req.Fecha?.Date,
            Para = para.Length == 0 ? null : para,
            De = Norm(req.De),
        };
        _db.BienvenidaRecordatorios.Add(r);
        await _db.SaveChangesAsync();
        return Ok(new RecordatorioDto(r.Id, r.Texto, r.Fecha, r.Para, r.De, r.Hecho));
    }

    /// <summary>Tilda / destilda. Guarda quién lo marcó.</summary>
    [HttpPost("recordatorios/{id:int}/hecho")]
    public async Task<IActionResult> Hecho(int id, [FromQuery] bool hecho, [FromQuery] string persona)
    {
        var r = await _db.BienvenidaRecordatorios.FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return NotFound();
        r.Hecho = hecho;
        r.HechoAt = hecho ? DateTime.UtcNow : null;
        r.HechoPor = hecho ? Norm(persona) : null;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpDelete("recordatorios/{id:int}")]
    public async Task<IActionResult> Borrar(int id)
    {
        var r = await _db.BienvenidaRecordatorios.FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return NotFound();
        _db.BienvenidaRecordatorios.Remove(r);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
