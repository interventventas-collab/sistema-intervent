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
    private readonly AppDbContext _db;

    public MpController(MpAccountService service, MpSyncService syncService,
        MpPagosService pagosService, AppDbContext db)
    {
        _service = service;
        _syncService = syncService;
        _pagosService = pagosService;
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

    public record MpPagosResumenDto(int Cantidad, decimal TotalBruto, decimal TotalNeto, DateTime? UltimoCobroAt);

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
        DateTime? ultimo = cant == 0 ? null : await q.MaxAsync(p => (DateTime?)p.Fecha);
        return Ok(new MpPagosResumenDto(cant, bruto, neto, ultimo));
    }
}
