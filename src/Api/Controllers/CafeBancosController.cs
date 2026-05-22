using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Catalogo maestro de bancos. CRUD basico para administracion. Usado como FK por
/// Cafe_Cheques (cheques manuales) y Cafe_ChequesBanco (e-cheqs del extracto bancario).
/// </summary>
[ApiController]
[Route("api/cafe/bancos")]
[Authorize]
public class CafeBancosController : ControllerBase
{
    private readonly AppDbContext _db;

    public CafeBancosController(AppDbContext db) { _db = db; }

    public record BancoDto(int Id, string Nombre, string? Alias, string? Cuit,
        bool IsActive, int SortOrder, int UsoEnCheques, int UsoEnEcheqs);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool incluirInactivos = false)
    {
        var q = _db.CafeBancos.AsQueryable();
        if (!incluirInactivos) q = q.Where(b => b.IsActive);
        var bancos = await q.OrderBy(b => b.SortOrder).ThenBy(b => b.Alias ?? b.Nombre).ToListAsync();
        // Contar uso para el operador (decisiones de activar/desactivar)
        var idsBancos = bancos.Select(b => b.Id).ToList();
        var usoCheques = await _db.CafeCheques
            .Where(c => c.BancoId.HasValue && idsBancos.Contains(c.BancoId.Value))
            .GroupBy(c => c.BancoId!.Value)
            .Select(g => new { Id = g.Key, Cnt = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Cnt);
        var usoEcheqs = await _db.CafeChequesBanco
            .Where(c => c.BancoId.HasValue && idsBancos.Contains(c.BancoId.Value))
            .GroupBy(c => c.BancoId!.Value)
            .Select(g => new { Id = g.Key, Cnt = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Cnt);
        return Ok(bancos.Select(b => new BancoDto(
            b.Id, b.Nombre, b.Alias, b.Cuit, b.IsActive, b.SortOrder,
            usoCheques.GetValueOrDefault(b.Id, 0),
            usoEcheqs.GetValueOrDefault(b.Id, 0))).ToList());
    }

    public record CreateBancoRequest(string Nombre, string? Alias, string? Cuit, int? SortOrder);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBancoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre obligatorio" });
        var nombre = req.Nombre.Trim();
        // Anti-duplicado por nombre canonico exacto
        if (await _db.CafeBancos.AnyAsync(b => b.Nombre == nombre))
            return Conflict(new { error = $"Ya existe un banco con ese nombre: {nombre}" });
        var b = new CafeBanco
        {
            Nombre = nombre,
            Alias = string.IsNullOrWhiteSpace(req.Alias) ? null : req.Alias.Trim(),
            Cuit = string.IsNullOrWhiteSpace(req.Cuit) ? null : req.Cuit.Trim(),
            SortOrder = req.SortOrder ?? 999, // por default al final
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeBancos.Add(b);
        await _db.SaveChangesAsync();
        return Ok(new BancoDto(b.Id, b.Nombre, b.Alias, b.Cuit, b.IsActive, b.SortOrder, 0, 0));
    }

    public record UpdateBancoRequest(string? Nombre, string? Alias, string? Cuit, bool? IsActive, int? SortOrder);

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateBancoRequest req)
    {
        var b = await _db.CafeBancos.FindAsync(id);
        if (b is null) return NotFound();
        if (req.Nombre is not null)
        {
            var nombre = req.Nombre.Trim();
            if (string.IsNullOrWhiteSpace(nombre)) return BadRequest(new { error = "Nombre no puede ser vacío" });
            if (await _db.CafeBancos.AnyAsync(x => x.Id != id && x.Nombre == nombre))
                return Conflict(new { error = $"Ya existe otro banco con ese nombre: {nombre}" });
            b.Nombre = nombre;
        }
        if (req.Alias is not null) b.Alias = string.IsNullOrWhiteSpace(req.Alias) ? null : req.Alias.Trim();
        if (req.Cuit is not null) b.Cuit = string.IsNullOrWhiteSpace(req.Cuit) ? null : req.Cuit.Trim();
        if (req.IsActive.HasValue) b.IsActive = req.IsActive.Value;
        if (req.SortOrder.HasValue) b.SortOrder = req.SortOrder.Value;
        b.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new BancoDto(b.Id, b.Nombre, b.Alias, b.Cuit, b.IsActive, b.SortOrder, 0, 0));
    }

    /// <summary>Elimina solo si no esta usado en ningun cheque ni e-cheq. Sino, sugerir desactivar.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var b = await _db.CafeBancos.FindAsync(id);
        if (b is null) return NotFound();
        var usado = await _db.CafeCheques.AnyAsync(c => c.BancoId == id)
                 || await _db.CafeChequesBanco.AnyAsync(c => c.BancoId == id);
        if (usado) return BadRequest(new { error = "El banco está en uso por cheques o e-cheqs. Desactivalo en vez de borrar." });
        _db.CafeBancos.Remove(b);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
