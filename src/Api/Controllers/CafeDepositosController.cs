using Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Depositos del modulo Cafe. Permite tener stock distribuido en varios depositos.
/// En Fase 1: por defecto hay solo "Depósito Principal". El usuario puede agregar mas
/// pero el descuento/suma automatico en ventas/compras va al principal hasta que se sume
/// un selector explicito (Fase 2).
/// </summary>
[ApiController]
[Route("api/cafe/depositos")]
[Authorize]
public class CafeDepositosController : ControllerBase
{
    private readonly AppDbContext _db;

    public CafeDepositosController(AppDbContext db) { _db = db; }

    public record DepositoDto(int Id, string Nombre, string? Direccion, string? Notas,
        bool IsDefault, bool IsActive, int Orden,
        int CantidadProductos, decimal StockGramosTotal, int StockUnidadesTotal);

    public record CrearDepositoReq(string Nombre, string? Direccion, string? Notas, int? Orden);
    public record EditarDepositoReq(string Nombre, string? Direccion, string? Notas, int? Orden, bool IsActive);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool incluirInactivos = false)
    {
        var q = _db.CafeDepositos.AsQueryable();
        if (!incluirInactivos) q = q.Where(d => d.IsActive);
        var depositos = await q.OrderBy(d => d.Orden).ThenBy(d => d.Nombre).ToListAsync();

        // Stock total por deposito
        var stockPorDep = await _db.CafeStockPorDeposito
            .GroupBy(s => s.DepositoId)
            .Select(g => new
            {
                DepositoId = g.Key,
                Productos = g.Count(),
                Gramos = g.Sum(s => s.StockGramos),
                Unidades = g.Sum(s => s.StockUnidades)
            })
            .ToListAsync();
        var dict = stockPorDep.ToDictionary(s => s.DepositoId);

        var result = depositos.Select(d =>
        {
            dict.TryGetValue(d.Id, out var s);
            return new DepositoDto(
                d.Id, d.Nombre, d.Direccion, d.Notas,
                d.IsDefault, d.IsActive, d.Orden,
                s?.Productos ?? 0,
                s?.Gramos ?? 0m,
                s?.Unidades ?? 0);
        }).ToList();
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearDepositoReq req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre vacio" });
        if (await _db.CafeDepositos.AnyAsync(d => d.Nombre == req.Nombre))
            return BadRequest(new { error = "Ya existe un deposito con ese nombre" });
        var d = new Models.CafeDeposito
        {
            Nombre = req.Nombre.Trim(),
            Direccion = req.Direccion,
            Notas = req.Notas,
            Orden = req.Orden ?? 0
        };
        _db.CafeDepositos.Add(d);
        await _db.SaveChangesAsync();

        // Pre-crear stock=0 para todos los productos en este deposito (asi aparece en la grilla)
        var productoIds = await _db.CafeProductos.Select(p => p.Id).ToListAsync();
        foreach (var pid in productoIds)
        {
            _db.CafeStockPorDeposito.Add(new Models.CafeStockPorDeposito
            {
                ProductoId = pid,
                DepositoId = d.Id,
                StockGramos = 0,
                StockUnidades = 0
            });
        }
        await _db.SaveChangesAsync();

        return Ok(d);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Editar(int id, [FromBody] EditarDepositoReq req)
    {
        var d = await _db.CafeDepositos.FindAsync(id);
        if (d is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre vacio" });
        if (await _db.CafeDepositos.AnyAsync(x => x.Nombre == req.Nombre && x.Id != id))
            return BadRequest(new { error = "Ya existe otro deposito con ese nombre" });
        d.Nombre = req.Nombre.Trim();
        d.Direccion = req.Direccion;
        d.Notas = req.Notas;
        if (req.Orden.HasValue) d.Orden = req.Orden.Value;
        d.IsActive = req.IsActive;
        d.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(d);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Eliminar(int id)
    {
        var d = await _db.CafeDepositos.FindAsync(id);
        if (d is null) return NotFound();
        if (d.IsDefault) return BadRequest(new { error = "No se puede eliminar el deposito por defecto" });
        var tieneStock = await _db.CafeStockPorDeposito.AnyAsync(s => s.DepositoId == id && (s.StockGramos > 0 || s.StockUnidades > 0));
        if (tieneStock) return BadRequest(new { error = "El deposito tiene stock. Movelo a otro deposito antes de eliminar." });
        // Borrar las filas con stock=0
        var ceros = await _db.CafeStockPorDeposito.Where(s => s.DepositoId == id).ToListAsync();
        _db.CafeStockPorDeposito.RemoveRange(ceros);
        _db.CafeDepositos.Remove(d);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
