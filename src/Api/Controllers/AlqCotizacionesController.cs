using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Presupuestos de alquiler pasados por WhatsApp. Van pegados al TELÉFONO: el que consulta
/// todavía no es cliente. Si vuelve en tres meses, acá está lo que se le presupuestó. 2026-08-31.
/// </summary>
[ApiController]
[Route("api/alquileres/cotizaciones")]
[Authorize]
public class AlqCotizacionesController : ControllerBase
{
    private readonly AppDbContext _db;

    public AlqCotizacionesController(AppDbContext db) { _db = db; }

    /// <summary>Las cotizaciones de un teléfono, de la más nueva a la más vieja.</summary>
    [HttpGet]
    public async Task<IActionResult> GetPorTelefono([FromQuery] string telefono, [FromQuery] int limit = 20)
    {
        var tel = SoloDigitos(telefono);
        if (tel.Length == 0) return BadRequest(new { error = "Falta el teléfono" });

        var list = await _db.AlqCotizaciones
            .AsNoTracking()
            .Where(c => c.Telefono == tel)
            .OrderByDescending(c => c.CreatedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(c => new AlqCotizacionDto(
                c.Id, c.Telefono, c.ClienteId, c.FechaEvento,
                c.FleteZona, c.FleteMonto, c.Descuento, c.Total,
                c.Texto, c.Operador, c.ReservaId, c.CreatedAt,
                c.Items.Select(i => new AlqCotizacionItemDto(i.Id, i.EquipoId, i.Nombre, i.Cantidad, i.PrecioUnitario)).ToList()))
            .ToListAsync();

        return Ok(list);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var c = await _db.AlqCotizaciones.AsNoTracking()
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound(new { error = "Cotización no encontrada" });

        return Ok(new AlqCotizacionDto(
            c.Id, c.Telefono, c.ClienteId, c.FechaEvento,
            c.FleteZona, c.FleteMonto, c.Descuento, c.Total,
            c.Texto, c.Operador, c.ReservaId, c.CreatedAt,
            c.Items.Select(i => new AlqCotizacionItemDto(i.Id, i.EquipoId, i.Nombre, i.Cantidad, i.PrecioUnitario)).ToList()));
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearAlqCotizacionRequest req)
    {
        var tel = SoloDigitos(req.Telefono);
        if (tel.Length == 0) return BadRequest(new { error = "Falta el teléfono" });

        var items = (req.Items ?? new())
            .Where(i => i.Cantidad > 0 && !string.IsNullOrWhiteSpace(i.Nombre))
            .ToList();
        if (items.Count == 0 && req.FleteMonto == 0m)
            return BadRequest(new { error = "La cotización está vacía" });

        var total = items.Sum(i => i.PrecioUnitario * i.Cantidad) + req.FleteMonto - req.Descuento;

        var c = new AlqCotizacion
        {
            Telefono = tel,
            ClienteId = req.ClienteId,
            FechaEvento = req.FechaEvento,
            FleteZona = string.IsNullOrWhiteSpace(req.FleteZona) ? null : req.FleteZona.Trim(),
            FleteMonto = req.FleteMonto,
            Descuento = req.Descuento,
            Total = total,
            Texto = req.Texto,
            Operador = string.IsNullOrWhiteSpace(req.Operador) ? null : req.Operador.Trim(),
            CreatedAt = DateTime.UtcNow,
            Items = items.Select(i => new AlqCotizacionItem
            {
                EquipoId = i.EquipoId,
                Nombre = i.Nombre.Trim(),
                Cantidad = i.Cantidad,
                PrecioUnitario = i.PrecioUnitario
            }).ToList()
        };

        _db.AlqCotizaciones.Add(c);
        await _db.SaveChangesAsync();

        return Ok(new AlqCotizacionDto(
            c.Id, c.Telefono, c.ClienteId, c.FechaEvento,
            c.FleteZona, c.FleteMonto, c.Descuento, c.Total,
            c.Texto, c.Operador, c.ReservaId, c.CreatedAt,
            c.Items.Select(i => new AlqCotizacionItemDto(i.Id, i.EquipoId, i.Nombre, i.Cantidad, i.PrecioUnitario)).ToList()));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Borrar(int id)
    {
        var c = await _db.AlqCotizaciones.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound(new { error = "Cotización no encontrada" });
        _db.AlqCotizacionItems.RemoveRange(c.Items);
        _db.AlqCotizaciones.Remove(c);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    private static string SoloDigitos(string? s)
        => new((s ?? "").Where(char.IsDigit).ToArray());
}
