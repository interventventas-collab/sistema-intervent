using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Cuenta de Mercado Pago (API oficial) + lectura del saldo. Etapa 1: solo saldo.
/// El Access Token se carga desde la pantalla /integraciones/mercadopago y la tarjeta
/// del dashboard muestra el saldo. Pedido de Osmar 2026-07-04.
/// </summary>
[ApiController]
[Route("api/mercadopago")]
[Authorize]
public class MpController : ControllerBase
{
    private readonly MpAccountService _service;
    private readonly MpSyncService _syncService;
    private readonly MpPagosService _pagosService;
    private readonly MpReportesService _reportesService;
    private readonly AppDbContext _db;

    public MpController(MpAccountService service, MpSyncService syncService,
        MpPagosService pagosService, MpReportesService reportesService, AppDbContext db)
    {
        _service = service;
        _syncService = syncService;
        _pagosService = pagosService;
        _reportesService = reportesService;
        _db = db;
    }

    /// <summary>Devuelve la cuenta cargada (sin el token), o null si no hay.</summary>
    [HttpGet("account")]
    public async Task<IActionResult> GetAccount() => Ok(await _service.GetAsync());

    /// <summary>Crea o actualiza el Access Token / alias / horarios.</summary>
    [HttpPut("account")]
    public async Task<IActionResult> SaveAccount([FromBody] MpAccountService.SaveMpAccountRequest req)
    {
        var (ok, error, dto) = await _service.SaveAsync(req);
        if (!ok) return BadRequest(new { error });
        return Ok(dto);
    }

    public record SincronizarMpResultDto(bool Ok, decimal? Disponible, decimal? Total, string? Error);

    /// <summary>Lee el saldo ahora contra la API de Mercado Pago. Rapido (llamada HTTP directa).
    /// OJO: MP deprecó el endpoint directo de saldo; para muchas cuentas devuelve 403. El saldo
    /// real va por Reportes (Parte B). Este endpoint se mantiene por si la cuenta aún lo permite.</summary>
    [HttpPost("sincronizar")]
    public async Task<IActionResult> Sincronizar()
    {
        var r = await _syncService.SincronizarAsync();
        return Ok(new SincronizarMpResultDto(r.Ok, r.Disponible, r.Total, r.Error));
    }

    // ─────────────────────────────────────────────────────────────
    // Cobros recibidos por Mercado Pago (/v1/payments/search) — Parte A
    // ─────────────────────────────────────────────────────────────
    public record SyncPagosResultDto(bool Ok, int Nuevos, int Actualizados, int TotalTraidos, string? Error, bool Truncado);

    /// <summary>Trae los cobros de los últimos N días desde Mercado Pago y los guarda (dedup).</summary>
    [HttpPost("pagos/sincronizar")]
    public async Task<IActionResult> SincronizarPagos([FromQuery] int dias = 30)
    {
        var r = await _pagosService.SincronizarAsync(dias);
        return Ok(new SyncPagosResultDto(r.Ok, r.Nuevos, r.Actualizados, r.TotalTraidos, r.Error, r.Truncado));
    }

    public record MpPagoDto(int Id, long MpPaymentId, DateTime Fecha, string? Estado, string? EstadoDetalle,
        decimal Monto, decimal? MontoNeto, string? Descripcion, string? PayerEmail, string? PayerNombre,
        string? MedioPago, string? ReferenciaExterna, int? VentaIdAsociada);

    /// <summary>Lista los cobros guardados (filtrable por fecha y estado). Por defecto: aprobados.</summary>
    [HttpGet("pagos")]
    public async Task<IActionResult> ListarPagos([FromQuery] DateTime? desde = null,
        [FromQuery] DateTime? hasta = null, [FromQuery] string? estado = "approved")
    {
        var q = _db.MpPagos.AsQueryable();
        if (!string.IsNullOrWhiteSpace(estado) && estado != "todos")
            q = q.Where(p => p.Estado == estado);
        if (desde.HasValue) { var d = desde.Value.Date; q = q.Where(p => p.Fecha >= d); }
        if (hasta.HasValue) { var h = hasta.Value.Date.AddDays(1); q = q.Where(p => p.Fecha < h); }
        var l = await q.OrderByDescending(p => p.Fecha).Take(500)
            .Select(p => new MpPagoDto(p.Id, p.MpPaymentId, p.Fecha, p.Estado, p.EstadoDetalle,
                p.Monto, p.MontoNeto, p.Descripcion, p.PayerEmail, p.PayerNombre,
                p.MedioPago, p.ReferenciaExterna, p.VentaIdAsociada))
            .ToListAsync();
        return Ok(l);
    }

    public record MpPagosResumenDto(int Cantidad, decimal TotalBruto, decimal TotalNeto, DateTime? UltimoCobroAt,
        decimal Liberado, decimal Pendiente);

    /// <summary>Resumen de cobros aprobados en un período (para tarjetas/resúmenes).</summary>
    [HttpGet("pagos/resumen")]
    public async Task<IActionResult> ResumenPagos([FromQuery] DateTime? desde = null, [FromQuery] DateTime? hasta = null)
    {
        var q = _db.MpPagos.Where(p => p.Estado == "approved");
        if (desde.HasValue) { var d = desde.Value.Date; q = q.Where(p => p.Fecha >= d); }
        if (hasta.HasValue) { var h = hasta.Value.Date.AddDays(1); q = q.Where(p => p.Fecha < h); }
        var cant = await q.CountAsync();
        var bruto = cant == 0 ? 0m : await q.SumAsync(p => p.Monto);
        var neto = cant == 0 ? 0m : await q.SumAsync(p => p.MontoNeto ?? p.Monto);
        var liberado = cant == 0 ? 0m : await q.Where(p => p.EstadoLiberacion == "released").SumAsync(p => p.MontoNeto ?? p.Monto);
        var pendiente = cant == 0 ? 0m : await q.Where(p => p.EstadoLiberacion == "pending").SumAsync(p => p.MontoNeto ?? p.Monto);
        DateTime? ultimo = cant == 0 ? null : await q.MaxAsync(p => (DateTime?)p.Fecha);
        return Ok(new MpPagosResumenDto(cant, bruto, neto, ultimo, liberado, pendiente));
    }

    // ─────────────────────────────────────────────────────────────
    // Resumen para la tarjeta del dashboard
    // ─────────────────────────────────────────────────────────────
    public record MpDashboardDto(bool Conectada, decimal CobradoNeto30, decimal CobradoBruto30,
        int CantCobros30, decimal NetoMov30, DateTime? UltimoDato,
        decimal Liberado30, decimal Pendiente30);

    /// <summary>Resumen de los últimos 30 días para la tarjeta del dashboard (lee lo ya guardado, rápido).</summary>
    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard()
    {
        var cuenta = await _service.GetAsync();
        var conectada = cuenta is not null && cuenta.HasToken;
        var desde = DateTime.UtcNow.Date.AddDays(-30);

        var cobros = _db.MpPagos.Where(p => p.Estado == "approved" && p.Fecha >= desde);
        var cantCobros = await cobros.CountAsync();
        var cobradoBruto = cantCobros == 0 ? 0m : await cobros.SumAsync(p => p.Monto);
        var cobradoNeto = cantCobros == 0 ? 0m : await cobros.SumAsync(p => p.MontoNeto ?? p.Monto);
        DateTime? ultCobro = cantCobros == 0 ? null : await cobros.MaxAsync(p => (DateTime?)p.Fecha);
        // Ya liberado (disponible aprox) vs pendiente de liberar (money_release_status).
        var liberado = cantCobros == 0 ? 0m : await cobros.Where(p => p.EstadoLiberacion == "released").SumAsync(p => p.MontoNeto ?? p.Monto);
        var pendiente = cantCobros == 0 ? 0m : await cobros.Where(p => p.EstadoLiberacion == "pending").SumAsync(p => p.MontoNeto ?? p.Monto);

        var movs = _db.MpMovimientos.Where(m => m.Fecha >= desde);
        var netoMov = await movs.AnyAsync() ? await movs.SumAsync(m => m.MontoNeto) : 0m;
        DateTime? ultMov = await movs.AnyAsync() ? await movs.MaxAsync(m => (DateTime?)m.Fecha) : null;

        DateTime? ultimo = new[] { ultCobro, ultMov }.Where(d => d.HasValue).Max();
        return Ok(new MpDashboardDto(conectada, cobradoNeto, cobradoBruto, cantCobros, netoMov, ultimo, liberado, pendiente));
    }

    // ─────────────────────────────────────────────────────────────
    // Movimientos por Reportes "Dinero en la cuenta" — Parte B
    // ─────────────────────────────────────────────────────────────
    public record SyncMovResultDto(bool Ok, int Nuevos, int TotalFilas, string? Error, bool EnProceso);

    /// <summary>Pide el reporte de movimientos a MP, espera a que se genere y lo procesa.
    /// Puede tardar (asincrónico); si aún no está listo devuelve EnProceso=true.</summary>
    [HttpPost("movimientos/sincronizar")]
    public async Task<IActionResult> SincronizarMovimientos([FromQuery] int dias = 30)
    {
        var r = await _reportesService.SincronizarAsync(dias);
        return Ok(new SyncMovResultDto(r.Ok, r.Nuevos, r.TotalFilas, r.Error, r.EnProceso));
    }

    public record MpMovimientoDto(int Id, string? SourceId, DateTime Fecha, string? TipoTransaccion,
        string? Descripcion, decimal MontoBruto, decimal Comision, decimal MontoNeto, string? Moneda,
        string? MedioPago, string? ReferenciaExterna, int? VentaIdAsociada);

    [HttpGet("movimientos")]
    public async Task<IActionResult> ListarMovimientos([FromQuery] DateTime? desde = null, [FromQuery] DateTime? hasta = null)
    {
        var q = _db.MpMovimientos.AsQueryable();
        if (desde.HasValue) { var d = desde.Value.Date; q = q.Where(m => m.Fecha >= d); }
        if (hasta.HasValue) { var h = hasta.Value.Date.AddDays(1); q = q.Where(m => m.Fecha < h); }
        var l = await q.OrderByDescending(m => m.Fecha).Take(1000)
            .Select(m => new MpMovimientoDto(m.Id, m.SourceId, m.Fecha, m.TipoTransaccion, m.Descripcion,
                m.MontoBruto, m.Comision, m.MontoNeto, m.Moneda, m.MedioPago, m.ReferenciaExterna, m.VentaIdAsociada))
            .ToListAsync();
        return Ok(l);
    }

    public record MpMovResumenDto(int Cantidad, decimal TotalIngresos, decimal TotalEgresos,
        decimal TotalComisiones, decimal NetoPeriodo, DateTime? Desde, DateTime? Hasta);

    /// <summary>Resumen de movimientos: ingresos (neto+), egresos (neto-), comisiones y neto del período.</summary>
    [HttpGet("movimientos/resumen")]
    public async Task<IActionResult> ResumenMovimientos([FromQuery] DateTime? desde = null, [FromQuery] DateTime? hasta = null)
    {
        var q = _db.MpMovimientos.AsQueryable();
        if (desde.HasValue) { var d = desde.Value.Date; q = q.Where(m => m.Fecha >= d); }
        if (hasta.HasValue) { var h = hasta.Value.Date.AddDays(1); q = q.Where(m => m.Fecha < h); }
        var cant = await q.CountAsync();
        if (cant == 0) return Ok(new MpMovResumenDto(0, 0, 0, 0, 0, null, null));
        var ingresos = await q.Where(m => m.MontoNeto > 0).SumAsync(m => m.MontoNeto);
        var egresos = await q.Where(m => m.MontoNeto < 0).SumAsync(m => m.MontoNeto);
        var comisiones = await q.SumAsync(m => m.Comision);
        var neto = await q.SumAsync(m => m.MontoNeto);
        var min = await q.MinAsync(m => (DateTime?)m.Fecha);
        var max = await q.MaxAsync(m => (DateTime?)m.Fecha);
        return Ok(new MpMovResumenDto(cant, ingresos, egresos, comisiones, neto, min, max));
    }
}
