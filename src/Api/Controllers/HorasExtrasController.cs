using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Modulo de Horas Extras: cada empleado tiene un link publico con su token. Entra al link
/// y carga cuantas horas extras hizo cada dia. El admin ve todo en tiempo real desde el panel.
///
/// Estructura:
///   /api/horas-extras/admin/...   → requiere auth (admin)
///   /api/horas-extras/publica/... → AllowAnonymous, acceso por token
/// </summary>
[ApiController]
[Route("api/horas-extras")]
public class HorasExtrasController : ControllerBase
{
    private readonly AppDbContext _db;
    public HorasExtrasController(AppDbContext db) { _db = db; }

    // ============================================================
    // ENDPOINTS PUBLICOS (sin auth, por token)
    // ============================================================

    public record PublicRegistroDto(int Id, DateTime Fecha, decimal Cantidad, string? Observaciones);
    public record PublicEmpleadoDto(string Nombre, DateTime HoyFecha, decimal? HorasHoy, string? ObservacionesHoy,
        List<PublicRegistroDto> Ultimos7Dias, decimal TotalSemana, decimal TotalMes);

    /// <summary>El empleado abre el link con su token y obtiene su nombre + carga del dia + historial.</summary>
    [HttpGet("publica/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPublica(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return NotFound();
        var emp = await _db.HorasExtrasEmpleados.FirstOrDefaultAsync(e => e.Token == token && e.IsActive);
        if (emp is null) return NotFound(new { error = "Token inválido o empleado inactivo" });

        var hoy = FechaArgentinaHoy();
        var hace7 = hoy.AddDays(-6);
        var inicioMes = new DateTime(hoy.Year, hoy.Month, 1);

        var registros = await _db.HorasExtrasRegistros
            .Where(r => r.EmpleadoId == emp.Id && r.Fecha >= inicioMes)
            .OrderByDescending(r => r.Fecha)
            .ToListAsync();

        var registroHoy = registros.FirstOrDefault(r => r.Fecha == hoy);
        var ultimos7 = registros.Where(r => r.Fecha >= hace7).OrderByDescending(r => r.Fecha)
            .Select(r => new PublicRegistroDto(r.Id, r.Fecha, r.Cantidad, r.Observaciones))
            .ToList();
        var totalSemana = ultimos7.Sum(r => r.Cantidad);
        var totalMes = registros.Sum(r => r.Cantidad);

        return Ok(new PublicEmpleadoDto(emp.Nombre, hoy,
            registroHoy?.Cantidad, registroHoy?.Observaciones,
            ultimos7, totalSemana, totalMes));
    }

    public class CargarHorasRequest
    {
        public decimal Cantidad { get; set; }
        public string? Observaciones { get; set; }
        /// <summary>Opcional. Si null, se usa la fecha de hoy (Argentina). Si viene, debe ser hoy o ayer.</summary>
        public DateTime? Fecha { get; set; }
    }

    /// <summary>El empleado carga (o actualiza) sus horas extras del día. Si ya existe un registro
    /// para ese día, se actualiza. Si no, se crea. UPSERT.</summary>
    [HttpPost("publica/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> CargarPublica(string token, [FromBody] CargarHorasRequest req)
    {
        if (string.IsNullOrWhiteSpace(token)) return NotFound();
        var emp = await _db.HorasExtrasEmpleados.FirstOrDefaultAsync(e => e.Token == token && e.IsActive);
        if (emp is null) return NotFound(new { error = "Token inválido o empleado inactivo" });

        if (req.Cantidad < 0 || req.Cantidad > 24)
            return BadRequest(new { error = "Cantidad inválida (0–24)" });

        // Default = hoy. Si vino fecha, validamos que sea hoy o ayer (para evitar cargas viejas falsas).
        var hoy = FechaArgentinaHoy();
        var fechaCarga = req.Fecha?.Date ?? hoy;
        if (fechaCarga > hoy || fechaCarga < hoy.AddDays(-1))
            return BadRequest(new { error = "Solo podés cargar horas de hoy o ayer" });

        var existente = await _db.HorasExtrasRegistros.FirstOrDefaultAsync(r => r.EmpleadoId == emp.Id && r.Fecha == fechaCarga);
        if (existente is null)
        {
            existente = new HorasExtrasRegistro
            {
                EmpleadoId = emp.Id,
                Fecha = fechaCarga,
                Cantidad = req.Cantidad,
                Observaciones = string.IsNullOrWhiteSpace(req.Observaciones) ? null : req.Observaciones.Trim(),
                CreatedAt = DateTime.UtcNow
            };
            _db.HorasExtrasRegistros.Add(existente);
        }
        else
        {
            existente.Cantidad = req.Cantidad;
            existente.Observaciones = string.IsNullOrWhiteSpace(req.Observaciones) ? null : req.Observaciones.Trim();
            existente.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, fecha = fechaCarga, cantidad = req.Cantidad });
    }

    // ============================================================
    // ENDPOINTS ADMIN (con auth)
    // ============================================================

    public record AdminEmpleadoDto(int Id, string Nombre, string Token, bool IsActive,
        decimal TotalHoy, decimal TotalSemana, decimal TotalMes, DateTime? UltimaCargaAt, DateTime CreatedAt);

    /// <summary>Lista de empleados con totales (hoy / semana / mes) y la última vez que cargaron.</summary>
    [HttpGet("admin/empleados")]
    [Authorize]
    public async Task<IActionResult> ListEmpleados()
    {
        var emps = await _db.HorasExtrasEmpleados.OrderBy(e => e.Nombre).ToListAsync();
        var hoy = FechaArgentinaHoy();
        var inicioSemana = hoy.AddDays(-6);
        var inicioMes = new DateTime(hoy.Year, hoy.Month, 1);

        var regs = await _db.HorasExtrasRegistros
            .Where(r => r.Fecha >= inicioMes)
            .ToListAsync();

        var ultimasCargas = await _db.HorasExtrasRegistros
            .GroupBy(r => r.EmpleadoId)
            .Select(g => new { EmpId = g.Key, Ult = g.Max(r => (DateTime?)(r.UpdatedAt ?? r.CreatedAt)) })
            .ToListAsync();
        var ultDic = ultimasCargas.ToDictionary(x => x.EmpId, x => x.Ult);

        var result = emps.Select(e => new AdminEmpleadoDto(
            e.Id, e.Nombre, e.Token, e.IsActive,
            regs.Where(r => r.EmpleadoId == e.Id && r.Fecha == hoy).Sum(r => r.Cantidad),
            regs.Where(r => r.EmpleadoId == e.Id && r.Fecha >= inicioSemana).Sum(r => r.Cantidad),
            regs.Where(r => r.EmpleadoId == e.Id).Sum(r => r.Cantidad),
            ultDic.TryGetValue(e.Id, out var u) ? u : null,
            e.CreatedAt
        )).ToList();
        return Ok(result);
    }

    public class CreateEmpleadoRequest
    {
        public string Nombre { get; set; } = "";
    }

    [HttpPost("admin/empleados")]
    [Authorize]
    public async Task<IActionResult> CreateEmpleado([FromBody] CreateEmpleadoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre obligatorio" });
        var emp = new HorasExtrasEmpleado
        {
            Nombre = req.Nombre.Trim(),
            Token = Guid.NewGuid().ToString("N"),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.HorasExtrasEmpleados.Add(emp);
        await _db.SaveChangesAsync();
        return Ok(emp);
    }

    public class UpdateEmpleadoRequest
    {
        public string? Nombre { get; set; }
        public bool? IsActive { get; set; }
        /// <summary>Si true, regenera el token (invalida el link anterior).</summary>
        public bool RegenerarToken { get; set; }
    }

    [HttpPut("admin/empleados/{id:int}")]
    [Authorize]
    public async Task<IActionResult> UpdateEmpleado(int id, [FromBody] UpdateEmpleadoRequest req)
    {
        var emp = await _db.HorasExtrasEmpleados.FindAsync(id);
        if (emp is null) return NotFound();
        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre no puede ser vacío" });
            emp.Nombre = req.Nombre.Trim();
        }
        if (req.IsActive.HasValue) emp.IsActive = req.IsActive.Value;
        if (req.RegenerarToken) emp.Token = Guid.NewGuid().ToString("N");
        emp.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(emp);
    }

    [HttpDelete("admin/empleados/{id:int}")]
    [Authorize]
    public async Task<IActionResult> DeleteEmpleado(int id)
    {
        var emp = await _db.HorasExtrasEmpleados.FindAsync(id);
        if (emp is null) return NotFound();
        _db.HorasExtrasEmpleados.Remove(emp);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    public record AdminRegistroDto(int Id, int EmpleadoId, string EmpleadoNombre, DateTime Fecha,
        decimal Cantidad, string? Observaciones, DateTime CreatedAt, DateTime? UpdatedAt);

    /// <summary>Lista todos los registros, opcionalmente filtrando por rango y/o empleado.
    /// Por default devuelve los últimos 30 días.</summary>
    [HttpGet("admin/registros")]
    [Authorize]
    public async Task<IActionResult> ListRegistros([FromQuery] DateTime? desde = null,
        [FromQuery] DateTime? hasta = null, [FromQuery] int? empleadoId = null)
    {
        var hoy = FechaArgentinaHoy();
        var d = (desde ?? hoy.AddDays(-30)).Date;
        var h = (hasta ?? hoy).Date;

        var q = _db.HorasExtrasRegistros.Include(r => r.Empleado).AsQueryable();
        q = q.Where(r => r.Fecha >= d && r.Fecha <= h);
        if (empleadoId.HasValue) q = q.Where(r => r.EmpleadoId == empleadoId.Value);
        var regs = await q.OrderByDescending(r => r.Fecha).ThenBy(r => r.Empleado!.Nombre).ToListAsync();
        var result = regs.Select(r => new AdminRegistroDto(r.Id, r.EmpleadoId, r.Empleado?.Nombre ?? "?",
            r.Fecha, r.Cantidad, r.Observaciones, r.CreatedAt, r.UpdatedAt)).ToList();
        return Ok(result);
    }

    [HttpDelete("admin/registros/{id:int}")]
    [Authorize]
    public async Task<IActionResult> DeleteRegistro(int id)
    {
        var r = await _db.HorasExtrasRegistros.FindAsync(id);
        if (r is null) return NotFound();
        _db.HorasExtrasRegistros.Remove(r);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ============================================================
    // Helpers
    // ============================================================

    /// <summary>Fecha "hoy" en horario Argentina (UTC-3).</summary>
    private static DateTime FechaArgentinaHoy()
    {
        return DateTime.UtcNow.AddHours(-3).Date;
    }
}
