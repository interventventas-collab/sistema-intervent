using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/nominas")]
[Authorize]
public class NomLiquidacionesController : ControllerBase
{
    private readonly AppDbContext _db;
    private static readonly string[] EstadosValidos = { "pendiente", "pagado", "anulada" };

    public NomLiquidacionesController(AppDbContext db) { _db = db; }

    // ============================================================
    // LIQUIDACIONES
    // ============================================================

    [HttpGet("liquidaciones")]
    public async Task<IActionResult> GetAll([FromQuery] int? anio = null, [FromQuery] int? mes = null, [FromQuery] string? estado = null)
    {
        var q = _db.NomLiquidaciones
            .Include(l => l.EmpleadoNav)
            .Include(l => l.Pagos)
            .AsQueryable();
        if (anio.HasValue) q = q.Where(l => l.Anio == anio.Value);
        if (mes.HasValue) q = q.Where(l => l.Mes == mes.Value);
        if (!string.IsNullOrWhiteSpace(estado)) { var e = estado.Trim().ToLowerInvariant(); q = q.Where(l => l.Estado == e); }
        var list = await q.OrderByDescending(l => l.Anio).ThenByDescending(l => l.Mes).ThenBy(l => l.EmpleadoNav!.Nombre).ToListAsync();
        return Ok(list.Select(Map).ToList());
    }

    [HttpGet("liquidaciones/{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var l = await _db.NomLiquidaciones
            .Include(l => l.EmpleadoNav)
            .Include(l => l.Pagos)
            .FirstOrDefaultAsync(l => l.Id == id);
        if (l is null) return NotFound(new { error = "Liquidacion no encontrada" });
        return Ok(Map(l));
    }

    [HttpPost("liquidaciones")]
    public async Task<IActionResult> Create([FromBody] CreateNomLiquidacionRequest req)
    {
        var emp = await _db.NomEmpleados.FindAsync(req.EmpleadoId);
        if (emp is null) return BadRequest(new { error = "Empleado no encontrado" });
        if (req.Anio < 2000 || req.Anio > 2100) return BadRequest(new { error = "Año invalido" });
        if (req.Mes < 1 || req.Mes > 12) return BadRequest(new { error = "Mes invalido (1-12)" });

        // Una sola liquidacion por (empleado, año, mes)
        var existe = await _db.NomLiquidaciones.AnyAsync(l => l.EmpleadoId == req.EmpleadoId && l.Anio == req.Anio && l.Mes == req.Mes);
        if (existe) return BadRequest(new { error = $"Ya existe una liquidacion de {emp.Nombre} para {req.Mes:00}/{req.Anio}" });

        var liq = new NomLiquidacion
        {
            EmpleadoId = req.EmpleadoId,
            Anio = req.Anio,
            Mes = req.Mes,
            HorasTrabajadas = Math.Max(0m, req.HorasTrabajadas),
            HorasExtra = Math.Max(0m, req.HorasExtra),
            RecargoHsExtraPct = req.RecargoHsExtraPct ?? 0m,
            DiasAusencia = Math.Max(0m, req.DiasAusencia),
            DiasVacaciones = Math.Max(0m, req.DiasVacaciones),
            Comision = Math.Max(0m, req.Comision),
            Bonos = Math.Max(0m, req.Bonos),
            Aguinaldo = Math.Max(0m, req.Aguinaldo),
            Adelantos = Math.Max(0m, req.Adelantos),
            OtrosDescuentos = Math.Max(0m, req.OtrosDescuentos),
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            Estado = "pendiente",
            CreatedAt = DateTime.UtcNow
        };
        Calcular(liq, emp);
        _db.NomLiquidaciones.Add(liq);
        await _db.SaveChangesAsync();

        var saved = await _db.NomLiquidaciones
            .Include(l => l.EmpleadoNav)
            .Include(l => l.Pagos)
            .FirstAsync(l => l.Id == liq.Id);
        return Ok(Map(saved));
    }

    [HttpPut("liquidaciones/{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateNomLiquidacionRequest req)
    {
        var liq = await _db.NomLiquidaciones.Include(l => l.Pagos).FirstOrDefaultAsync(l => l.Id == id);
        if (liq is null) return NotFound(new { error = "Liquidacion no encontrada" });
        var emp = await _db.NomEmpleados.FindAsync(liq.EmpleadoId);
        if (emp is null) return BadRequest(new { error = "Empleado no encontrado" });

        if (req.HorasTrabajadas.HasValue) liq.HorasTrabajadas = Math.Max(0m, req.HorasTrabajadas.Value);
        if (req.HorasExtra.HasValue) liq.HorasExtra = Math.Max(0m, req.HorasExtra.Value);
        if (req.RecargoHsExtraPct.HasValue) liq.RecargoHsExtraPct = Math.Max(0m, req.RecargoHsExtraPct.Value);
        if (req.DiasAusencia.HasValue) liq.DiasAusencia = Math.Max(0m, req.DiasAusencia.Value);
        if (req.DiasVacaciones.HasValue) liq.DiasVacaciones = Math.Max(0m, req.DiasVacaciones.Value);
        if (req.Comision.HasValue) liq.Comision = Math.Max(0m, req.Comision.Value);
        if (req.Bonos.HasValue) liq.Bonos = Math.Max(0m, req.Bonos.Value);
        if (req.Aguinaldo.HasValue) liq.Aguinaldo = Math.Max(0m, req.Aguinaldo.Value);
        if (req.Adelantos.HasValue) liq.Adelantos = Math.Max(0m, req.Adelantos.Value);
        if (req.OtrosDescuentos.HasValue) liq.OtrosDescuentos = Math.Max(0m, req.OtrosDescuentos.Value);
        if (req.Notas is not null) liq.Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim();
        if (req.Estado is not null)
        {
            var ne = req.Estado.Trim().ToLowerInvariant();
            if (!EstadosValidos.Contains(ne)) return BadRequest(new { error = $"Estado invalido. Validos: {string.Join(", ", EstadosValidos)}" });
            liq.Estado = ne;
        }

        Calcular(liq, emp);
        liq.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var saved = await _db.NomLiquidaciones
            .Include(l => l.EmpleadoNav)
            .Include(l => l.Pagos)
            .FirstAsync(l => l.Id == liq.Id);
        return Ok(Map(saved));
    }

    [HttpDelete("liquidaciones/{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var liq = await _db.NomLiquidaciones.Include(l => l.Pagos).FirstOrDefaultAsync(l => l.Id == id);
        if (liq is null) return NotFound(new { error = "Liquidacion no encontrada" });
        if (liq.Pagos.Any())
            return BadRequest(new { error = "No se puede eliminar: ya tiene pagos asociados. Borrá los pagos primero o cambiala a 'anulada'." });
        _db.NomLiquidaciones.Remove(liq);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    // ============================================================
    // PAGOS
    // ============================================================

    [HttpPost("pagos")]
    public async Task<IActionResult> CreatePago([FromBody] CreateNomPagoRequest req)
    {
        var liq = await _db.NomLiquidaciones.Include(l => l.Pagos).FirstOrDefaultAsync(l => l.Id == req.LiquidacionId);
        if (liq is null) return BadRequest(new { error = "Liquidacion no encontrada" });
        if (liq.Estado == "anulada") return BadRequest(new { error = "No se puede pagar una liquidacion anulada" });
        if (req.Monto <= 0) return BadRequest(new { error = "El monto debe ser mayor a 0" });
        if (string.IsNullOrWhiteSpace(req.Metodo)) return BadRequest(new { error = "Indicá un metodo de pago" });

        var pagado = liq.Pagos.Sum(p => p.Monto);
        var saldo = liq.NetoAPagar - pagado;
        if (req.Monto > saldo + 0.01m)
            return BadRequest(new { error = $"El monto excede el saldo pendiente (${saldo:N2})" });

        // Concepto: validamos contra la lista permitida; si viene vacio o invalido, default a "sueldo".
        var conceptosValidos = new HashSet<string> { "sueldo", "comision_cafe", "horas_extra", "bono", "adelanto", "aguinaldo", "otro" };
        var concepto = (req.Concepto ?? "sueldo").Trim().ToLowerInvariant();
        if (!conceptosValidos.Contains(concepto)) concepto = "otro";

        var pago = new NomPago
        {
            LiquidacionId = req.LiquidacionId,
            FechaPago = (req.FechaPago ?? DateTime.Today).Date,
            Metodo = req.Metodo.Trim().ToLowerInvariant(),
            Monto = req.Monto,
            Concepto = concepto,
            Detalle = string.IsNullOrWhiteSpace(req.Detalle) ? null : req.Detalle.Trim(),
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _db.NomPagos.Add(pago);

        // Si con este pago se cancela la liquidacion → estado pagado
        if (pagado + req.Monto >= liq.NetoAPagar - 0.01m)
        {
            liq.Estado = "pagado";
            liq.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        var saved = await _db.NomLiquidaciones
            .Include(l => l.EmpleadoNav)
            .Include(l => l.Pagos)
            .FirstAsync(l => l.Id == liq.Id);
        return Ok(Map(saved));
    }

    [HttpDelete("pagos/{id:int}")]
    public async Task<IActionResult> DeletePago(int id)
    {
        var pago = await _db.NomPagos.FindAsync(id);
        if (pago is null) return NotFound(new { error = "Pago no encontrado" });
        var liq = await _db.NomLiquidaciones.FindAsync(pago.LiquidacionId);
        _db.NomPagos.Remove(pago);
        // Si estaba pagada y ahora queda saldo → vuelve a pendiente
        if (liq is not null && liq.Estado == "pagado")
        {
            liq.Estado = "pendiente";
            liq.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    // ============================================================
    // REPORTES
    // ============================================================

    [HttpGet("resumen")]
    public async Task<IActionResult> Resumen([FromQuery] int anio, [FromQuery] int mes)
    {
        if (anio < 2000 || mes < 1 || mes > 12) return BadRequest(new { error = "Año/mes invalido" });
        var liqs = await _db.NomLiquidaciones
            .Include(l => l.Pagos)
            .Where(l => l.Anio == anio && l.Mes == mes && l.Estado != "anulada")
            .ToListAsync();
        var totalPagado = liqs.Sum(l => l.Pagos.Sum(p => p.Monto));
        return Ok(new NomResumenMensualDto(
            anio, mes,
            liqs.Count,
            liqs.Sum(l => l.TotalGanado),
            liqs.Sum(l => l.TotalDescuentos),
            liqs.Sum(l => l.NetoAPagar),
            totalPagado,
            liqs.Sum(l => l.NetoAPagar) - totalPagado));
    }

    // ============================================================
    // CALCULO
    // ============================================================

    /// <summary>
    /// Calcula los totales de la liquidacion segun los insumos cargados y los datos del empleado.
    /// Reglas:
    ///  - Sueldo base = empleado.SueldoBase
    ///  - Hora extra con recargo = ValorHora * (1 + RecargoHsExtraPct/100)
    ///  - Monto hs extra = HorasExtra * HoraExtraConRecargo
    ///  - Descuento por dia de ausencia = DiasAusencia * (SueldoBase / 30)
    ///  - TOTAL GANADO = Base + MontoHsExtra + Comision + Bonos + Aguinaldo
    ///  - TOTAL DESCUENTOS = DescuentoFaltas + Adelantos + OtrosDescuentos
    ///  - NETO = Ganado - Descuentos
    /// </summary>
    private static void Calcular(NomLiquidacion liq, NomEmpleado emp)
    {
        liq.SueldoBase = emp.SueldoBase;
        var hsExtraConRecargo = emp.ValorHora * (1m + liq.RecargoHsExtraPct / 100m);
        liq.MontoHsExtra = Math.Round(liq.HorasExtra * hsExtraConRecargo, 2, MidpointRounding.AwayFromZero);
        var diaProporcional = emp.SueldoBase / 30m;
        liq.DescuentoFaltas = Math.Round(liq.DiasAusencia * diaProporcional, 2, MidpointRounding.AwayFromZero);
        liq.TotalGanado = liq.SueldoBase + liq.MontoHsExtra + liq.Comision + liq.Bonos + liq.Aguinaldo;
        liq.TotalDescuentos = liq.DescuentoFaltas + liq.Adelantos + liq.OtrosDescuentos;
        liq.NetoAPagar = Math.Round(liq.TotalGanado - liq.TotalDescuentos, 2, MidpointRounding.AwayFromZero);
    }

    private static NomLiquidacionDto Map(NomLiquidacion l)
    {
        var totalPagado = l.Pagos.Sum(p => p.Monto);
        return new NomLiquidacionDto(
            l.Id, l.EmpleadoId, l.EmpleadoNav?.Nombre ?? "—", l.EmpleadoNav?.Puesto,
            l.Anio, l.Mes,
            l.HorasTrabajadas, l.HorasExtra, l.RecargoHsExtraPct,
            l.DiasAusencia, l.DiasVacaciones,
            l.SueldoBase, l.MontoHsExtra, l.Comision, l.Bonos,
            l.Aguinaldo,
            l.DescuentoFaltas, l.Adelantos, l.OtrosDescuentos,
            l.TotalGanado, l.TotalDescuentos, l.NetoAPagar,
            l.Estado, l.Notas,
            totalPagado, l.NetoAPagar - totalPagado,
            l.CreatedAt, l.UpdatedAt,
            l.Pagos.OrderByDescending(p => p.FechaPago).Select(p => new NomPagoDto(
                p.Id, p.LiquidacionId, p.FechaPago, p.Metodo, p.Monto,
                p.Concepto, p.Detalle,
                p.Notas, p.CreatedAt)).ToList());
    }
}
