using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

// 2026-11-02: almanaque de vacaciones de los empleados (Empleados -> Vacaciones).
// La cuenta de cuántos días le tocan a cada uno la hace la pantalla con la fecha de ingreso.
[ApiController]
[Route("api/nominas/vacaciones")]
[Authorize]
public class NomVacacionesController : ControllerBase
{
    private readonly AppDbContext _db;

    public NomVacacionesController(AppDbContext db) { _db = db; }

    public record VacacionDto(int Id, int EmpleadoId, DateTime Desde, DateTime Hasta, int Periodo, string Estado, string? Nota);
    public record EmpleadoVacDto(int Id, string Nombre, string? Puesto, DateTime FechaIngreso);
    public record FeriadoVacDto(DateTime Fecha, string Descripcion);
    public record VacacionesDatosDto(DateTime Hoy, List<EmpleadoVacDto> Empleados, List<VacacionDto> Vacaciones, List<FeriadoVacDto> Feriados);
    public record GuardarVacacionRequest(int EmpleadoId, DateTime Desde, DateTime Hasta, int Periodo, string? Estado, string? Nota);

    private static VacacionDto Map(NomVacacion v) => new(v.Id, v.EmpleadoId, v.Desde, v.Hasta, v.Periodo, v.Estado, v.Nota);

    // Trae todo lo del año pedido, el anterior y el siguiente: las vacaciones de un periodo
    // se toman entre octubre y abril, así que cruzan de año.
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int anio)
    {
        if (anio < 2000 || anio > 2100) anio = PanoramaService.AhoraAr().Year;
        var desde = new DateTime(anio - 1, 1, 1);
        var hasta = new DateTime(anio + 1, 12, 31);

        var empleados = await _db.NomEmpleados
            .Where(e => e.IsActive)
            .OrderBy(e => e.Nombre)
            .Select(e => new EmpleadoVacDto(e.Id, e.Nombre, e.Puesto, e.FechaIngreso))
            .ToListAsync();

        var vacaciones = await _db.NomVacaciones
            .Where(v => v.Hasta >= desde && v.Desde <= hasta)
            .OrderBy(v => v.Desde)
            .ToListAsync();

        var feriados = await _db.HorasExtrasFeriados
            .Where(f => f.Fecha >= desde && f.Fecha <= hasta)
            .OrderBy(f => f.Fecha)
            .Select(f => new FeriadoVacDto(f.Fecha, f.Descripcion))
            .ToListAsync();

        return Ok(new VacacionesDatosDto(DateTime.SpecifyKind(PanoramaService.AhoraAr().Date, DateTimeKind.Unspecified), empleados,
            vacaciones.Select(Map).ToList(), feriados));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] GuardarVacacionRequest req)
    {
        var error = await Validar(req, null);
        if (error is not null) return BadRequest(new { error });

        var v = new NomVacacion
        {
            EmpleadoId = req.EmpleadoId,
            Desde = req.Desde.Date,
            Hasta = req.Hasta.Date,
            Periodo = req.Periodo,
            Estado = NormalizarEstado(req.Estado),
            Nota = string.IsNullOrWhiteSpace(req.Nota) ? null : req.Nota.Trim(),
            CreatedBy = User?.Identity?.Name
        };
        _db.NomVacaciones.Add(v);
        await _db.SaveChangesAsync();
        return Ok(Map(v));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] GuardarVacacionRequest req)
    {
        var v = await _db.NomVacaciones.FindAsync(id);
        if (v is null) return NotFound(new { error = "No se encontraron esas vacaciones." });

        var error = await Validar(req, id);
        if (error is not null) return BadRequest(new { error });

        v.EmpleadoId = req.EmpleadoId;
        v.Desde = req.Desde.Date;
        v.Hasta = req.Hasta.Date;
        v.Periodo = req.Periodo;
        v.Estado = NormalizarEstado(req.Estado);
        v.Nota = string.IsNullOrWhiteSpace(req.Nota) ? null : req.Nota.Trim();
        v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(v));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var v = await _db.NomVacaciones.FindAsync(id);
        if (v is null) return NotFound();
        _db.NomVacaciones.Remove(v);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static string NormalizarEstado(string? e)
        => (e ?? "").Trim().ToLowerInvariant() == "pedida" ? "pedida" : "aprobada";

    private async Task<string?> Validar(GuardarVacacionRequest req, int? idActual)
    {
        if (!await _db.NomEmpleados.AnyAsync(e => e.Id == req.EmpleadoId))
            return "Elegí un empleado.";
        if (req.Hasta.Date < req.Desde.Date)
            return "La fecha \"hasta\" no puede ser antes que \"desde\".";
        if ((req.Hasta.Date - req.Desde.Date).TotalDays > 60)
            return "Son más de 60 días seguidos. Revisá las fechas.";
        if (req.Periodo < 2000 || req.Periodo > 2100)
            return "Falta el año al que corresponden.";

        var pisa = await _db.NomVacaciones
            .Where(v => v.EmpleadoId == req.EmpleadoId && v.Id != (idActual ?? 0)
                        && v.Desde <= req.Hasta.Date && v.Hasta >= req.Desde.Date)
            .Select(v => new { v.Desde, v.Hasta })
            .FirstOrDefaultAsync();
        if (pisa is not null)
            return $"Ya tiene vacaciones cargadas del {pisa.Desde:dd/MM} al {pisa.Hasta:dd/MM} que se pisan con estas fechas.";
        return null;
    }
}
