using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Admin: revisa y aprueba/rechaza las cobranzas que los repartidores precargaron en alquileres.
/// Al aprobar, el importe se suma a Alq_Reservas.MontoCobrado (baja el saldo de la reserva) y,
/// si el repartidor habia tildado entrega/retiro junto al cobro, se refleja en la reserva.
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
        DateTime CreatedAt, decimal ReservaSaldo);

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
            p.Reserva is null ? 0m : Math.Max(0m, p.Reserva.MontoTotal - p.Reserva.Sena - p.Reserva.MontoCobrado)
        )).ToList();
        return Ok(dto);
    }

    [HttpGet("count-pendientes")]
    public async Task<IActionResult> CountPendientes()
    {
        var count = await _db.AlqCobranzasPendientes.CountAsync(p => p.Estado == "PENDIENTE");
        return Ok(new { count });
    }

    public record AprobarRequest(string? Operador);

    [HttpPost("{id:int}/aprobar")]
    public async Task<IActionResult> Aprobar(int id, [FromBody] AprobarRequest req)
    {
        var p = await _db.AlqCobranzasPendientes
            .Include(x => x.Reserva).Include(x => x.Repartidor)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = $"Ya esta {p.Estado}" });
        if (p.Reserva is null) return BadRequest(new { error = "La reserva asociada no existe" });

        var now = DateTime.UtcNow;
        // Sumar al cobrado de la reserva (baja el saldo)
        p.Reserva.MontoCobrado += p.Importe;

        // Reflejar entrega/retiro si el repartidor las tildo junto al cobro
        if (p.MarcadoEntregado && !p.Reserva.EntregadoPorRepartidorId.HasValue)
        {
            p.Reserva.EntregadoPorRepartidorId = p.RepartidorId;
            p.Reserva.EntregadoAt = now;
            if (p.Reserva.Estado == "reservado" || p.Reserva.Estado == "confirmado") p.Reserva.Estado = "entregado";
        }
        if (p.MarcadoRetirado && !p.Reserva.RetiradoPorRepartidorId.HasValue)
        {
            p.Reserva.RetiradoPorRepartidorId = p.RepartidorId;
            p.Reserva.RetiradoAt = now;
            p.Reserva.Estado = "finalizado";
        }
        p.Reserva.UpdatedAt = now;

        p.Estado = "APROBADA";
        p.RevisadaPor = req.Operador;
        p.RevisadaAt = now;

        await _db.SaveChangesAsync();
        var saldo = Math.Max(0m, p.Reserva.MontoTotal - p.Reserva.Sena - p.Reserva.MontoCobrado);
        return Ok(new { id = p.Id, reservaSaldo = saldo });
    }

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
