using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/reglas-precios")]
[Authorize]
public class CafeReglasPreciosController : ControllerBase
{
    private readonly AppDbContext _db;
    private static readonly string[] TiposClienteValidos = { "BAR", "OTRO" };
    private static readonly string[] CategoriasValidas = { "CAFE", "OTROS" };

    public CafeReglasPreciosController(AppDbContext db) { _db = db; }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var reglas = await _db.CafeReglasPrecios.Include(r => r.MarcaNav)
            .OrderBy(r => r.TipoCliente).ThenBy(r => r.Categoria).ThenBy(r => r.MarcaId)
            .ToListAsync();
        var dtos = reglas.Select(r => new CafeReglaPrecioDto(
            r.Id, r.TipoCliente, r.Categoria, r.MarcaId, r.MarcaNav?.Nombre,
            r.DescuentoPct)).ToList();
        return Ok(new
        {
            tiposCliente = TiposClienteValidos,
            categorias = CategoriasValidas,
            reglas = dtos
        });
    }

    // Upsert: crea o actualiza la regla por (TipoCliente, Categoria, MarcaId).
    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] UpsertReglaPrecioRequest req)
    {
        var tipo = (req.TipoCliente ?? "").Trim().ToUpperInvariant();
        var cat = (req.Categoria ?? "").Trim().ToUpperInvariant();
        if (!TiposClienteValidos.Contains(tipo))
            return BadRequest(new { error = $"Tipo invalido. Validos: {string.Join(", ", TiposClienteValidos)}" });
        if (!CategoriasValidas.Contains(cat))
            return BadRequest(new { error = $"Categoria invalida. Validas: {string.Join(", ", CategoriasValidas)}" });
        if (req.DescuentoPct < 0 || req.DescuentoPct > 100)
            return BadRequest(new { error = "El descuento debe estar entre 0 y 100." });
        if (req.MarcaId.HasValue)
        {
            if (!await _db.CafeMarcas.AnyAsync(m => m.Id == req.MarcaId.Value))
                return NotFound(new { error = "Marca no encontrada" });
        }

        var existing = await _db.CafeReglasPrecios.FirstOrDefaultAsync(r =>
            r.TipoCliente == tipo && r.Categoria == cat && r.MarcaId == req.MarcaId);

        if (existing is null)
        {
            existing = new CafeReglaPrecio
            {
                TipoCliente = tipo, Categoria = cat, MarcaId = req.MarcaId,
                DescuentoPct = Math.Round(req.DescuentoPct, 2),
                CreatedAt = DateTime.UtcNow
            };
            _db.CafeReglasPrecios.Add(existing);
        }
        else
        {
            existing.DescuentoPct = Math.Round(req.DescuentoPct, 2);
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new { existing.Id, existing.TipoCliente, existing.Categoria, existing.MarcaId, existing.DescuentoPct });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await _db.CafeReglasPrecios.FindAsync(id);
        if (r is null) return NotFound();
        // No permitir borrar reglas generales (sin marca) — son base. Solo overrides.
        if (!r.MarcaId.HasValue)
            return BadRequest(new { error = "No se puede borrar una regla general. Editá el % a 0 si querés anularla." });
        _db.CafeReglasPrecios.Remove(r);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }
}
