using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/calendario")]
[Authorize]
public class CafeCalendarioController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafeCalendarioController(AppDbContext db) { _db = db; }

    public record CalendarioNotaDto(int Id, DateTime Fecha, string Titulo, string? Descripcion,
        decimal? Importe, string? Color, string? CreadoPor, DateTime CreatedAt);
    public record CrearNotaRequest(DateTime Fecha, string Titulo, string? Descripcion, decimal? Importe, string? Color, string? CreadoPor);

    [HttpGet("notas")]
    public async Task<IActionResult> ListarNotas([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta)
    {
        var q = _db.CafeCalendarioNotas.AsQueryable();
        if (desde.HasValue) q = q.Where(n => n.Fecha >= desde.Value.Date);
        if (hasta.HasValue) q = q.Where(n => n.Fecha <= hasta.Value.Date);
        var list = await q.OrderBy(n => n.Fecha).ThenBy(n => n.Id)
            .Select(n => new CalendarioNotaDto(n.Id, n.Fecha, n.Titulo, n.Descripcion, n.Importe, n.Color, n.CreadoPor, n.CreatedAt))
            .ToListAsync();
        return Ok(list);
    }

    [HttpPost("notas")]
    public async Task<IActionResult> Crear([FromBody] CrearNotaRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Titulo)) return BadRequest(new { error = "Título requerido" });
        var nota = new CafeCalendarioNota
        {
            Fecha = req.Fecha.Date,
            Titulo = req.Titulo.Trim(),
            Descripcion = string.IsNullOrWhiteSpace(req.Descripcion) ? null : req.Descripcion.Trim(),
            Importe = req.Importe,
            Color = string.IsNullOrWhiteSpace(req.Color) ? null : req.Color.Trim(),
            CreadoPor = string.IsNullOrWhiteSpace(req.CreadoPor) ? null : req.CreadoPor.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeCalendarioNotas.Add(nota);
        await _db.SaveChangesAsync();
        return Ok(new CalendarioNotaDto(nota.Id, nota.Fecha, nota.Titulo, nota.Descripcion, nota.Importe, nota.Color, nota.CreadoPor, nota.CreatedAt));
    }

    [HttpDelete("notas/{id:int}")]
    public async Task<IActionResult> Borrar(int id)
    {
        var n = await _db.CafeCalendarioNotas.FirstOrDefaultAsync(x => x.Id == id);
        if (n is null) return NotFound();
        _db.CafeCalendarioNotas.Remove(n);
        await _db.SaveChangesAsync();
        return Ok();
    }
}
