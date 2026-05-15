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

    public record PublicRegistroDto(int Id, DateTime Fecha, decimal Cantidad, string? Observaciones,
        string? HoraEntrada, string? HoraSalida);
    public record PublicEmpleadoDto(string Nombre, DateTime HoyFecha, DateTime FechaSeleccionada,
        decimal? HorasSeleccionada, string? ObservacionesSeleccionada,
        string? HoraEntradaSeleccionada, string? HoraSalidaSeleccionada,
        List<PublicRegistroDto> Ultimos7Dias, decimal TotalSemana, decimal TotalMes);

    /// <summary>El empleado abre el link con su token y obtiene su nombre + carga del dia + historial.
    /// Opcional: ?fecha=YYYY-MM-DD para precargar los datos de una fecha pasada (carga atrasada).</summary>
    [HttpGet("publica/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPublica(string token, [FromQuery] string? fecha = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return NotFound();
        var emp = await _db.HorasExtrasEmpleados.FirstOrDefaultAsync(e => e.Token == token && e.IsActive);
        if (emp is null) return NotFound(new { error = "Token inválido o empleado inactivo" });

        var hoy = FechaArgentinaHoy();
        // Si vino fecha, la parseamos; si no, hoy. Cap a hoy (no permitir futuro).
        var fechaSel = hoy;
        if (!string.IsNullOrWhiteSpace(fecha) && DateTime.TryParse(fecha, out var f))
        {
            fechaSel = f.Date;
            if (fechaSel > hoy) fechaSel = hoy;
        }
        var hace7 = hoy.AddDays(-6);
        var inicioMes = new DateTime(hoy.Year, hoy.Month, 1);

        // Traemos los registros del empleado desde el inicio del mes o desde hace 7 días
        // o desde la fecha seleccionada — lo que sea más viejo.
        var desde = new[] { inicioMes, hace7, fechaSel }.Min();
        var registros = await _db.HorasExtrasRegistros
            .Where(r => r.EmpleadoId == emp.Id && r.Fecha >= desde)
            .OrderByDescending(r => r.Fecha)
            .ToListAsync();

        var registroSel = registros.FirstOrDefault(r => r.Fecha == fechaSel);
        var ultimos7 = registros.Where(r => r.Fecha >= hace7 && r.Fecha <= hoy).OrderByDescending(r => r.Fecha)
            .Select(r => new PublicRegistroDto(r.Id, r.Fecha, r.Cantidad, r.Observaciones,
                FormatHora(r.HoraEntrada), FormatHora(r.HoraSalida)))
            .ToList();
        var totalSemana = ultimos7.Sum(r => r.Cantidad);
        var totalMes = registros.Where(r => r.Fecha >= inicioMes).Sum(r => r.Cantidad);

        return Ok(new PublicEmpleadoDto(emp.Nombre, hoy, fechaSel,
            registroSel?.Cantidad, registroSel?.Observaciones,
            FormatHora(registroSel?.HoraEntrada), FormatHora(registroSel?.HoraSalida),
            ultimos7, totalSemana, totalMes));
    }

    /// <summary>Devuelve "HH:mm" o null. Lo usamos en el JSON para que el input type=time del browser
    /// lo entienda sin conversion extra.</summary>
    private static string? FormatHora(TimeSpan? t) =>
        t.HasValue ? $"{t.Value.Hours:D2}:{t.Value.Minutes:D2}" : null;

    public class CargarHorasRequest
    {
        public decimal Cantidad { get; set; }
        public string? Observaciones { get; set; }
        /// <summary>Opcional. Si null, se usa la fecha de hoy (Argentina). Si viene, debe ser hoy o ayer.</summary>
        public DateTime? Fecha { get; set; }
        /// <summary>Opcionales. Formato "HH:mm" (lo que manda el input type=time). Null = no se carga.</summary>
        public string? HoraEntrada { get; set; }
        public string? HoraSalida { get; set; }
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

        // El campo en DB es decimal(5,2) → hasta 999.99. Antes limitabamos a 24 pensando en
        // 1 dia, pero el usuario carga acumulados (ej. 90hs para cerrar periodo) → permitido.
        if (req.Cantidad < 0 || req.Cantidad > 999)
            return BadRequest(new { error = "Cantidad inválida (0–999)" });

        // Default = hoy. Si vino fecha, validamos que NO sea futura. El empleado puede
        // cargar cualquier fecha pasada (modo "carga atrasada"); si se equivoca, el admin
        // lo corrige desde el panel.
        var hoy = FechaArgentinaHoy();
        var fechaCarga = req.Fecha?.Date ?? hoy;
        if (fechaCarga > hoy)
            return BadRequest(new { error = "No podés cargar fechas futuras" });

        // Parsea "HH:mm" → TimeSpan?. Strings vacios o invalidos → null (campo opcional).
        var horaEnt = ParseHora(req.HoraEntrada);
        var horaSal = ParseHora(req.HoraSalida);

        var existente = await _db.HorasExtrasRegistros.FirstOrDefaultAsync(r => r.EmpleadoId == emp.Id && r.Fecha == fechaCarga);
        if (existente is null)
        {
            existente = new HorasExtrasRegistro
            {
                EmpleadoId = emp.Id,
                Fecha = fechaCarga,
                Cantidad = req.Cantidad,
                Observaciones = string.IsNullOrWhiteSpace(req.Observaciones) ? null : req.Observaciones.Trim(),
                HoraEntrada = horaEnt,
                HoraSalida = horaSal,
                CreatedAt = DateTime.UtcNow
            };
            _db.HorasExtrasRegistros.Add(existente);
        }
        else
        {
            existente.Cantidad = req.Cantidad;
            existente.Observaciones = string.IsNullOrWhiteSpace(req.Observaciones) ? null : req.Observaciones.Trim();
            existente.HoraEntrada = horaEnt;
            existente.HoraSalida = horaSal;
            existente.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, fecha = fechaCarga, cantidad = req.Cantidad });
    }

    /// <summary>Parsea "HH:mm" o "HH:mm:ss" a TimeSpan. Strings vacios/invalidos → null.</summary>
    private static TimeSpan? ParseHora(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return TimeSpan.TryParse(s.Trim(), out var t) ? t : null;
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
        decimal Cantidad, string? Observaciones, string? HoraEntrada, string? HoraSalida,
        DateTime CreatedAt, DateTime? UpdatedAt);

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
            r.Fecha, r.Cantidad, r.Observaciones, FormatHora(r.HoraEntrada), FormatHora(r.HoraSalida),
            r.CreatedAt, r.UpdatedAt)).ToList();
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
