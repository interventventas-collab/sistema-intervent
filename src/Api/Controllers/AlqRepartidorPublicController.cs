using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Endpoints publicos (sin login del sistema) para que el repartidor opere una reserva de
/// alquiler desde el celu al escanear el QR del comprobante (/alquiler/{token}).
/// Reusa los MISMOS repartidores y PIN que ventas (Cafe_Repartidores). El PIN se valida
/// en cada accion. Pedido 2026-06-26 — espejo del flujo de ventas.
///
/// - Entrega / Retiro de los equipos: se marca directo en la reserva (no necesita aprobacion).
/// - Cobro: queda como AlqCobranzaPendiente PENDIENTE hasta que el admin la apruebe.
/// </summary>
[ApiController]
[Route("api/alquileres/repartidor-public")]
[AllowAnonymous]
public class AlqRepartidorPublicController : ControllerBase
{
    private readonly AppDbContext _db;
    public AlqRepartidorPublicController(AppDbContext db) { _db = db; }

    public record ItemDto(int Cantidad, string Sku, string Nombre);
    public record InfoReservaDto(
        int Id, string Numero, string Estado,
        string ClienteNombre, string? ClienteTelefono, string? Direccion,
        DateTime FechaEntrega, DateTime FechaRetiro, string? HoraInicio, string? HoraFin,
        decimal MontoTotal, decimal Sena, decimal MontoCobrado, decimal Saldo,
        bool Entregado, string? EntregadoPorNombre, DateTime? EntregadoAt, string? ComentarioEntrega,
        bool Retirado, string? RetiradoPorNombre, DateTime? RetiradoAt, string? ComentarioRetiro,
        List<ItemDto> Items);

    /// <summary>Datos de la reserva (publico, lo protege el token). Sin PIN.</summary>
    [HttpGet("reserva/{token}")]
    public async Task<IActionResult> GetReserva(string token)
    {
        var r = await _db.AlqReservas
            .Include(x => x.ClienteNav)
            .Include(x => x.EntregadoPorRepartidor)
            .Include(x => x.RetiradoPorRepartidor)
            .Include(x => x.Items).ThenInclude(i => i.EquipoNav)
            .FirstOrDefaultAsync(x => x.PublicToken == token);
        if (r is null) return NotFound(new { error = "Reserva no encontrada (QR invalido)" });
        return Ok(ToInfo(r));
    }

    private static InfoReservaDto ToInfo(AlqReserva r) => new(
        r.Id, r.Numero, r.Estado,
        r.ClienteNav?.Nombre ?? "—", r.ClienteNav?.Telefono, r.DireccionEvento,
        r.FechaEntrega, r.FechaRetiro, r.HoraInicio, r.HoraFin,
        r.MontoTotal, r.Sena, r.MontoCobrado,
        Math.Max(0m, r.MontoTotal - r.Sena - r.MontoCobrado),
        r.EntregadoPorRepartidorId.HasValue, r.EntregadoPorRepartidor?.Nombre, r.EntregadoAt, r.ComentarioEntrega,
        r.RetiradoPorRepartidorId.HasValue, r.RetiradoPorRepartidor?.Nombre, r.RetiradoAt, r.ComentarioRetiro,
        r.Items.Select(i => new ItemDto(i.Cantidad, i.EquipoNav?.Sku ?? "—", i.EquipoNav?.Nombre ?? "—")).ToList());

    public record AccionRequest(
        int RepartidorId, string Pin,
        string Momento,        // "entrega" | "retiro"
        bool MarcarHecho,      // entregue / retire los equipos
        decimal? Importe,      // efectivo cobrado (opcional)
        string? Notas);

    public record AccionResult(bool Ok, string Mensaje, decimal Saldo);

    /// <summary>El repartidor marca entrega/retiro y/o cobra. Valida PIN. El cobro queda
    /// PENDIENTE de aprobacion del admin; la entrega/retiro se aplica directo a la reserva.</summary>
    [HttpPost("accion/{token}")]
    public async Task<IActionResult> Accion(string token, [FromBody] AccionRequest req)
    {
        var rep = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.Id == req.RepartidorId && x.IsActive);
        if (rep is null) return BadRequest(new { error = "Repartidor no encontrado" });
        if (string.IsNullOrEmpty(rep.DniUltimos3))
            return BadRequest(new { error = "Este repartidor no tiene PIN configurado. Avisale al admin." });
        if ((req.Pin ?? "").Trim() != rep.DniUltimos3)
            return Unauthorized(new { error = "PIN incorrecto" });

        var r = await _db.AlqReservas.Include(x => x.ClienteNav).FirstOrDefaultAsync(x => x.PublicToken == token);
        if (r is null) return NotFound(new { error = "Reserva no encontrada (QR invalido)" });

        var momento = (req.Momento ?? "entrega").Trim().ToLowerInvariant();
        if (momento != "entrega" && momento != "retiro") momento = "entrega";
        var importe = req.Importe.HasValue ? Math.Max(0m, req.Importe.Value) : 0m;
        if (!req.MarcarHecho && importe <= 0m)
            return BadRequest(new { error = "No marcaste nada: tildá entrega/retiro o ingresá un importe cobrado." });

        var ip = Request.HttpContext.Connection.RemoteIpAddress?.ToString();
        var now = DateTime.UtcNow;

        // Siempre dejar registrado el "cargado" para que la reserva aparezca en el panel del repartidor.
        _db.AlqQrEscaneos.Add(new AlqQrEscaneo { ReservaId = r.Id, RepartidorId = rep.Id, Accion = "cargado", CreatedAt = now, Ip = ip });

        var mensajes = new List<string>();

        if (req.MarcarHecho)
        {
            if (momento == "entrega")
            {
                r.EntregadoPorRepartidorId = rep.Id;
                r.EntregadoAt = now;
                if (!string.IsNullOrWhiteSpace(req.Notas)) r.ComentarioEntrega = req.Notas.Trim();
                if (r.Estado == "reservado" || r.Estado == "confirmado") r.Estado = "entregado";
                _db.AlqQrEscaneos.Add(new AlqQrEscaneo { ReservaId = r.Id, RepartidorId = rep.Id, Accion = "entregado", CreatedAt = now, Ip = ip });
                mensajes.Add("Entrega registrada");
            }
            else
            {
                r.RetiradoPorRepartidorId = rep.Id;
                r.RetiradoAt = now;
                if (!string.IsNullOrWhiteSpace(req.Notas)) r.ComentarioRetiro = req.Notas.Trim();
                if (r.Estado == "entregado" || r.Estado == "confirmado" || r.Estado == "reservado") r.Estado = "finalizado";
                _db.AlqQrEscaneos.Add(new AlqQrEscaneo { ReservaId = r.Id, RepartidorId = rep.Id, Accion = "retirado", CreatedAt = now, Ip = ip });
                mensajes.Add("Retiro registrado");
            }
            r.UpdatedAt = now;
        }

        if (importe > 0m)
        {
            _db.AlqCobranzasPendientes.Add(new AlqCobranzaPendiente
            {
                ReservaId = r.Id,
                RepartidorId = rep.Id,
                Importe = importe,
                Tipo = momento,
                MarcadoEntregado = req.MarcarHecho && momento == "entrega",
                MarcadoRetirado = req.MarcarHecho && momento == "retiro",
                Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
                Estado = "PENDIENTE",
                CreatedAt = now
            });
            _db.AlqQrEscaneos.Add(new AlqQrEscaneo { ReservaId = r.Id, RepartidorId = rep.Id, Accion = "cobrado", CreatedAt = now, Ip = ip });
            mensajes.Add($"Cobro de ${importe:N0} precargado — el admin lo va a aprobar");
        }

        await _db.SaveChangesAsync();
        var saldo = Math.Max(0m, r.MontoTotal - r.Sena - r.MontoCobrado);
        return Ok(new AccionResult(true, string.Join(". ", mensajes) + ".", saldo));
    }

    // ===== Panel "Mis pedidos" (alquileres del repartidor) =====
    public record MisReservaDto(
        int Id, string Numero, string Token, string Estado,
        string ClienteNombre, string? ClienteTelefono, string? Direccion,
        DateTime FechaEntrega, DateTime FechaRetiro,
        decimal MontoTotal, decimal Saldo,
        bool Entregado, bool Retirado, DateTime CargadoAt);

    /// <summary>Lista de reservas de alquiler asignadas al repartidor (enlace fijo por su token publico).
    /// Asignacion estilo ventas: dueño = repartidor del ultimo escaneo 'cargado'. Sin PIN para mirar.</summary>
    [HttpGet("mis-reservas/{tokenRepartidor}")]
    public async Task<IActionResult> MisReservas(string tokenRepartidor, [FromQuery] int dias = 30)
    {
        var rep = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (rep is null) return NotFound(new { error = "Enlace invalido o repartidor inactivo" });

        var desde = DateTime.UtcNow.AddDays(-Math.Max(1, dias));
        var reservaIds = await _db.AlqQrEscaneos
            .Where(e => e.RepartidorId == rep.Id && e.Accion == "cargado" && e.CreatedAt >= desde)
            .Select(e => e.ReservaId).Distinct().ToListAsync();
        if (reservaIds.Count == 0)
            return Ok(new { repartidorId = rep.Id, nombre = rep.Nombre, reservas = new List<MisReservaDto>() });

        // Dueño actual de cada reserva = repartidor del ultimo 'cargado'
        var duenios = await _db.AlqQrEscaneos
            .Where(e => reservaIds.Contains(e.ReservaId) && e.Accion == "cargado")
            .Select(e => new { e.ReservaId, e.RepartidorId, e.CreatedAt, e.Id })
            .ToListAsync();
        var duenioActual = duenios
            .GroupBy(e => e.ReservaId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).First());

        var mias = reservaIds.Where(id => duenioActual.TryGetValue(id, out var d) && d.RepartidorId == rep.Id).ToList();

        var reservas = await _db.AlqReservas
            .Include(x => x.ClienteNav)
            .Where(x => mias.Contains(x.Id))
            .ToListAsync();

        var list = reservas
            .Select(x => new MisReservaDto(
                x.Id, x.Numero, x.PublicToken ?? "", x.Estado,
                x.ClienteNav?.Nombre ?? "—", x.ClienteNav?.Telefono, x.DireccionEvento,
                x.FechaEntrega, x.FechaRetiro,
                x.MontoTotal, Math.Max(0m, x.MontoTotal - x.Sena - x.MontoCobrado),
                x.EntregadoPorRepartidorId.HasValue, x.RetiradoPorRepartidorId.HasValue,
                duenioActual.TryGetValue(x.Id, out var d) ? d.CreatedAt : x.CreatedAt))
            .OrderBy(x => x.Entregado && x.Retirado)        // pendientes arriba
            .ThenBy(x => x.FechaEntrega)
            .ToList();

        return Ok(new { repartidorId = rep.Id, nombre = rep.Nombre, reservas = list });
    }
}
