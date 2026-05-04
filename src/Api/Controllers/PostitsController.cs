using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/postits")]
[Authorize]
public class PostitsController : ControllerBase
{
    private readonly AppDbContext _db;
    private static readonly string[] ColoresValidos = { "amarillo", "rosa", "verde", "azul", "naranja" };

    public PostitsController(AppDbContext db) { _db = db; }

    private static PostitDto Map(Postit p) => new(p.Id, p.Texto, p.Color, p.CreadoPor, p.Scope, p.CreatedAt, p.UpdatedAt);

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? scope = null)
    {
        var s = string.IsNullOrWhiteSpace(scope) ? "dashboard" : scope.Trim().ToLowerInvariant();
        var list = await _db.Postits
            .Where(p => p.Scope == s)
            .OrderByDescending(p => p.CreatedAt)
            .Take(50)
            .ToListAsync();
        return Ok(list.Select(Map).ToList());
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePostitRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Texto))
            return BadRequest(new { error = "El texto es obligatorio" });
        var color = NormColor(req.Color);
        var scope = string.IsNullOrWhiteSpace(req.Scope) ? "dashboard" : req.Scope.Trim().ToLowerInvariant();
        var p = new Postit
        {
            Texto = req.Texto.Trim(),
            Color = color,
            CreadoPor = string.IsNullOrWhiteSpace(req.CreadoPor) ? null : req.CreadoPor.Trim(),
            Scope = scope,
            CreatedAt = DateTime.UtcNow
        };
        _db.Postits.Add(p);
        await _db.SaveChangesAsync();
        return Ok(Map(p));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdatePostitRequest req)
    {
        var p = await _db.Postits.FindAsync(id);
        if (p is null) return NotFound(new { error = "Postit no encontrado" });
        if (req.Texto is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Texto)) return BadRequest(new { error = "El texto no puede ser vacio" });
            p.Texto = req.Texto.Trim();
        }
        if (req.Color is not null) p.Color = NormColor(req.Color);
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(p));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var p = await _db.Postits.FindAsync(id);
        if (p is null) return NotFound(new { error = "Postit no encontrado" });
        _db.Postits.Remove(p);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    private static string NormColor(string? c)
    {
        if (string.IsNullOrWhiteSpace(c)) return "amarillo";
        var v = c.Trim().ToLowerInvariant();
        return ColoresValidos.Contains(v) ? v : "amarillo";
    }
}
