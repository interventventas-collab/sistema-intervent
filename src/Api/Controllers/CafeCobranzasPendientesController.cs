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

    /// <summary>Detalle de una cobranza pendiente para precargarla en el modal de Nueva cobranza.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var p = await _db.CafeCobranzasPendientes
            .Include(x => x.Venta).Include(x => x.Repartidor)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        return Ok(new PendienteDto(p.Id, p.VentaId,
            p.Venta?.Numero ?? "?", p.Venta?.ClienteId, p.Venta?.ClienteNombreSnapshot,
            p.Venta?.Total ?? 0m, p.RepartidorId, p.Repartidor?.Nombre ?? "?",
            p.Importe, p.MarcadoEntregado, p.Notas, p.Estado, p.CreatedAt));
    }

    public record VincularRequest(int CobranzaId, string? Operador);

    /// <summary>Marca la cobranza pendiente como APROBADA vinculandola a una CafeCobranza ya
    /// creada por el admin desde el modal de Nueva Cobranza. La crea_cion de la cobranza
    /// real con sus imputaciones la hace /cafe/tesoreria/cobranzas con los datos pre-cargados
    /// (cliente + efectivo + importe). Aca solo hacemos el marcado. Tambien sincroniza el
    /// estado "entregado" si el repartidor lo habia tildado.</summary>
    [HttpPost("{id:int}/vincular")]
    public async Task<IActionResult> Vincular(int id, [FromBody] VincularRequest req)
    {
        var p = await _db.CafeCobranzasPendientes.Include(x => x.Venta)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = $"Ya esta {p.Estado}" });
        p.Estado = "APROBADA";
        p.CobranzaCreadaId = req.CobranzaId;
        p.RevisadaPor = req.Operador;
        p.RevisadaAt = DateTime.UtcNow;
        // Si la cobranza tilde "entregado", actualizar la venta
        if (p.MarcadoEntregado && p.Venta is not null)
        {
            p.Venta.EntregadoPorRepartidorId = p.RepartidorId;
            p.Venta.EntregadoAt = DateTime.UtcNow;
            if (p.Venta.EstadoPreparacion != null)
            {
                var estadoAntApr1 = p.Venta.EstadoPreparacion;
                p.Venta.EstadoPreparacion = "ENTREGADO";
                p.Venta.PreparacionUpdatedAt = DateTime.UtcNow;
                // 2026-06-09 log
                _db.CafeVentaPreparacionLogs.Add(new Models.CafeVentaPreparacionLog
                {
                    VentaId = p.Venta.Id, EstadoAnterior = estadoAntApr1, EstadoNuevo = "ENTREGADO",
                    OperadorNombre = req.Operador ?? "admin",
                    Notas = "Admin asocio cobranza pendiente a venta — marca entregada",
                    CreatedAt = DateTime.UtcNow
                });
            }
        }
        await _db.SaveChangesAsync();
        return Ok();
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
                var estadoAntApr2 = p.Venta.EstadoPreparacion;
                p.Venta.EstadoPreparacion = "ENTREGADO";
                p.Venta.PreparacionUpdatedAt = DateTime.UtcNow;
                // 2026-06-09 log
                _db.CafeVentaPreparacionLogs.Add(new Models.CafeVentaPreparacionLog
                {
                    VentaId = p.Venta.Id, EstadoAnterior = estadoAntApr2, EstadoNuevo = "ENTREGADO",
                    OperadorNombre = req.Operador ?? "admin",
                    Notas = "Admin aprobo cobranza pendiente — marca entregada",
                    CreatedAt = DateTime.UtcNow
                });
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

    /// <summary>2026-06-10: Arqueo de TODOS los repartidores en un dia. Devuelve una lista
    /// de ArqueoDto, una por cada repartidor que tenga al menos una cobranza ese dia.</summary>
    [HttpGet("arqueo/todos")]
    public async Task<IActionResult> ArqueoTodos([FromQuery] DateTime? fecha)
    {
        var dia = (fecha ?? DateTime.Today).Date;
        var diaFin = dia.AddDays(1);
        var pendientes = await _db.CafeCobranzasPendientes
            .Include(p => p.Venta)
            .Include(p => p.Repartidor)
            .Where(p => p.CreatedAt >= dia && p.CreatedAt < diaFin
                && p.Estado != "RECHAZADA")
            .OrderBy(p => p.CreatedAt)
            .ToListAsync();

        var resultados = pendientes
            .GroupBy(p => new { p.RepartidorId, RepartidorNombre = p.Repartidor?.Nombre ?? "?" })
            .Select(g =>
            {
                var items = g.Select(p => new ArqueoItemDto(
                    p.VentaId, p.Venta?.Numero ?? "?", p.Venta?.ClienteNombreSnapshot,
                    p.Importe, p.MarcadoEntregado, p.Estado, p.CreatedAt)).ToList();
                var totalP = g.Where(p => p.Estado == "PENDIENTE").Sum(p => p.Importe);
                var totalA = g.Where(p => p.Estado == "APROBADA").Sum(p => p.Importe);
                var cantP = g.Count(p => p.Estado == "PENDIENTE");
                var cantA = g.Count(p => p.Estado == "APROBADA");
                return new ArqueoDto(g.Key.RepartidorId, g.Key.RepartidorNombre, dia,
                    totalP, totalA, cantP, cantA, items);
            })
            .OrderByDescending(a => a.TotalPendiente + a.TotalAprobado)
            .ToList();

        return Ok(resultados);
    }
}
