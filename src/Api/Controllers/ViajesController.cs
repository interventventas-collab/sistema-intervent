using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Modulo de Viajes: el empleado carga cada dia cuantos viajes hizo en CABA y en Provincia.
/// El admin (dueño / hermano del dueño) carga los pagos que le va haciendo (transferencias,
/// efectivo, etc). El sistema calcula el saldo: cuanto se le debe al empleado o cuanto debe el.
///
/// Modelo:
///   - 1 tarifa CABA + 1 tarifa PCIA por empleado (configurable, no cambia todos los dias).
///   - Total a cobrar = SUM(viajes_CABA × tarifaCABA + viajes_PCIA × tarifaPCIA).
///   - Total pagado  = SUM(pagos).
///   - Saldo = TotalACobrar - TotalPagado.
///       Saldo > 0 → la empresa debe al empleado.
///       Saldo < 0 → el empleado debe a la empresa (pagamos de mas).
///
/// El empleado VE su saldo (transparencia, asi nadie se sorprende).
/// Cierres / liquidaciones: por ahora calculado dinamicamente. Si despues hace falta congelar
/// saldos a una fecha de corte, se agrega Viajes_Cierres.
/// </summary>
[ApiController]
[Route("api/viajes")]
public class ViajesController : ControllerBase
{
    private readonly AppDbContext _db;
    public ViajesController(AppDbContext db) { _db = db; }

    // ============================================================
    // ENDPOINTS PUBLICOS (sin auth, por token)
    // ============================================================

    public record PublicRegistroDto(int Id, DateTime Fecha, int CantidadCABA, int CantidadPCIA, string? Anotaciones);
    public record PublicPagoDto(int Id, DateTime Fecha, string Descripcion, decimal Importe);
    public record PublicViajesDto(
        string Nombre,
        decimal TarifaCABA, decimal TarifaPCIA,
        DateTime HoyFecha, DateTime FechaSeleccionada,
        int CantidadCABASeleccionada, int CantidadPCIASeleccionada, string? AnotacionesSeleccionada,
        List<PublicRegistroDto> Ultimos7Dias,
        int TotalViajesMes, decimal TotalACobrarMes,
        decimal TotalPagadoMes,
        decimal SaldoMes,            // a favor del empleado si > 0
        decimal SaldoAcumulado,      // saldo historico desde siempre (todos los registros vs todos los pagos)
        List<PublicPagoDto> UltimosPagos);

    [HttpGet("publica/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPublica(string token, [FromQuery] string? fecha = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return NotFound();
        var emp = await _db.ViajesEmpleados.FirstOrDefaultAsync(e => e.Token == token && e.IsActive);
        if (emp is null) return NotFound(new { error = "Token inválido o empleado inactivo" });

        var hoy = FechaArgentinaHoy();
        var fechaSel = hoy;
        if (!string.IsNullOrWhiteSpace(fecha) && DateTime.TryParse(fecha, out var f))
        {
            fechaSel = f.Date;
            if (fechaSel > hoy) fechaSel = hoy;
        }
        var hace7 = hoy.AddDays(-6);
        var inicioMes = new DateTime(hoy.Year, hoy.Month, 1);
        var desde = new[] { inicioMes, hace7, fechaSel }.Min();

        var registros = await _db.ViajesRegistros
            .Where(r => r.EmpleadoId == emp.Id && r.Fecha >= desde)
            .OrderByDescending(r => r.Fecha)
            .ToListAsync();

        var pagos = await _db.ViajesPagos
            .Where(p => p.EmpleadoId == emp.Id)
            .OrderByDescending(p => p.Fecha)
            .ToListAsync();

        // Totales del MES en curso
        var regsMes = registros.Where(r => r.Fecha >= inicioMes).ToList();
        var pagosMes = pagos.Where(p => p.Fecha >= inicioMes).ToList();
        var totalViajesMes = regsMes.Sum(r => r.CantidadCABA + r.CantidadPCIA);
        // Cada viaje se valua con SU tarifa congelada (no la actual del empleado).
        var totalACobrarMes = regsMes.Sum(r => r.CantidadCABA * r.TarifaCABA + r.CantidadPCIA * r.TarifaPCIA);
        var totalPagadoMes = pagosMes.Sum(p => p.Importe);
        var saldoMes = totalACobrarMes - totalPagadoMes;

        // Saldo acumulado (historico, todas las fechas) — para esto pido TODO de la DB.
        var totalACobrarAll = await _db.ViajesRegistros
            .Where(r => r.EmpleadoId == emp.Id)
            .SumAsync(r => (decimal)r.CantidadCABA * r.TarifaCABA + (decimal)r.CantidadPCIA * r.TarifaPCIA);
        var totalPagadoAll = await _db.ViajesPagos.Where(p => p.EmpleadoId == emp.Id).SumAsync(p => p.Importe);
        var saldoAcum = totalACobrarAll - totalPagadoAll;

        var regSel = registros.FirstOrDefault(r => r.Fecha == fechaSel);
        var ultimos7 = registros.Where(r => r.Fecha >= hace7 && r.Fecha <= hoy)
            .OrderByDescending(r => r.Fecha)
            .Select(r => new PublicRegistroDto(r.Id, r.Fecha, r.CantidadCABA, r.CantidadPCIA, r.Anotaciones))
            .ToList();
        var ultimosPagos = pagos.Take(8)
            .Select(p => new PublicPagoDto(p.Id, p.Fecha, p.Descripcion, p.Importe))
            .ToList();

        return Ok(new PublicViajesDto(
            emp.Nombre, emp.TarifaCABA, emp.TarifaPCIA,
            hoy, fechaSel,
            regSel?.CantidadCABA ?? 0, regSel?.CantidadPCIA ?? 0, regSel?.Anotaciones,
            ultimos7,
            totalViajesMes, totalACobrarMes,
            totalPagadoMes, saldoMes,
            saldoAcum, ultimosPagos));
    }

    public class CargarViajesRequest
    {
        public int CantidadCABA { get; set; }
        public int CantidadPCIA { get; set; }
        public string? Anotaciones { get; set; }
        public DateTime? Fecha { get; set; }
    }

    [HttpPost("publica/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> CargarPublica(string token, [FromBody] CargarViajesRequest req)
    {
        if (string.IsNullOrWhiteSpace(token)) return NotFound();
        var emp = await _db.ViajesEmpleados.FirstOrDefaultAsync(e => e.Token == token && e.IsActive);
        if (emp is null) return NotFound(new { error = "Token inválido o empleado inactivo" });

        if (req.CantidadCABA < 0 || req.CantidadCABA > 200) return BadRequest(new { error = "Cantidad CABA inválida (0–200)" });
        if (req.CantidadPCIA < 0 || req.CantidadPCIA > 200) return BadRequest(new { error = "Cantidad PCIA inválida (0–200)" });

        var hoy = FechaArgentinaHoy();
        var fechaCarga = req.Fecha?.Date ?? hoy;
        if (fechaCarga > hoy) return BadRequest(new { error = "No podés cargar fechas futuras" });

        var existente = await _db.ViajesRegistros.FirstOrDefaultAsync(r => r.EmpleadoId == emp.Id && r.Fecha == fechaCarga);
        if (existente is null)
        {
            existente = new ViajesRegistro
            {
                EmpleadoId = emp.Id,
                Fecha = fechaCarga,
                CantidadCABA = req.CantidadCABA,
                CantidadPCIA = req.CantidadPCIA,
                // Congelamos la tarifa vigente HOY. Si mañana cambian la tarifa del empleado,
                // este viaje sigue valuado a la de hoy (no se recalcula la deuda vieja).
                TarifaCABA = emp.TarifaCABA,
                TarifaPCIA = emp.TarifaPCIA,
                Anotaciones = string.IsNullOrWhiteSpace(req.Anotaciones) ? null : req.Anotaciones.Trim(),
                CreatedAt = DateTime.UtcNow
            };
            _db.ViajesRegistros.Add(existente);
        }
        else
        {
            existente.CantidadCABA = req.CantidadCABA;
            existente.CantidadPCIA = req.CantidadPCIA;
            existente.Anotaciones = string.IsNullOrWhiteSpace(req.Anotaciones) ? null : req.Anotaciones.Trim();
            existente.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, fecha = fechaCarga });
    }

    // ============================================================
    // ENDPOINTS ADMIN (con auth)
    // ============================================================

    public record AdminEmpleadoDto(int Id, string Nombre, string Token, bool IsActive,
        decimal TarifaCABA, decimal TarifaPCIA,
        int TotalViajesMes, decimal TotalACobrarMes, decimal TotalPagadoMes,
        decimal SaldoMes, decimal SaldoAcumulado,
        DateTime? UltimaCargaAt, DateTime CreatedAt);

    [HttpGet("admin/empleados")]
    [Authorize]
    public async Task<IActionResult> ListEmpleados()
    {
        var emps = await _db.ViajesEmpleados.OrderBy(e => e.Nombre).ToListAsync();
        var hoy = FechaArgentinaHoy();
        var inicioMes = new DateTime(hoy.Year, hoy.Month, 1);

        var regsMes = await _db.ViajesRegistros.Where(r => r.Fecha >= inicioMes).ToListAsync();
        var pagosMes = await _db.ViajesPagos.Where(p => p.Fecha >= inicioMes).ToListAsync();
        var regsAll = await _db.ViajesRegistros.ToListAsync();
        var pagosAll = await _db.ViajesPagos.ToListAsync();
        var ultimasCargas = regsAll.GroupBy(r => r.EmpleadoId)
            .ToDictionary(g => g.Key, g => g.Max(r => (DateTime?)(r.UpdatedAt ?? r.CreatedAt)));

        var result = emps.Select(e =>
        {
            var totalACobrarMes = regsMes.Where(r => r.EmpleadoId == e.Id)
                .Sum(r => r.CantidadCABA * r.TarifaCABA + r.CantidadPCIA * r.TarifaPCIA);
            var totalPagadoMes = pagosMes.Where(p => p.EmpleadoId == e.Id).Sum(p => p.Importe);
            var totalACobrarAll = regsAll.Where(r => r.EmpleadoId == e.Id)
                .Sum(r => r.CantidadCABA * r.TarifaCABA + r.CantidadPCIA * r.TarifaPCIA);
            var totalPagadoAll = pagosAll.Where(p => p.EmpleadoId == e.Id).Sum(p => p.Importe);
            return new AdminEmpleadoDto(e.Id, e.Nombre, e.Token, e.IsActive,
                e.TarifaCABA, e.TarifaPCIA,
                regsMes.Where(r => r.EmpleadoId == e.Id).Sum(r => r.CantidadCABA + r.CantidadPCIA),
                totalACobrarMes, totalPagadoMes,
                totalACobrarMes - totalPagadoMes,
                totalACobrarAll - totalPagadoAll,
                ultimasCargas.TryGetValue(e.Id, out var u) ? u : null,
                e.CreatedAt);
        }).ToList();
        return Ok(result);
    }

    public class CreateEmpleadoRequest
    {
        public string Nombre { get; set; } = "";
        public decimal? TarifaCABA { get; set; }
        public decimal? TarifaPCIA { get; set; }
    }

    [HttpPost("admin/empleados")]
    [Authorize]
    public async Task<IActionResult> CreateEmpleado([FromBody] CreateEmpleadoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre obligatorio" });
        var emp = new ViajesEmpleado
        {
            Nombre = req.Nombre.Trim(),
            Token = Guid.NewGuid().ToString("N"),
            TarifaCABA = req.TarifaCABA ?? 6000m,
            TarifaPCIA = req.TarifaPCIA ?? 8000m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.ViajesEmpleados.Add(emp);
        await _db.SaveChangesAsync();
        return Ok(emp);
    }

    public class UpdateEmpleadoRequest
    {
        public string? Nombre { get; set; }
        public bool? IsActive { get; set; }
        public decimal? TarifaCABA { get; set; }
        public decimal? TarifaPCIA { get; set; }
        public bool RegenerarToken { get; set; }
    }

    [HttpPut("admin/empleados/{id:int}")]
    [Authorize]
    public async Task<IActionResult> UpdateEmpleado(int id, [FromBody] UpdateEmpleadoRequest req)
    {
        var emp = await _db.ViajesEmpleados.FindAsync(id);
        if (emp is null) return NotFound();
        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre no puede ser vacío" });
            emp.Nombre = req.Nombre.Trim();
        }
        if (req.IsActive.HasValue) emp.IsActive = req.IsActive.Value;
        if (req.TarifaCABA.HasValue && req.TarifaCABA.Value >= 0) emp.TarifaCABA = req.TarifaCABA.Value;
        if (req.TarifaPCIA.HasValue && req.TarifaPCIA.Value >= 0) emp.TarifaPCIA = req.TarifaPCIA.Value;
        if (req.RegenerarToken) emp.Token = Guid.NewGuid().ToString("N");
        emp.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(emp);
    }

    [HttpDelete("admin/empleados/{id:int}")]
    [Authorize]
    public async Task<IActionResult> DeleteEmpleado(int id)
    {
        var emp = await _db.ViajesEmpleados.FindAsync(id);
        if (emp is null) return NotFound();
        _db.ViajesEmpleados.Remove(emp);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    public record AdminRegistroDto(int Id, int EmpleadoId, string EmpleadoNombre, DateTime Fecha,
        int CantidadCABA, int CantidadPCIA, decimal SubtotalCABA, decimal SubtotalPCIA, decimal Total,
        string? Anotaciones, DateTime CreatedAt, DateTime? UpdatedAt);

    [HttpGet("admin/registros")]
    [Authorize]
    public async Task<IActionResult> ListRegistros([FromQuery] DateTime? desde = null,
        [FromQuery] DateTime? hasta = null, [FromQuery] int? empleadoId = null)
    {
        var hoy = FechaArgentinaHoy();
        var d = (desde ?? hoy.AddDays(-60)).Date;
        var h = (hasta ?? hoy).Date;

        var q = _db.ViajesRegistros.Include(r => r.Empleado).AsQueryable();
        q = q.Where(r => r.Fecha >= d && r.Fecha <= h);
        if (empleadoId.HasValue) q = q.Where(r => r.EmpleadoId == empleadoId.Value);
        var regs = await q.OrderByDescending(r => r.Fecha).ThenBy(r => r.Empleado!.Nombre).ToListAsync();

        var result = regs.Select(r =>
        {
            var subCABA = r.CantidadCABA * r.TarifaCABA;
            var subPCIA = r.CantidadPCIA * r.TarifaPCIA;
            return new AdminRegistroDto(r.Id, r.EmpleadoId, r.Empleado?.Nombre ?? "?",
                r.Fecha, r.CantidadCABA, r.CantidadPCIA, subCABA, subPCIA, subCABA + subPCIA,
                r.Anotaciones, r.CreatedAt, r.UpdatedAt);
        }).ToList();
        return Ok(result);
    }

    [HttpDelete("admin/registros/{id:int}")]
    [Authorize]
    public async Task<IActionResult> DeleteRegistro(int id)
    {
        var r = await _db.ViajesRegistros.FindAsync(id);
        if (r is null) return NotFound();
        _db.ViajesRegistros.Remove(r);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ============== Pagos ==============

    public record AdminPagoDto(int Id, int EmpleadoId, string EmpleadoNombre, DateTime Fecha,
        string Descripcion, decimal Importe, DateTime CreatedAt);

    [HttpGet("admin/pagos")]
    [Authorize]
    public async Task<IActionResult> ListPagos([FromQuery] int? empleadoId = null,
        [FromQuery] DateTime? desde = null, [FromQuery] DateTime? hasta = null)
    {
        var q = _db.ViajesPagos.Include(p => p.Empleado).AsQueryable();
        if (empleadoId.HasValue) q = q.Where(p => p.EmpleadoId == empleadoId.Value);
        if (desde.HasValue) q = q.Where(p => p.Fecha >= desde.Value.Date);
        if (hasta.HasValue) q = q.Where(p => p.Fecha <= hasta.Value.Date);
        var pagos = await q.OrderByDescending(p => p.Fecha).ThenBy(p => p.Empleado!.Nombre).ToListAsync();
        return Ok(pagos.Select(p => new AdminPagoDto(p.Id, p.EmpleadoId, p.Empleado?.Nombre ?? "?",
            p.Fecha, p.Descripcion, p.Importe, p.CreatedAt)).ToList());
    }

    public class CreatePagoRequest
    {
        public int EmpleadoId { get; set; }
        public DateTime Fecha { get; set; }
        public string Descripcion { get; set; } = "";
        public decimal Importe { get; set; }
    }

    [HttpPost("admin/pagos")]
    [Authorize]
    public async Task<IActionResult> CreatePago([FromBody] CreatePagoRequest req)
    {
        if (req.EmpleadoId <= 0) return BadRequest(new { error = "Empleado obligatorio" });
        if (string.IsNullOrWhiteSpace(req.Descripcion)) return BadRequest(new { error = "Descripción obligatoria" });
        if (req.Importe == 0) return BadRequest(new { error = "Importe no puede ser 0" });
        var emp = await _db.ViajesEmpleados.FindAsync(req.EmpleadoId);
        if (emp is null) return NotFound(new { error = "Empleado no existe" });
        var p = new ViajesPago
        {
            EmpleadoId = req.EmpleadoId,
            Fecha = req.Fecha.Date,
            Descripcion = req.Descripcion.Trim(),
            Importe = req.Importe,
            CreatedAt = DateTime.UtcNow
        };
        _db.ViajesPagos.Add(p);
        await _db.SaveChangesAsync();
        return Ok(p);
    }

    public class UpdatePagoRequest
    {
        public DateTime? Fecha { get; set; }
        public string? Descripcion { get; set; }
        public decimal? Importe { get; set; }
    }

    [HttpPut("admin/pagos/{id:int}")]
    [Authorize]
    public async Task<IActionResult> UpdatePago(int id, [FromBody] UpdatePagoRequest req)
    {
        var p = await _db.ViajesPagos.FindAsync(id);
        if (p is null) return NotFound();
        if (req.Fecha.HasValue) p.Fecha = req.Fecha.Value.Date;
        if (req.Descripcion is not null && !string.IsNullOrWhiteSpace(req.Descripcion))
            p.Descripcion = req.Descripcion.Trim();
        if (req.Importe.HasValue) p.Importe = req.Importe.Value;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(p);
    }

    [HttpDelete("admin/pagos/{id:int}")]
    [Authorize]
    public async Task<IActionResult> DeletePago(int id)
    {
        var p = await _db.ViajesPagos.FindAsync(id);
        if (p is null) return NotFound();
        _db.ViajesPagos.Remove(p);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static DateTime FechaArgentinaHoy() => DateTime.UtcNow.AddHours(-3).Date;
}
