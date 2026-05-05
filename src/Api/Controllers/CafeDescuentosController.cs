using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/descuentos")]
[Authorize]
public class CafeDescuentosController : ControllerBase
{
    private readonly AppDbContext _db;
    // Tipos validos. Vienen de los Tipo distintos cargados en Cafe_Clientes (BAR / OTRO).
    // Si en el futuro hay mas tipos se carga aca.
    private static readonly string[] TiposClienteValidos = { "BAR", "OTRO" };

    public CafeDescuentosController(AppDbContext db) { _db = db; }

    [HttpGet("grilla")]
    public async Task<IActionResult> Grilla()
    {
        // Marcas activas + 1 fila "general" al principio (MarcaId null).
        var marcas = await _db.CafeMarcas
            .Where(m => m.IsActive)
            .OrderBy(m => m.Nombre)
            .ToListAsync();

        var descuentos = await _db.CafeDescuentosCliente.ToListAsync();

        // Index: (TipoCliente, MarcaId | null) -> DescuentoPct
        var byKey = descuentos.ToDictionary(
            d => (d.TipoCliente, d.MarcaId),
            d => d.DescuentoPct);

        var filas = new List<CafeDescuentoGrillaFila>();

        // Fila general (MarcaId = null).
        filas.Add(new CafeDescuentoGrillaFila(
            null,
            "(General — todas las marcas)",
            false,
            TiposClienteValidos.ToDictionary(
                t => t,
                t => byKey.TryGetValue((t, (int?)null), out var v) ? (decimal?)v : null)));

        // Una fila por marca.
        foreach (var m in marcas)
        {
            filas.Add(new CafeDescuentoGrillaFila(
                m.Id,
                m.Nombre,
                m.BloqueaDescuento,
                TiposClienteValidos.ToDictionary(
                    t => t,
                    t => byKey.TryGetValue((t, (int?)m.Id), out var v) ? (decimal?)v : null)));
        }

        return Ok(new CafeDescuentoGrillaResponse(TiposClienteValidos.ToList(), filas));
    }

    // Upsert: crea o actualiza el descuento (TipoCliente, MarcaId).
    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] UpsertDescuentoRequest req)
    {
        var tipo = (req.TipoCliente ?? "").Trim().ToUpperInvariant();
        if (!TiposClienteValidos.Contains(tipo))
            return BadRequest(new { error = $"Tipo de cliente invalido. Validos: {string.Join(", ", TiposClienteValidos)}" });

        if (req.DescuentoPct < 0 || req.DescuentoPct > 100)
            return BadRequest(new { error = "El descuento debe estar entre 0 y 100." });

        // Si la marca tiene BloqueaDescuento, no se pueden cargar descuentos.
        if (req.MarcaId.HasValue)
        {
            var marca = await _db.CafeMarcas.FindAsync(req.MarcaId.Value);
            if (marca is null) return NotFound(new { error = "Marca no encontrada" });
            if (marca.BloqueaDescuento && req.DescuentoPct > 0)
                return BadRequest(new { error = $"La marca '{marca.Nombre}' tiene los descuentos bloqueados." });
        }

        var existing = await _db.CafeDescuentosCliente
            .FirstOrDefaultAsync(d => d.TipoCliente == tipo && d.MarcaId == req.MarcaId);

        if (existing is null)
        {
            existing = new CafeDescuentoCliente
            {
                TipoCliente = tipo,
                MarcaId = req.MarcaId,
                DescuentoPct = req.DescuentoPct,
                CreatedAt = DateTime.UtcNow
            };
            _db.CafeDescuentosCliente.Add(existing);
        }
        else
        {
            existing.DescuentoPct = req.DescuentoPct;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return Ok(new
        {
            existing.Id,
            existing.TipoCliente,
            existing.MarcaId,
            existing.DescuentoPct
        });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var d = await _db.CafeDescuentosCliente.FindAsync(id);
        if (d is null) return NotFound();
        _db.CafeDescuentosCliente.Remove(d);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }
}
