using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Cobranzas precargadas por los repartidores desde la pantalla mobile /repartidor/{token}.
/// El admin las revisa aca y las APRUEBA (que crea CafeCobranza real) o RECHAZA. Pedido 2026-05-19.
/// </summary>
[ApiController]
[Route("api/cafe/cobranzas-pendientes")]
[Authorize]
public class CafeCobranzasPendientesController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafeCobranzasPendientesController(AppDbContext db) { _db = db; }

    public record PendienteDto(int Id, int VentaId, string VentaNumero, int? ClienteId, string? ClienteNombre,
        decimal VentaTotal, int RepartidorId, string RepartidorNombre, decimal Importe,
        bool MarcadoEntregado, string? Notas, string Estado, DateTime CreatedAt);

    public record ArqueoItemDto(int VentaId, string VentaNumero, string? ClienteNombre, decimal Importe,
        bool MarcadoEntregado, string Estado, DateTime CreatedAt);
    public record ArqueoDto(int RepartidorId, string RepartidorNombre, DateTime Fecha,
        decimal TotalPendiente, decimal TotalAprobado, int CantPendiente, int CantAprobado, List<ArqueoItemDto> Items);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? estado = "PENDIENTE")
    {
        var q = _db.CafeCobranzasPendientes
            .Include(p => p.Venta)
            .Include(p => p.Repartidor)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(estado) && estado != "todos")
            q = q.Where(p => p.Estado == estado.ToUpperInvariant());
        var l = await q.OrderByDescending(p => p.CreatedAt)
            .Select(p => new PendienteDto(p.Id, p.VentaId,
                p.Venta!.Numero, p.Venta.ClienteId, p.Venta.ClienteNombreSnapshot,
                p.Venta.Total, p.RepartidorId, p.Repartidor!.Nombre,
                p.Importe, p.MarcadoEntregado, p.Notas, p.Estado, p.CreatedAt))
            .ToListAsync();
        return Ok(l);
    }

    /// <summary>Cantidad de cobranzas pendientes (para badge en topbar).</summary>
    [HttpGet("count-pendientes")]
    public async Task<IActionResult> CountPendientes()
    {
        var c = await _db.CafeCobranzasPendientes.CountAsync(p => p.Estado == "PENDIENTE");
        return Ok(new { count = c });
    }

    public record AprobarRequest(string? Operador, int? CajaId);

    /// <summary>Aprueba una cobranza pendiente — crea una CafeCobranza real con un solo medio
    /// (efectivo). El CajaId opcional es la caja de efectivo a usar; si no viene, usa la primera
    /// caja activa tipo EFECTIVO. Imputa el importe contra la venta del repartidor.</summary>
    [HttpPost("{id:int}/aprobar")]
    public async Task<IActionResult> Aprobar(int id, [FromBody] AprobarRequest req)
    {
        var p = await _db.CafeCobranzasPendientes.Include(x => x.Venta).Include(x => x.Repartidor)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = $"Ya esta {p.Estado}" });
        if (p.Venta is null) return BadRequest(new { error = "La venta asociada no existe" });

        // Buscar caja de efectivo
        var caja = req.CajaId.HasValue
            ? await _db.CafeCajas.FirstOrDefaultAsync(c => c.Id == req.CajaId.Value)
            : await _db.CafeCajas.FirstOrDefaultAsync(c => c.IsActive && c.Tipo == "EFECTIVO");
        if (caja is null) return BadRequest(new { error = "No hay caja de efectivo activa. Crea una en Tesoreria → Cajas." });

        // Numero de cobranza correlativo (similar logica que CafeCobranzasController)
        var prox = (await _db.CafeCobranzas.OrderByDescending(c => c.Id).Select(c => (int?)c.Id).FirstOrDefaultAsync() ?? 0) + 1;
        var numero = $"COB-{DateTime.Now.Year:0000}-{prox:0000}";

        var cobranza = new CafeCobranza
        {
            Numero = numero,
            Fecha = DateTime.UtcNow,
            ClienteId = p.Venta.ClienteId ?? 0,
            Total = p.Importe,
            Retenciones = 0,
            Operador = req.Operador ?? p.Repartidor?.Nombre,
            Observaciones = $"Auto-generada desde cobranza precargada #{p.Id} por {p.Repartidor?.Nombre} (repartidor)" + (string.IsNullOrEmpty(p.Notas) ? "" : $" — {p.Notas}"),
            Estado = "VIGENTE",
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeCobranzas.Add(cobranza);
        await _db.SaveChangesAsync();

        // Imputacion a la venta
        _db.CafeCobranzasComprobantes.Add(new CafeCobranzaComprobante
        {
            CobranzaId = cobranza.Id,
            VentaId = p.VentaId,
            Importe = p.Importe
        });
        // Medio: efectivo en la caja
        _db.CafeCobranzasMedios.Add(new CafeCobranzaMedio
        {
            CobranzaId = cobranza.Id,
            CajaId = caja.Id,
            Importe = p.Importe,
            Referencia = $"Cobrado por {p.Repartidor?.Nombre}"
        });

        // Sincronizar IsPaid si saldo cubierto
        var totalPagado = await _db.CafeCobranzasComprobantes
            .Where(c => c.VentaId == p.VentaId && c.Cobranza!.Estado == "VIGENTE").SumAsync(c => c.Importe);
        totalPagado += p.Importe; // incluir la que estamos por guardar
        var totalCobrable = (p.Venta.ArcaImpTotal.HasValue && p.Venta.ArcaImpTotal.Value > 0m) ? p.Venta.ArcaImpTotal.Value : p.Venta.Total;
        p.Venta.IsPaid = totalPagado >= totalCobrable - 0.01m;

        // Si tilde "entregue", anotar repartidor + actualizar tablero de preparacion
        if (p.MarcadoEntregado)
        {
            p.Venta.EntregadoPorRepartidorId = p.RepartidorId;
            p.Venta.EntregadoAt = DateTime.UtcNow;
            if (p.Venta.EstadoPreparacion != null)
            {
                p.Venta.EstadoPreparacion = "ENTREGADO";
                p.Venta.PreparacionUpdatedAt = DateTime.UtcNow;
            }
        }

        p.Estado = "APROBADA";
        p.CobranzaCreadaId = cobranza.Id;
        p.RevisadaPor = req.Operador;
        p.RevisadaAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new { id = p.Id, cobranzaId = cobranza.Id, numero });
    }

    public record RechazarRequest(string? Motivo, string? Operador);

    [HttpPost("{id:int}/rechazar")]
    public async Task<IActionResult> Rechazar(int id, [FromBody] RechazarRequest req)
    {
        var p = await _db.CafeCobranzasPendientes.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = $"Ya esta {p.Estado}" });
        p.Estado = "RECHAZADA";
        p.RechazadaMotivo = req.Motivo?.Trim();
        p.RevisadaPor = req.Operador;
        p.RevisadaAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok();
    }

    /// <summary>Arqueo del dia para un repartidor: muestra lo que cobro hoy (sumando aprobadas
    /// + pendientes), ordenado por venta. Sirve para que el repartidor rinda la plata.</summary>
    [HttpGet("arqueo/{repartidorId:int}")]
    public async Task<IActionResult> Arqueo(int repartidorId, [FromQuery] DateTime? fecha)
    {
        var dia = (fecha ?? DateTime.Today).Date;
        var diaFin = dia.AddDays(1);
        var rep = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.Id == repartidorId);
        if (rep is null) return NotFound();
        var pendientes = await _db.CafeCobranzasPendientes
            .Include(p => p.Venta)
            .Where(p => p.RepartidorId == repartidorId
                && p.CreatedAt >= dia && p.CreatedAt < diaFin
                && p.Estado != "RECHAZADA")
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();
        var items = pendientes.Select(p => new ArqueoItemDto(
            p.VentaId, p.Venta?.Numero ?? "?", p.Venta?.ClienteNombreSnapshot,
            p.Importe, p.MarcadoEntregado, p.Estado, p.CreatedAt)).ToList();
        var totalPendiente = pendientes.Where(p => p.Estado == "PENDIENTE").Sum(p => p.Importe);
        var totalAprobado = pendientes.Where(p => p.Estado == "APROBADA").Sum(p => p.Importe);
        var cantP = pendientes.Count(p => p.Estado == "PENDIENTE");
        var cantA = pendientes.Count(p => p.Estado == "APROBADA");
        return Ok(new ArqueoDto(rep.Id, rep.Nombre, dia, totalPendiente, totalAprobado, cantP, cantA, items));
    }
}
