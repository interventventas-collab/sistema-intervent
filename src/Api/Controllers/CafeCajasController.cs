using Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// CRUD de Cajas (Cafe → Tesorería → Cajas).
/// Una "caja" es un lugar donde vive plata: Efectivo, MP, Banco Galicia, Cheques en cartera, V_PRIVADO.
/// Configurables por el usuario. Cada caja tiene un saldo inicial editable.
/// El saldo CURRENT se calcula sumando saldo inicial + movimientos (Cobranzas + Egresos).
/// </summary>
[ApiController]
[Route("api/cafe/cajas")]
[Authorize]
public class CafeCajasController : ControllerBase
{
    private readonly AppDbContext _db;

    public CafeCajasController(AppDbContext db) { _db = db; }

    public record CajaDto(
        int Id, string Nombre, string Tipo, decimal SaldoInicial, int Orden,
        bool IsActive, string? Notas, decimal SaldoActual);

    public record CrearCajaRequest(string Nombre, string Tipo, decimal SaldoInicial, int? Orden, string? Notas);
    public record EditarCajaRequest(string Nombre, string Tipo, decimal SaldoInicial, int? Orden, bool IsActive, string? Notas);

    /// <summary>Lista todas las cajas con su saldo actual calculado.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool incluirInactivas = false)
    {
        var query = _db.CafeCajas.AsQueryable();
        if (!incluirInactivas) query = query.Where(c => c.IsActive);
        var cajas = await query.OrderBy(c => c.Orden).ThenBy(c => c.Nombre).ToListAsync();

        // Pre-calcular sumas de cobranzas por caja
        var sumasPorCaja = await _db.CafeCobranzasMedios
            .GroupBy(m => m.CajaId)
            .Select(g => new { CajaId = g.Key, Total = g.Sum(x => x.Importe) })
            .ToListAsync();
        var dictSumas = sumasPorCaja.ToDictionary(s => s.CajaId, s => s.Total);

        var result = cajas.Select(c => new CajaDto(
            c.Id, c.Nombre, c.Tipo, c.SaldoInicial, c.Orden, c.IsActive, c.Notas,
            c.SaldoInicial + (dictSumas.TryGetValue(c.Id, out var t) ? t : 0m)
        )).ToList();
        return Ok(result);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var c = await _db.CafeCajas.FindAsync(id);
        if (c is null) return NotFound();
        return Ok(c);
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearCajaRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre vacio" });
        if (await _db.CafeCajas.AnyAsync(c => c.Nombre == req.Nombre))
            return BadRequest(new { error = "Ya existe una caja con ese nombre" });
        var c = new Models.CafeCaja
        {
            Nombre = req.Nombre.Trim(),
            Tipo = (req.Tipo ?? "EFECTIVO").Trim().ToUpperInvariant(),
            SaldoInicial = req.SaldoInicial,
            Orden = req.Orden ?? 0,
            Notas = req.Notas
        };
        _db.CafeCajas.Add(c);
        await _db.SaveChangesAsync();
        return Ok(c);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Editar(int id, [FromBody] EditarCajaRequest req)
    {
        var c = await _db.CafeCajas.FindAsync(id);
        if (c is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre vacio" });
        // Verificar duplicado de nombre (excluyendo a si mismo)
        if (await _db.CafeCajas.AnyAsync(x => x.Nombre == req.Nombre && x.Id != id))
            return BadRequest(new { error = "Ya existe otra caja con ese nombre" });
        c.Nombre = req.Nombre.Trim();
        c.Tipo = (req.Tipo ?? c.Tipo).Trim().ToUpperInvariant();
        c.SaldoInicial = req.SaldoInicial;
        if (req.Orden.HasValue) c.Orden = req.Orden.Value;
        c.IsActive = req.IsActive;
        c.Notas = req.Notas;
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(c);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Eliminar(int id)
    {
        var c = await _db.CafeCajas.FindAsync(id);
        if (c is null) return NotFound();
        // No permitir eliminar si tiene movimientos
        var tieneMovs = await _db.CafeCobranzasMedios.AnyAsync(m => m.CajaId == id);
        if (tieneMovs) return BadRequest(new { error = "La caja tiene movimientos. Desactivala en vez de eliminar." });
        _db.CafeCajas.Remove(c);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
