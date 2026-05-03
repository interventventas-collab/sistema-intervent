using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/alquileres/reservas")]
[Authorize]
public class AlqReservasController : ControllerBase
{
    private readonly AppDbContext _db;
    private static readonly string[] EstadosValidos = { "reservado", "confirmado", "entregado", "finalizado", "cancelado" };

    public AlqReservasController(AppDbContext db) { _db = db; }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? estado = null)
    {
        var q = _db.AlqReservas
            .Include(r => r.ClienteNav)
            .Include(r => r.Items).ThenInclude(i => i.EquipoNav)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(estado))
        {
            var e = estado.Trim().ToLowerInvariant();
            q = q.Where(r => r.Estado == e);
        }
        var list = await q.OrderByDescending(r => r.FechaEntrega).ThenByDescending(r => r.Id).ToListAsync();
        return Ok(list.Select(Map).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var r = await _db.AlqReservas
            .Include(r => r.ClienteNav)
            .Include(r => r.Items).ThenInclude(i => i.EquipoNav)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (r is null) return NotFound(new { error = "Reserva no encontrada" });
        return Ok(Map(r));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAlqReservaRequest req)
    {
        if (req.ClienteId <= 0) return BadRequest(new { error = "Tenés que elegir un cliente" });
        if (req.FechaEntrega == default || req.FechaRetiro == default)
            return BadRequest(new { error = "Las fechas de entrega y retiro son obligatorias" });
        if (req.FechaRetiro < req.FechaEntrega)
            return BadRequest(new { error = "La fecha de retiro no puede ser anterior a la de entrega" });
        if (req.Items is null || req.Items.Count == 0)
            return BadRequest(new { error = "Agregá al menos un equipo a la reserva" });

        var cliente = await _db.AlqClientes.FindAsync(req.ClienteId);
        if (cliente is null) return BadRequest(new { error = "Cliente no encontrado" });

        // Consolidar items por EquipoId (sumar cantidades si vinieron repetidos)
        var consolidados = req.Items
            .GroupBy(i => i.EquipoId)
            .Select(g => new { EquipoId = g.Key, Cantidad = g.Sum(x => x.Cantidad), PrecioUnitario = g.First().PrecioUnitario })
            .ToList();

        // Validar disponibilidad por equipo en el rango
        foreach (var it in consolidados)
        {
            if (it.Cantidad <= 0) return BadRequest(new { error = "Las cantidades deben ser mayores a 0" });
            var equipo = await _db.AlqEquipos.FindAsync(it.EquipoId);
            if (equipo is null) return BadRequest(new { error = $"Equipo {it.EquipoId} no encontrado" });

            var comprometido = await CalcularComprometidoAsync(it.EquipoId, req.FechaEntrega, req.FechaRetiro, excluirReservaId: null);
            var disponible = equipo.StockTotal - comprometido;
            if (it.Cantidad > disponible)
            {
                return BadRequest(new
                {
                    error = $"No hay stock suficiente de '{equipo.Nombre}' ({equipo.Sku}) para esas fechas. " +
                            $"Stock total: {equipo.StockTotal}, ya reservado en ese rango: {comprometido}, disponible: {disponible}, pediste: {it.Cantidad}."
                });
            }
        }

        // Estado y subtotal
        var estado = NormalizarEstado(req.Estado) ?? "reservado";
        var subtotal = consolidados.Sum(i => i.Cantidad * i.PrecioUnitario);
        var total = Math.Max(0m, subtotal - Math.Max(0m, req.Descuento));

        var reserva = new AlqReserva
        {
            Numero = await GenerarNumeroAsync(),
            ClienteId = req.ClienteId,
            FechaEntrega = req.FechaEntrega.Date,
            FechaRetiro = req.FechaRetiro.Date,
            HoraInicio = NormHora(req.HoraInicio),
            HoraFin = NormHora(req.HoraFin),
            DireccionEvento = string.IsNullOrWhiteSpace(req.DireccionEvento) ? null : req.DireccionEvento.Trim(),
            Descuento = Math.Max(0m, req.Descuento),
            Sena = Math.Max(0m, req.Sena),
            MontoTotal = total,
            Estado = estado,
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            CreatedAt = DateTime.UtcNow,
            Items = consolidados.Select(i => new AlqReservaItem
            {
                EquipoId = i.EquipoId,
                Cantidad = i.Cantidad,
                PrecioUnitario = i.PrecioUnitario
            }).ToList()
        };
        _db.AlqReservas.Add(reserva);
        await _db.SaveChangesAsync();

        // Recargo con includes para devolver el DTO completo
        var saved = await _db.AlqReservas
            .Include(r => r.ClienteNav)
            .Include(r => r.Items).ThenInclude(i => i.EquipoNav)
            .FirstAsync(r => r.Id == reserva.Id);
        return Ok(Map(saved));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateAlqReservaRequest req)
    {
        var reserva = await _db.AlqReservas
            .Include(r => r.Items)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (reserva is null) return NotFound(new { error = "Reserva no encontrada" });

        if (req.ClienteId.HasValue) reserva.ClienteId = req.ClienteId.Value;
        if (req.FechaEntrega.HasValue) reserva.FechaEntrega = req.FechaEntrega.Value.Date;
        if (req.FechaRetiro.HasValue) reserva.FechaRetiro = req.FechaRetiro.Value.Date;
        if (reserva.FechaRetiro < reserva.FechaEntrega)
            return BadRequest(new { error = "La fecha de retiro no puede ser anterior a la de entrega" });
        if (req.HoraInicio is not null) reserva.HoraInicio = NormHora(req.HoraInicio);
        if (req.HoraFin is not null) reserva.HoraFin = NormHora(req.HoraFin);
        if (req.DireccionEvento is not null) reserva.DireccionEvento = string.IsNullOrWhiteSpace(req.DireccionEvento) ? null : req.DireccionEvento.Trim();
        if (req.Descuento.HasValue) reserva.Descuento = Math.Max(0m, req.Descuento.Value);
        if (req.Sena.HasValue) reserva.Sena = Math.Max(0m, req.Sena.Value);
        if (req.Notas is not null) reserva.Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim();
        if (req.Estado is not null)
        {
            var ne = NormalizarEstado(req.Estado);
            if (ne is null) return BadRequest(new { error = $"Estado invalido. Validos: {string.Join(", ", EstadosValidos)}" });
            reserva.Estado = ne;
        }

        // Si vienen items, reemplazar todos. Validar disponibilidad excluyendo esta reserva.
        if (req.Items is not null)
        {
            if (req.Items.Count == 0) return BadRequest(new { error = "La reserva debe tener al menos un equipo" });

            var consolidados = req.Items
                .GroupBy(i => i.EquipoId)
                .Select(g => new { EquipoId = g.Key, Cantidad = g.Sum(x => x.Cantidad), PrecioUnitario = g.First().PrecioUnitario })
                .ToList();

            foreach (var it in consolidados)
            {
                if (it.Cantidad <= 0) return BadRequest(new { error = "Las cantidades deben ser mayores a 0" });
                var equipo = await _db.AlqEquipos.FindAsync(it.EquipoId);
                if (equipo is null) return BadRequest(new { error = $"Equipo {it.EquipoId} no encontrado" });

                var comprometido = await CalcularComprometidoAsync(it.EquipoId, reserva.FechaEntrega, reserva.FechaRetiro, excluirReservaId: reserva.Id);
                var disponible = equipo.StockTotal - comprometido;
                if (it.Cantidad > disponible)
                {
                    return BadRequest(new
                    {
                        error = $"No hay stock suficiente de '{equipo.Nombre}' ({equipo.Sku}) para esas fechas. " +
                                $"Disponible: {disponible}, pediste: {it.Cantidad}."
                    });
                }
            }

            _db.AlqReservaItems.RemoveRange(reserva.Items);
            reserva.Items = consolidados.Select(i => new AlqReservaItem
            {
                EquipoId = i.EquipoId,
                Cantidad = i.Cantidad,
                PrecioUnitario = i.PrecioUnitario
            }).ToList();
            var subtotal = consolidados.Sum(i => i.Cantidad * i.PrecioUnitario);
            reserva.MontoTotal = Math.Max(0m, subtotal - reserva.Descuento);
        }
        else
        {
            // Si solo cambio el descuento, recalcular total
            var subtotal = reserva.Items.Sum(i => i.Cantidad * i.PrecioUnitario);
            reserva.MontoTotal = Math.Max(0m, subtotal - reserva.Descuento);
        }

        reserva.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var saved = await _db.AlqReservas
            .Include(r => r.ClienteNav)
            .Include(r => r.Items).ThenInclude(i => i.EquipoNav)
            .FirstAsync(r => r.Id == reserva.Id);
        return Ok(Map(saved));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await _db.AlqReservas.FindAsync(id);
        if (r is null) return NotFound(new { error = "Reserva no encontrada" });
        _db.AlqReservas.Remove(r);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    /// <summary>Disponibilidad de TODOS los equipos para un rango de fechas.</summary>
    [HttpGet("disponibilidad")]
    public async Task<IActionResult> Disponibilidad(
        [FromQuery] DateTime fechaEntrega,
        [FromQuery] DateTime fechaRetiro,
        [FromQuery] int? excluirReservaId = null)
    {
        if (fechaRetiro < fechaEntrega)
            return BadRequest(new { error = "La fecha de retiro no puede ser anterior a la de entrega" });

        var equipos = await _db.AlqEquipos.Where(e => e.IsActive).OrderBy(e => e.Nombre).ToListAsync();
        var result = new List<AlqDisponibilidadDto>();
        foreach (var eq in equipos)
        {
            var comp = await CalcularComprometidoAsync(eq.Id, fechaEntrega, fechaRetiro, excluirReservaId);
            result.Add(new AlqDisponibilidadDto(eq.Id, eq.Sku, eq.Nombre, eq.StockTotal, comp, eq.StockTotal - comp));
        }
        return Ok(result);
    }

    // --- helpers ---

    /// <summary>
    /// Cantidad comprometida de un equipo en cualquier reserva que se solape con [from, to].
    /// Solo cuentan reservas con estado != cancelado y != finalizado (las finalizadas ya volvieron).
    /// </summary>
    private async Task<int> CalcularComprometidoAsync(int equipoId, DateTime from, DateTime to, int? excluirReservaId)
    {
        var f = from.Date; var t = to.Date;
        var q = _db.AlqReservaItems
            .Where(i => i.EquipoId == equipoId
                && i.ReservaNav!.Estado != "cancelado"
                && i.ReservaNav!.Estado != "finalizado"
                && i.ReservaNav!.FechaEntrega <= t
                && i.ReservaNav!.FechaRetiro >= f);
        if (excluirReservaId.HasValue) q = q.Where(i => i.ReservaId != excluirReservaId.Value);
        return await q.SumAsync(i => (int?)i.Cantidad) ?? 0;
    }

    private async Task<string> GenerarNumeroAsync()
    {
        var year = DateTime.UtcNow.Year;
        var prefix = $"RES-{year}-";
        var existing = await _db.AlqReservas
            .Where(r => r.Numero.StartsWith(prefix))
            .Select(r => r.Numero)
            .ToListAsync();
        int max = 0;
        foreach (var s in existing)
        {
            if (int.TryParse(s.Substring(prefix.Length), out var n) && n > max) max = n;
        }
        return $"{prefix}{(max + 1):D4}";
    }

    private static string? NormalizarEstado(string? estado)
    {
        if (string.IsNullOrWhiteSpace(estado)) return null;
        var e = estado.Trim().ToLowerInvariant();
        return EstadosValidos.Contains(e) ? e : null;
    }

    private static string? NormHora(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var v = s.Trim();
        return v.Length > 8 ? v.Substring(0, 8) : v;
    }

    private static AlqReservaDto Map(AlqReserva r) => new(
        r.Id, r.Numero,
        r.ClienteId, r.ClienteNav?.Nombre ?? "—", r.ClienteNav?.Telefono,
        r.FechaEntrega, r.FechaRetiro, r.HoraInicio, r.HoraFin,
        r.DireccionEvento,
        r.MontoTotal, r.Descuento, r.Sena,
        r.Estado, r.Notas,
        r.CreatedAt, r.UpdatedAt,
        r.Items.Select(i => new AlqReservaItemDto(
            i.Id, i.EquipoId, i.EquipoNav?.Sku ?? "—", i.EquipoNav?.Nombre ?? "—",
            i.Cantidad, i.PrecioUnitario)).ToList());
}
