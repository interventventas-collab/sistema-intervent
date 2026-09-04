using Api.Data;
using Api.Models;
using Api.Services;
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
    private readonly ViajesAutoService _auto;
    public ViajesController(AppDbContext db, ViajesAutoService auto) { _db = db; _auto = auto; }

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
        List<PublicPagoDto> UltimosPagos,
        // 2026-09-04: el que cobra por entrega no carga nada — solo mira como le va sumando.
        bool ModoAutomatico, decimal TarifaViaje,
        int ViajesHoy, decimal ImporteHoy,
        int ViajesPendientes, decimal ImportePendiente, DateTime? PendienteDesde);

    [HttpGet("publica/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPublica(string token, [FromQuery] string? fecha = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return NotFound();
        var emp = await _db.ViajesEmpleados.FirstOrDefaultAsync(e => e.Token == token && e.IsActive);
        if (emp is null) return NotFound(new { error = "Token inválido o empleado inactivo" });

        // Si cobra por entrega, primero ponemos al dia lo que le sumo el mapa.
        await _auto.SincronizarAsync(emp);

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

        // Viajes contados solos (modo automatico). En modo manual esta lista viene vacia.
        var entregas = await _db.ViajesEntregas.Where(x => x.EmpleadoId == emp.Id).ToListAsync();

        // Totales del MES en curso
        var regsMes = registros.Where(r => r.Fecha >= inicioMes).ToList();
        var entMes = entregas.Where(x => x.Fecha >= inicioMes).ToList();
        var pagosMes = pagos.Where(p => p.Fecha >= inicioMes).ToList();
        var totalViajesMes = regsMes.Sum(r => r.CantidadCABA + r.CantidadPCIA) + entMes.Count;
        // Cada viaje se valua con SU tarifa congelada (no la actual del empleado).
        var totalACobrarMes = regsMes.Sum(r => r.CantidadCABA * r.TarifaCABA + r.CantidadPCIA * r.TarifaPCIA)
                            + entMes.Sum(x => x.Tarifa);
        var totalPagadoMes = pagosMes.Sum(p => p.Importe);
        var saldoMes = totalACobrarMes - totalPagadoMes;

        // Saldo acumulado (historico, todas las fechas) — para esto pido TODO de la DB.
        var totalACobrarAll = await _db.ViajesRegistros
            .Where(r => r.EmpleadoId == emp.Id)
            .SumAsync(r => (decimal)r.CantidadCABA * r.TarifaCABA + (decimal)r.CantidadPCIA * r.TarifaPCIA)
            + entregas.Sum(x => x.Tarifa);
        var totalPagadoAll = await _db.ViajesPagos.Where(p => p.EmpleadoId == emp.Id).SumAsync(p => p.Importe);
        var saldoAcum = totalACobrarAll - totalPagadoAll;

        var entHoy = entregas.Where(x => x.Fecha == hoy).ToList();
        var entPend = entregas.Where(x => x.LiquidadoPagoId is null).ToList();

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
            saldoAcum, ultimosPagos,
            emp.ModoAutomatico, emp.TarifaViaje,
            entHoy.Count, entHoy.Sum(x => x.Tarifa),
            entPend.Count, entPend.Sum(x => x.Tarifa),
            entPend.Count == 0 ? null : entPend.Min(x => x.Fecha)));
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

        // El que cobra por entrega no carga nada a mano: sus viajes los cuenta el mapa.
        if (emp.ModoAutomatico) return BadRequest(new { error = "Tus viajes se cuentan solos con las entregas. No hace falta que cargues nada." });

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
        DateTime? UltimaCargaAt, DateTime CreatedAt,
        // 2026-09-04: los que cobran por entrega (Nacho). En modo manual estos campos van en cero.
        bool ModoAutomatico, int? MapeoDriverId, string? MapeoDriverNombre, decimal TarifaViaje,
        int ViajesPendientes, decimal ImportePendiente, DateTime? PendienteDesde,
        int ViajesHoy, decimal ImporteHoy);

    [HttpGet("admin/empleados")]
    [Authorize]
    public async Task<IActionResult> ListEmpleados()
    {
        // Antes de mostrar nada, ponemos al dia a los que cobran por entrega: sus viajes salen de
        // las paradas entregadas del mapa, no de lo que ellos carguen.
        await _auto.SincronizarTodosAsync();

        var emps = await _db.ViajesEmpleados.OrderBy(e => e.Nombre).ToListAsync();
        var hoy = FechaArgentinaHoy();
        var inicioMes = new DateTime(hoy.Year, hoy.Month, 1);

        var regsAll = await _db.ViajesRegistros.ToListAsync();
        var pagosAll = await _db.ViajesPagos.ToListAsync();
        var entAll = await _db.ViajesEntregas.ToListAsync();
        var drivers = await _db.MapeoDrivers.ToDictionaryAsync(d => d.Id, d => d.Nombre);

        var ultimasCargas = regsAll.GroupBy(r => r.EmpleadoId)
            .ToDictionary(g => g.Key, g => g.Max(r => (DateTime?)(r.UpdatedAt ?? r.CreatedAt)));
        var ultimasEntregas = entAll.GroupBy(e => e.EmpleadoId)
            .ToDictionary(g => g.Key, g => g.Max(e => (DateTime?)(e.UpdatedAt ?? e.CreatedAt)));

        var result = emps.Select(e =>
        {
            var regs = regsAll.Where(r => r.EmpleadoId == e.Id).ToList();
            var ents = entAll.Where(x => x.EmpleadoId == e.Id).ToList();
            var pagos = pagosAll.Where(p => p.EmpleadoId == e.Id).ToList();

            // UNA sola formula para los dos modos: el que carga a mano suma registros, el que cobra
            // por entrega suma entregas. El que no usa un modo lo tiene vacio y suma cero.
            decimal ACobrar(DateTime? desde) =>
                  regs.Where(r => desde == null || r.Fecha >= desde).Sum(r => r.CantidadCABA * r.TarifaCABA + r.CantidadPCIA * r.TarifaPCIA)
                + ents.Where(x => desde == null || x.Fecha >= desde).Sum(x => x.Tarifa);
            int Viajes(DateTime? desde) =>
                  regs.Where(r => desde == null || r.Fecha >= desde).Sum(r => r.CantidadCABA + r.CantidadPCIA)
                + ents.Count(x => desde == null || x.Fecha >= desde);

            var totalACobrarMes = ACobrar(inicioMes);
            var totalPagadoMes = pagos.Where(p => p.Fecha >= inicioMes).Sum(p => p.Importe);
            var saldoAcum = ACobrar(null) - pagos.Sum(p => p.Importe);

            // Pendiente = lo que todavia no se le liquido (solo aplica al modo automatico).
            var pend = ents.Where(x => x.LiquidadoPagoId is null).ToList();
            var hoyEnts = ents.Where(x => x.Fecha == hoy).ToList();

            var ultima = ultimasCargas.TryGetValue(e.Id, out var u1) ? u1 : null;
            var ultimaEnt = ultimasEntregas.TryGetValue(e.Id, out var u2) ? u2 : null;
            if (ultimaEnt.HasValue && (!ultima.HasValue || ultimaEnt > ultima)) ultima = ultimaEnt;

            return new AdminEmpleadoDto(e.Id, e.Nombre, e.Token, e.IsActive,
                e.TarifaCABA, e.TarifaPCIA,
                Viajes(inicioMes), totalACobrarMes, totalPagadoMes,
                totalACobrarMes - totalPagadoMes,
                saldoAcum,
                ultima, e.CreatedAt,
                e.ModoAutomatico, e.MapeoDriverId,
                e.MapeoDriverId.HasValue && drivers.TryGetValue(e.MapeoDriverId.Value, out var dn) ? dn : null,
                e.TarifaViaje,
                pend.Count, pend.Sum(x => x.Tarifa),
                pend.Count == 0 ? null : pend.Min(x => x.Fecha),
                hoyEnts.Count, hoyEnts.Sum(x => x.Tarifa));
        }).ToList();
        return Ok(result);
    }

    /// <summary>Choferes del mapa, para elegir de cual se cuentan las entregas.</summary>
    [HttpGet("admin/choferes")]
    [Authorize]
    public async Task<IActionResult> ListChoferes()
    {
        var ds = await _db.MapeoDrivers.Where(d => d.IsActive)
            .OrderBy(d => d.Nombre)
            .Select(d => new { d.Id, d.Nombre })
            .ToListAsync();
        return Ok(ds);
    }

    public class CreateEmpleadoRequest
    {
        public string Nombre { get; set; } = "";
        public decimal? TarifaCABA { get; set; }
        public decimal? TarifaPCIA { get; set; }
        public bool ModoAutomatico { get; set; }
        public int? MapeoDriverId { get; set; }
        public decimal? TarifaViaje { get; set; }
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
            ModoAutomatico = req.ModoAutomatico,
            MapeoDriverId = req.ModoAutomatico ? req.MapeoDriverId : null,
            TarifaViaje = req.TarifaViaje ?? 8500m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.ViajesEmpleados.Add(emp);
        await _db.SaveChangesAsync();
        // Si nace en modo automatico, ya le traemos los viajes de los ultimos dias.
        await _auto.SincronizarAsync(emp);
        return Ok(emp);
    }

    public class UpdateEmpleadoRequest
    {
        public string? Nombre { get; set; }
        public bool? IsActive { get; set; }
        public decimal? TarifaCABA { get; set; }
        public decimal? TarifaPCIA { get; set; }
        public bool RegenerarToken { get; set; }
        public bool? ModoAutomatico { get; set; }
        public int? MapeoDriverId { get; set; }
        public decimal? TarifaViaje { get; set; }
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
        if (req.ModoAutomatico.HasValue) emp.ModoAutomatico = req.ModoAutomatico.Value;
        if (req.MapeoDriverId.HasValue) emp.MapeoDriverId = req.MapeoDriverId.Value > 0 ? req.MapeoDriverId.Value : null;
        // La tarifa nueva vale de aca en mas: los viajes ya contados tienen la suya congelada.
        if (req.TarifaViaje.HasValue && req.TarifaViaje.Value >= 0) emp.TarifaViaje = req.TarifaViaje.Value;
        if (!emp.ModoAutomatico) emp.MapeoDriverId = null;
        emp.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _auto.SincronizarAsync(emp);
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
        // Si este pago era una liquidacion, los viajes que cerro vuelven a quedar impagos
        // (si no, desaparecerian de "pendiente" y nadie los volveria a cobrar).
        var liquidados = await _db.ViajesEntregas.Where(e => e.LiquidadoPagoId == id).ToListAsync();
        foreach (var e in liquidados) { e.LiquidadoPagoId = null; e.UpdatedAt = DateTime.UtcNow; }
        _db.ViajesPagos.Remove(p);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ============================================================
    // VIAJES QUE SE CUENTAN SOLOS (modo automatico) — 2026-09-04
    // El repartidor no carga nada: cada parada entregada del mapa le suma un viaje.
    // ============================================================

    public record AdminEntregaDto(int Id, DateTime Fecha, string Origen, string? Direccion,
        string? Cliente, DateTime? EntregadoAt, decimal Tarifa, bool Manual, string? Detalle,
        bool Liquidado);

    public record AdminDiaDto(DateTime Fecha, int Cantidad, decimal Importe, bool Liquidado,
        List<AdminEntregaDto> Entregas);

    public record AdminEntregasResumenDto(int EmpleadoId, string Nombre, decimal TarifaViaje,
        int ViajesPendientes, decimal ImportePendiente, DateTime? PendienteDesde,
        List<AdminDiaDto> Dias);

    /// <summary>Dia por dia de los viajes contados (y los ajustes a mano) de un empleado.</summary>
    [HttpGet("admin/empleados/{id:int}/entregas")]
    [Authorize]
    public async Task<IActionResult> ListEntregas(int id, [FromQuery] int dias = 30,
        [FromQuery] bool soloPendientes = false)
    {
        var emp = await _db.ViajesEmpleados.FindAsync(id);
        if (emp is null) return NotFound();
        await _auto.SincronizarAsync(emp);

        var hoy = FechaArgentinaHoy();
        var desde = hoy.AddDays(-Math.Clamp(dias, 1, 365));

        var q = _db.ViajesEntregas.Where(e => e.EmpleadoId == id && e.Fecha >= desde);
        if (soloPendientes) q = q.Where(e => e.LiquidadoPagoId == null);
        var ents = await q.ToListAsync();

        var pend = await _db.ViajesEntregas
            .Where(e => e.EmpleadoId == id && e.LiquidadoPagoId == null).ToListAsync();

        var dias_ = ents.GroupBy(e => e.Fecha)
            .OrderByDescending(g => g.Key)
            .Select(g => new AdminDiaDto(
                g.Key,
                g.Count(),
                g.Sum(x => x.Tarifa),
                g.All(x => x.LiquidadoPagoId != null),
                g.OrderBy(x => x.EntregadoAt ?? x.CreatedAt)
                 .Select(x => new AdminEntregaDto(x.Id, x.Fecha, x.Origen, x.Direccion, x.Cliente,
                     x.EntregadoAt, x.Tarifa, x.StopId == null, x.Detalle, x.LiquidadoPagoId != null))
                 .ToList()))
            .ToList();

        return Ok(new AdminEntregasResumenDto(emp.Id, emp.Nombre, emp.TarifaViaje,
            pend.Count, pend.Sum(x => x.Tarifa),
            pend.Count == 0 ? null : pend.Min(x => x.Fecha),
            dias_));
    }

    public class AjusteRequest
    {
        public DateTime? Fecha { get; set; }
        /// <summary>Cuantos viajes sumar (o restar, en negativo). Si viene Importe, se ignora.</summary>
        public int Cantidad { get; set; } = 1;
        /// <summary>Importe exacto, si no se quiere usar la tarifa. Puede ser negativo (descuento).</summary>
        public decimal? Importe { get; set; }
        public string? Detalle { get; set; }
    }

    /// <summary>Suma (o resta) viajes a mano: un extra que le reconoces, ir a buscar mercaderia, etc.</summary>
    [HttpPost("admin/empleados/{id:int}/ajuste")]
    [Authorize]
    public async Task<IActionResult> CrearAjuste(int id, [FromBody] AjusteRequest req)
    {
        var emp = await _db.ViajesEmpleados.FindAsync(id);
        if (emp is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Detalle)) return BadRequest(new { error = "Poné por qué es el ajuste" });

        var hoy = FechaArgentinaHoy();
        var fecha = (req.Fecha?.Date ?? hoy);
        if (fecha > hoy) return BadRequest(new { error = "No podés cargar fechas futuras" });

        var creados = new List<ViajesEntrega>();
        if (req.Importe.HasValue && req.Importe.Value != 0)
        {
            creados.Add(new ViajesEntrega
            {
                EmpleadoId = id, Fecha = fecha, Tarifa = req.Importe.Value, Origen = "manual",
                Detalle = req.Detalle!.Trim(), CreatedAt = DateTime.UtcNow
            });
        }
        else
        {
            var cant = req.Cantidad == 0 ? 1 : req.Cantidad;
            if (Math.Abs(cant) > 50) return BadRequest(new { error = "Cantidad demasiado grande (máx 50)" });
            var signo = cant < 0 ? -1 : 1;
            for (var i = 0; i < Math.Abs(cant); i++)
                creados.Add(new ViajesEntrega
                {
                    EmpleadoId = id, Fecha = fecha, Tarifa = signo * emp.TarifaViaje, Origen = "manual",
                    Detalle = req.Detalle!.Trim(), CreatedAt = DateTime.UtcNow
                });
        }
        _db.ViajesEntregas.AddRange(creados);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, cantidad = creados.Count, importe = creados.Sum(x => x.Tarifa) });
    }

    /// <summary>Borra un ajuste cargado a mano. Los viajes que vienen del mapa no se borran de acá
    /// (se corrigen en el mapa) y los ya liquidados no se tocan.</summary>
    [HttpDelete("admin/entregas/{id:int}")]
    [Authorize]
    public async Task<IActionResult> DeleteEntrega(int id)
    {
        var e = await _db.ViajesEntregas.FindAsync(id);
        if (e is null) return NotFound();
        if (e.LiquidadoPagoId is not null) return BadRequest(new { error = "Ya está liquidado: borrá el pago primero" });
        if (e.StopId is not null) return BadRequest(new { error = "Este viaje viene de una entrega del mapa. Si no corresponde, corregí la parada en el mapa." });
        _db.ViajesEntregas.Remove(e);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    public class LiquidarRequest
    {
        public DateTime? Hasta { get; set; }
        public string? Descripcion { get; set; }
    }

    /// <summary>
    /// Cierra todo lo pendiente hasta una fecha: registra el pago por ese total y deja esos viajes
    /// marcados como liquidados (congelados: no se recalculan mas aunque cambie el mapa).
    /// </summary>
    [HttpPost("admin/empleados/{id:int}/liquidar")]
    [Authorize]
    public async Task<IActionResult> Liquidar(int id, [FromBody] LiquidarRequest req)
    {
        var emp = await _db.ViajesEmpleados.FindAsync(id);
        if (emp is null) return NotFound();
        await _auto.SincronizarAsync(emp);

        var hoy = FechaArgentinaHoy();
        var hasta = (req.Hasta?.Date ?? hoy);

        var pend = await _db.ViajesEntregas
            .Where(e => e.EmpleadoId == id && e.LiquidadoPagoId == null && e.Fecha <= hasta)
            .ToListAsync();
        if (pend.Count == 0) return BadRequest(new { error = "No hay viajes pendientes para liquidar" });

        var total = pend.Sum(x => x.Tarifa);
        var desde = pend.Min(x => x.Fecha);
        var desc = string.IsNullOrWhiteSpace(req.Descripcion)
            ? $"Liquidación {pend.Count} viajes ({desde:dd/MM} al {hasta:dd/MM})"
            : req.Descripcion!.Trim();

        var pago = new ViajesPago
        {
            EmpleadoId = id,
            Fecha = hoy,
            Descripcion = desc,
            Importe = total,
            CreatedAt = DateTime.UtcNow
        };
        _db.ViajesPagos.Add(pago);
        await _db.SaveChangesAsync();

        foreach (var e in pend) { e.LiquidadoPagoId = pago.Id; e.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();

        return Ok(new { ok = true, pagoId = pago.Id, cantidad = pend.Count, importe = total });
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static DateTime FechaArgentinaHoy() => DateTime.UtcNow.AddHours(-3).Date;
}
