using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/nominas/empleados")]
[Authorize]
public class NomEmpleadosController : ControllerBase
{
    private readonly AppDbContext _db;

    public NomEmpleadosController(AppDbContext db) { _db = db; }

    private static NomEmpleadoDto Map(NomEmpleado e) => new(
        e.Id, e.Nombre, e.Documento, e.Puesto, e.FechaIngreso,
        e.SueldoBase, e.ValorHora, e.ComisionPorcentaje,
        e.ComisionPorKg,
        e.IsActive, e.CreatedAt, e.UpdatedAt);

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var list = await _db.NomEmpleados.OrderBy(e => e.Nombre).ToListAsync();
        return Ok(list.Select(Map).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var e = await _db.NomEmpleados.FindAsync(id);
        if (e is null) return NotFound(new { error = "Empleado no encontrado" });
        return Ok(Map(e));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateNomEmpleadoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { error = "El nombre es obligatorio" });
        if (req.SueldoBase < 0 || req.ValorHora < 0)
            return BadRequest(new { error = "Sueldo base y valor hora no pueden ser negativos" });

        var e = new NomEmpleado
        {
            Nombre = req.Nombre.Trim(),
            Documento = string.IsNullOrWhiteSpace(req.Documento) ? null : req.Documento.Trim(),
            Puesto = string.IsNullOrWhiteSpace(req.Puesto) ? null : req.Puesto.Trim(),
            FechaIngreso = (req.FechaIngreso ?? DateTime.Today).Date,
            SueldoBase = req.SueldoBase,
            ValorHora = req.ValorHora,
            ComisionPorcentaje = req.ComisionPorcentaje,
            ComisionPorKg = Math.Max(0m, req.ComisionPorKg),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.NomEmpleados.Add(e);
        await _db.SaveChangesAsync();
        return Ok(Map(e));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateNomEmpleadoRequest req)
    {
        var e = await _db.NomEmpleados.FindAsync(id);
        if (e is null) return NotFound(new { error = "Empleado no encontrado" });

        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "El nombre no puede ser vacio" });
            e.Nombre = req.Nombre.Trim();
        }
        if (req.Documento is not null) e.Documento = string.IsNullOrWhiteSpace(req.Documento) ? null : req.Documento.Trim();
        if (req.Puesto is not null) e.Puesto = string.IsNullOrWhiteSpace(req.Puesto) ? null : req.Puesto.Trim();
        if (req.FechaIngreso.HasValue) e.FechaIngreso = req.FechaIngreso.Value.Date;
        if (req.SueldoBase.HasValue)
        {
            if (req.SueldoBase.Value < 0) return BadRequest(new { error = "Sueldo base no puede ser negativo" });
            e.SueldoBase = req.SueldoBase.Value;
        }
        if (req.ValorHora.HasValue)
        {
            if (req.ValorHora.Value < 0) return BadRequest(new { error = "Valor hora no puede ser negativo" });
            e.ValorHora = req.ValorHora.Value;
        }
        if (req.ComisionPorcentaje.HasValue) e.ComisionPorcentaje = req.ComisionPorcentaje.Value;
        if (req.ComisionPorKg.HasValue) e.ComisionPorKg = Math.Max(0m, req.ComisionPorKg.Value);
        if (req.IsActive.HasValue) e.IsActive = req.IsActive.Value;
        e.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(Map(e));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var e = await _db.NomEmpleados.FindAsync(id);
        if (e is null) return NotFound(new { error = "Empleado no encontrado" });
        var tieneLiq = await _db.NomLiquidaciones.AnyAsync(l => l.EmpleadoId == id);
        if (tieneLiq)
            return BadRequest(new { error = "No se puede eliminar: el empleado tiene liquidaciones cargadas. Marcalo como inactivo en su lugar." });
        _db.NomEmpleados.Remove(e);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }
}
