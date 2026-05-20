using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>CRUD admin de repartidores (los que entran a /repartidor/{token} con PIN).</summary>
[ApiController]
[Route("api/cafe/repartidores")]
[Authorize]
public class CafeRepartidoresController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafeRepartidoresController(AppDbContext db) { _db = db; }

    public record RepartidorDto(int Id, string Nombre, string? DniUltimos3, bool IsActive);
    public record CrearRequest(string Nombre, string? DniUltimos3);
    public record EditarRequest(string Nombre, string? DniUltimos3, bool IsActive);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var l = await _db.CafeRepartidores.OrderBy(r => r.Nombre)
            .Select(r => new RepartidorDto(r.Id, r.Nombre, r.DniUltimos3, r.IsActive))
            .ToListAsync();
        return Ok(l);
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre requerido" });
        var r = new CafeRepartidor
        {
            Nombre = req.Nombre.Trim(),
            DniUltimos3 = LimpiarPin(req.DniUltimos3),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeRepartidores.Add(r);
        await _db.SaveChangesAsync();
        return Ok(new RepartidorDto(r.Id, r.Nombre, r.DniUltimos3, r.IsActive));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Editar(int id, [FromBody] EditarRequest req)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(req.Nombre)) r.Nombre = req.Nombre.Trim();
        r.DniUltimos3 = LimpiarPin(req.DniUltimos3);
        r.IsActive = req.IsActive;
        r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new RepartidorDto(r.Id, r.Nombre, r.DniUltimos3, r.IsActive));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Borrar(int id)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return NotFound();
        var usadoEnCobranzas = await _db.CafeCobranzasPendientes.AnyAsync(c => c.RepartidorId == id);
        if (usadoEnCobranzas)
        {
            r.IsActive = false;
            r.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(new { soft = true, mensaje = "Repartidor desactivado (tiene cobranzas asociadas, no se puede borrar)" });
        }
        _db.CafeRepartidores.Remove(r);
        await _db.SaveChangesAsync();
        return Ok(new { soft = false });
    }

    private static string? LimpiarPin(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        var digits = new string(p.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : (digits.Length > 3 ? digits[..3] : digits);
    }
}
