using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Admin: revisa y rechaza las cobranzas que los repartidores precargaron en alquileres.
/// 2026-09-14: se procesan en Tesorería como las de ventas (recibo + caja) y quedan aprobadas por Vincular.
/// Espejo de CafeCobranzasPendientesController. Pedido 2026-06-26.
/// </summary>
[ApiController]
[Route("api/alquileres/cobranzas-pendientes")]
[Authorize]
public class AlqCobranzasPendientesController : ControllerBase
{
    private readonly AppDbContext _db;
    public AlqCobranzasPendientesController(AppDbContext db) { _db = db; }

    public record PendienteDto(
        int Id, int ReservaId, string ReservaNumero, string ClienteNombre,
        int RepartidorId, string RepartidorNombre,
        decimal Importe, string Tipo, bool MarcadoEntregado, bool MarcadoRetirado,
        string? Notas, string Estado, string? RechazadaMotivo,
        DateTime CreatedAt, decimal ReservaSaldo,
        // 2026-09-14: para "Procesar cobranza →" (abre Tesorería con este cliente) y para saber si
        // ya tiene recibo (entonces se anula desde Tesorería, no desde acá).
        int? ClienteId = null, int? CobranzaCreadaId = null);

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? estado = null, [FromQuery] int? repartidorId = null)
    {
        var q = _db.AlqCobranzasPendientes
            .Include(p => p.Reserva).ThenInclude(r => r!.ClienteNav)
            .Include(p => p.Repartidor)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(estado))
            q = q.Where(p => p.Estado == estado.ToUpper());
        if (repartidorId.HasValue)
            q = q.Where(p => p.RepartidorId == repartidorId.Value);

        var list = await q.OrderByDescending(p => p.CreatedAt).Take(500).ToListAsync();
        var dto = list.Select(p => new PendienteDto(
            p.Id, p.ReservaId, p.Reserva?.Numero ?? "?", p.Reserva?.ClienteNav?.Nombre ?? "—",
            p.RepartidorId, p.Repartidor?.Nombre ?? "—",
            p.Importe, p.Tipo, p.MarcadoEntregado, p.MarcadoRetirado,
            p.Notas, p.Estado, p.RechazadaMotivo,
            p.CreatedAt,
            p.Reserva is null ? 0m : Math.Max(0m, p.Reserva.MontoTotal - p.Reserva.Sena - p.Reserva.MontoCobrado),
            p.Reserva?.ClienteId, p.CobranzaCreadaId
        )).ToList();
        return Ok(dto);
    }

    /// <summary>Un cobro puntual, para precargar la Nueva cobranza de Tesorería.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var p = await _db.AlqCobranzasPendientes
            .Include(x => x.Reserva).ThenInclude(r => r!.ClienteNav)
            .Include(x => x.Repartidor)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        return Ok(new PendienteDto(
            p.Id, p.ReservaId, p.Reserva?.Numero ?? "?", p.Reserva?.ClienteNav?.Nombre ?? "—",
            p.RepartidorId, p.Repartidor?.Nombre ?? "—",
            p.Importe, p.Tipo, p.MarcadoEntregado, p.MarcadoRetirado,
            p.Notas, p.Estado, p.RechazadaMotivo, p.CreatedAt,
            p.Reserva is null ? 0m : Math.Max(0m, p.Reserva.MontoTotal - p.Reserva.Sena - p.Reserva.MontoCobrado),
            p.Reserva?.ClienteId, p.CobranzaCreadaId));
    }

    public record VincularRequest(int CobranzaId, string? Operador);

    /// <summary>
    /// 2026-09-14 — Se procesó en Tesorería: queda APROBADA atada a esa cobranza.
    ///
    /// ⚠ NO suma a MontoCobrado: eso ya lo hizo la cobranza al imputarle la reserva
    /// (CafeCobranzasController.Crear). Si se sumara acá también, la reserva quedaría cobrada
    /// dos veces. Lo único que se copia del "Aprobar" viejo es marcar la entrega o el retiro.
    /// </summary>
    [HttpPost("{id:int}/vincular")]
    public async Task<IActionResult> Vincular(int id, [FromBody] VincularRequest req)
    {
        var p = await _db.AlqCobranzasPendientes.Include(x => x.Reserva).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = $"Ya esta {p.Estado}" });
        var cobranzaOk = await _db.CafeCobranzas.AnyAsync(c => c.Id == req.CobranzaId && c.Estado == "VIGENTE");
        if (!cobranzaOk) return BadRequest(new { error = "La cobranza no existe o está anulada" });

        var now = DateTime.UtcNow;
        if (p.Reserva is not null)
        {
            // Con la fecha en que el repartidor lo marcó, no la de la aprobación (mismo arreglo que ventas 03/07).
            if (p.MarcadoEntregado && !p.Reserva.EntregadoPorRepartidorId.HasValue)
            {
                p.Reserva.EntregadoPorRepartidorId = p.RepartidorId;
                p.Reserva.EntregadoAt = p.CreatedAt;
                if (p.Reserva.Estado == "reservado" || p.Reserva.Estado == "confirmado") p.Reserva.Estado = "entregado";
            }
            if (p.MarcadoRetirado && !p.Reserva.RetiradoPorRepartidorId.HasValue)
            {
                p.Reserva.RetiradoPorRepartidorId = p.RepartidorId;
                p.Reserva.RetiradoAt = p.CreatedAt;
                p.Reserva.Estado = "finalizado";
            }
            p.Reserva.UpdatedAt = now;
        }
        p.Estado = "APROBADA";
        p.CobranzaCreadaId = req.CobranzaId;
        p.RevisadaPor = req.Operador;
        p.RevisadaAt = now;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpGet("count-pendientes")]
    public async Task<IActionResult> CountPendientes()
    {
        var count = await _db.AlqCobranzasPendientes.CountAsync(p => p.Estado == "PENDIENTE");
        return Ok(new { count });
    }

    // 2026-09-14: acá estaba "Aprobar", que sumaba a MontoCobrado sin recibo ni caja. Se sacó a propósito:
    // los cobros de alquiler ahora se procesan en Tesorería (ver Vincular), igual que los de ventas.
    // Dejarlo vivo era dejar abierta la puerta para que la plata no entre a la caja Efectivo.

    public record RechazarRequest(string? Motivo, string? Operador);

    [HttpPost("{id:int}/rechazar")]
    public async Task<IActionResult> Rechazar(int id, [FromBody] RechazarRequest req)
    {
        var p = await _db.AlqCobranzasPendientes.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = $"Ya esta {p.Estado}" });
        p.Estado = "RECHAZADA";
        p.RechazadaMotivo = req.Motivo?.Trim();
        p.RevisadaPor = req.Operador;
        p.RevisadaAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok();
    }

    public record RestaurarRequest(string? Operador);

    /// <summary>Devuelve una cobranza RECHAZADA a PENDIENTE (se rechazó por error). No mueve saldos:
    /// vuelve a la cola para que el admin la apruebe o rechace de nuevo. Pedido 2026-07-07.</summary>
    [HttpPost("{id:int}/restaurar")]
    public async Task<IActionResult> Restaurar(int id, [FromBody] RestaurarRequest req)
    {
        var p = await _db.AlqCobranzasPendientes.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado != "RECHAZADA") return BadRequest(new { error = $"Solo se puede restaurar una cobranza RECHAZADA (esta está {p.Estado})." });
        p.Estado = "PENDIENTE";
        p.RechazadaMotivo = null;
        p.RevisadaPor = req.Operador;
        p.RevisadaAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    public record AnularRequest(string? Password, string? Operador);

    /// <summary>Anula un cobro YA APROBADO (se cargó por error). Pide clave del usuario, revierte
    /// el MontoCobrado de la reserva (sube el saldo) y lo deja como RECHAZADA. Pedido 2026-06-26.</summary>
    [HttpPost("{id:int}/anular")]
    public async Task<IActionResult> Anular(int id, [FromBody] AnularRequest req)
    {
        var p = await _db.AlqCobranzasPendientes.Include(x => x.Reserva).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado != "APROBADA") return BadRequest(new { error = $"Solo se puede anular una cobranza APROBADA (esta está {p.Estado})." });
        // 2026-09-14: si se procesó con recibo, la plata está en la caja y en la reserva POR la cobranza.
        // Anular acá restaría la reserva sin sacar la plata de la caja: se anula el recibo en Tesorería.
        if (p.CobranzaCreadaId.HasValue)
            return BadRequest(new { error = "Este cobro tiene recibo: anulalo desde Tesorería → Cobranzas." });

        // Pedir clave del usuario actual (acción sensible: mueve plata).
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                       ?? User.FindFirst("sub")?.Value;
        if (!int.TryParse(userIdClaim, out var userId)) return Unauthorized(new { error = "Sesión inválida" });
        var user = await _db.Users.FindAsync(userId);
        if (user is null) return Unauthorized(new { error = "Usuario no encontrado" });
        if (string.IsNullOrEmpty(req?.Password) || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
            return BadRequest(new { error = "Clave incorrecta" });

        // Revertir el efecto en la reserva (sube el saldo de nuevo).
        if (p.Reserva is not null)
        {
            p.Reserva.MontoCobrado = Math.Max(0m, p.Reserva.MontoCobrado - p.Importe);
            p.Reserva.UpdatedAt = DateTime.UtcNow;
        }
        p.Estado = "RECHAZADA";
        p.RechazadaMotivo = "Anulada (estaba aprobada)" + (string.IsNullOrEmpty(req.Operador) ? "" : $" por {req.Operador}");
        p.RevisadaPor = req.Operador;
        p.RevisadaAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    public record ArqueoItemDto(int ReservaId, string ReservaNumero, string? ClienteNombre,
        decimal Importe, string Tipo, string Estado, DateTime CreatedAt);
    public record ArqueoDto(int RepartidorId, string RepartidorNombre, DateTime Dia,
        decimal TotalPendiente, decimal TotalAprobado, int CantPendiente, int CantAprobado,
        List<ArqueoItemDto> Items);

    [HttpGet("arqueo/{repartidorId:int}")]
    public async Task<IActionResult> Arqueo(int repartidorId, [FromQuery] DateTime? fecha)
    {
        var dia = (fecha ?? DateTime.Today).Date;
        var diaFin = dia.AddDays(1);
        var rep = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.Id == repartidorId);
        if (rep is null) return NotFound();
        var pend = await _db.AlqCobranzasPendientes
            .Include(p => p.Reserva).ThenInclude(r => r!.ClienteNav)
            .Where(p => p.RepartidorId == repartidorId
                && p.CreatedAt >= dia && p.CreatedAt < diaFin
                && p.Estado != "RECHAZADA")
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();
        var items = pend.Select(p => new ArqueoItemDto(
            p.ReservaId, p.Reserva?.Numero ?? "?", p.Reserva?.ClienteNav?.Nombre,
            p.Importe, p.Tipo, p.Estado, p.CreatedAt)).ToList();
        var totalPend = pend.Where(p => p.Estado == "PENDIENTE").Sum(p => p.Importe);
        var totalApr = pend.Where(p => p.Estado == "APROBADA").Sum(p => p.Importe);
        return Ok(new ArqueoDto(rep.Id, rep.Nombre, dia, totalPend, totalApr,
            pend.Count(p => p.Estado == "PENDIENTE"), pend.Count(p => p.Estado == "APROBADA"), items));
    }
}
