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
        r.Items.Select(i => new ItemDto(i.Cantidad, i.EquipoId.HasValue ? (i.EquipoNav?.Sku ?? "—") : "", i.EquipoId.HasValue ? (i.EquipoNav?.Nombre ?? "—") : (i.Descripcion ?? "—"))).ToList());

    public record AccionRequest(
        int RepartidorId, string Pin,
        string Momento,        // "entrega" | "retiro"
        bool MarcarHecho,      // entregue / retire los equipos
        decimal? Importe,      // efectivo cobrado (opcional)
        string? Notas);

    public record AccionResult(bool Ok, string Mensaje, decimal Saldo);

    /// <summary>Flujo "QR frío": alguien escanea el QR del comprobante (solo tiene el token de la
    /// reserva), entonces se identifica con nombre + PIN. El cobro queda PENDIENTE de aprobacion;
    /// la entrega/retiro se aplica directo a la reserva.</summary>
    [HttpPost("accion/{token}")]
    public async Task<IActionResult> Accion(string token, [FromBody] AccionRequest req)
    {
        var rep = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.Id == req.RepartidorId && x.IsActive);
        if (rep is null) return BadRequest(new { error = "Repartidor no encontrado" });
        if (string.IsNullOrEmpty(rep.DniUltimos3))
            return BadRequest(new { error = "Este repartidor no tiene PIN configurado. Avisale al admin." });
        if ((req.Pin ?? "").Trim() != rep.DniUltimos3)
            return Unauthorized(new { error = "PIN incorrecto" });

        return await EjecutarAccion(token, rep, req.Momento, req.MarcarHecho, req.Importe, req.Notas);
    }

    public record AccionPanelRequest(string Momento, bool MarcarHecho, decimal? Importe, string? Notas);

    /// <summary>Flujo "desde el panel": el repartidor ya entró con SU link propio (token fijo del
    /// repartidor), asi que NO se le pide ni nombre ni PIN — el token lo identifica. Mismo modelo
    /// que la pantalla de ventas. Pedido 2026-06-26.</summary>
    [HttpPost("mis-reservas/{tokenRepartidor}/accion/{reservaId:int}")]
    public async Task<IActionResult> AccionPanel(string tokenRepartidor, int reservaId, [FromBody] AccionPanelRequest req)
    {
        var rep = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (rep is null) return Unauthorized(new { error = "Enlace invalido o repartidor inactivo" });

        var r = await _db.AlqReservas.FirstOrDefaultAsync(x => x.Id == reservaId);
        if (r is null) return NotFound(new { error = "Reserva no encontrada" });

        return await EjecutarAccion(r.PublicToken ?? "", rep, req.Momento, req.MarcarHecho, req.Importe, req.Notas);
    }

    /// <summary>Logica compartida de entrega/retiro/cobro. La identidad del repartidor ya viene resuelta.</summary>
    private async Task<IActionResult> EjecutarAccion(string token, CafeRepartidor rep, string? momentoRaw, bool marcarHecho, decimal? importeRaw, string? notas)
    {
        var r = await _db.AlqReservas.Include(x => x.ClienteNav).FirstOrDefaultAsync(x => x.PublicToken == token);
        if (r is null) return NotFound(new { error = "Reserva no encontrada (QR invalido)" });

        var momento = (momentoRaw ?? "entrega").Trim().ToLowerInvariant();
        if (momento != "entrega" && momento != "retiro") momento = "entrega";
        var importe = importeRaw.HasValue ? Math.Max(0m, importeRaw.Value) : 0m;
        if (!marcarHecho && importe <= 0m)
            return BadRequest(new { error = "No marcaste nada: tildá entrega/retiro o ingresá un importe cobrado." });

        var ip = Request.HttpContext.Connection.RemoteIpAddress?.ToString();
        var now = DateTime.UtcNow;

        // Siempre dejar registrado el "cargado" para que la reserva aparezca en el panel del repartidor.
        _db.AlqQrEscaneos.Add(new AlqQrEscaneo { ReservaId = r.Id, RepartidorId = rep.Id, Accion = "cargado", CreatedAt = now, Ip = ip });

        var mensajes = new List<string>();

        if (marcarHecho)
        {
            if (momento == "entrega")
            {
                r.EntregadoPorRepartidorId = rep.Id;
                r.EntregadoAt = now;
                if (!string.IsNullOrWhiteSpace(notas)) r.ComentarioEntrega = notas.Trim();
                if (r.Estado == "reservado" || r.Estado == "confirmado") r.Estado = "entregado";
                _db.AlqQrEscaneos.Add(new AlqQrEscaneo { ReservaId = r.Id, RepartidorId = rep.Id, Accion = "entregado", CreatedAt = now, Ip = ip });
                mensajes.Add("Entrega registrada");
            }
            else
            {
                r.RetiradoPorRepartidorId = rep.Id;
                r.RetiradoAt = now;
                if (!string.IsNullOrWhiteSpace(notas)) r.ComentarioRetiro = notas.Trim();
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
                MarcadoEntregado = marcarHecho && momento == "entrega",
                MarcadoRetirado = marcarHecho && momento == "retiro",
                Notas = string.IsNullOrWhiteSpace(notas) ? null : notas.Trim(),
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
        bool Entregado, bool Retirado, DateTime CargadoAt,
        DateTime? EntregadoAt, DateTime? RetiradoAt,
        List<ItemDto> Items,
        // true si está entregado pero falta retirar Y ya toca (fecha de retiro llegó o se re-escaneó)
        bool ParaRetiro = false,
        // Link de Maps efectivo: el de la reserva o, si no tiene, el del cliente. 2026-07-02
        string? MapeoLink = null);

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
            .Include(x => x.Items).ThenInclude(i => i.EquipoNav)
            .Where(x => mias.Contains(x.Id))
            .ToListAsync();

        var hoyAr = DateTime.UtcNow.AddHours(-3).Date;
        var list = reservas
            .Select(x =>
            {
                var cargadoAt = duenioActual.TryGetValue(x.Id, out var d) ? d.CreatedAt : x.CreatedAt;
                var entregado = x.EntregadoPorRepartidorId.HasValue;
                var retirado = x.RetiradoPorRepartidorId.HasValue;
                // "Para retirar" = entregado, falta retiro, y ya toca: o llegó la fecha de retiro,
                // o el repartidor re-escaneó el QR después de haber entregado.
                var paraRetiro = entregado && !retirado &&
                    (x.FechaRetiro.Date <= hoyAr || (x.EntregadoAt.HasValue && cargadoAt > x.EntregadoAt.Value));
                return new MisReservaDto(
                    x.Id, x.Numero, x.PublicToken ?? "", x.Estado,
                    x.ClienteNav?.Nombre ?? "—", x.ClienteNav?.Telefono, x.DireccionEvento,
                    x.FechaEntrega, x.FechaRetiro,
                    x.MontoTotal, Math.Max(0m, x.MontoTotal - x.Sena - x.MontoCobrado),
                    entregado, retirado, cargadoAt,
                    x.EntregadoAt, x.RetiradoAt,
                    x.Items.Select(i => new ItemDto(i.Cantidad, i.EquipoId.HasValue ? (i.EquipoNav?.Sku ?? "—") : "", i.EquipoId.HasValue ? (i.EquipoNav?.Nombre ?? "—") : (i.Descripcion ?? "—"))).ToList(),
                    paraRetiro,
                    string.IsNullOrWhiteSpace(x.MapeoLink) ? x.ClienteNav?.MapeoLink : x.MapeoLink);
            })
            .OrderBy(x => x.Entregado && x.Retirado)        // pendientes arriba
            .ThenBy(x => x.FechaEntrega)
            .ToList();

        // Cobros de alquiler del repartidor (para el arqueo "tenés que rendir"). Mismo criterio
        // que ventas: PENDIENTE suma (todavía no rindió); APROBADA ya rindió; RECHAZADA no cuenta.
        var cobros = await _db.AlqCobranzasPendientes
            .Where(p => p.RepartidorId == rep.Id && p.Estado != "RECHAZADA" && p.CreatedAt >= desde)
            .Select(p => new MisCobroAlqDto(p.ReservaId, p.Importe, p.Estado, p.CreatedAt))
            .ToListAsync();

        return Ok(new { repartidorId = rep.Id, nombre = rep.Nombre, reservas = list, cobros });
    }

    public record MisCobroAlqDto(int ReservaId, decimal Importe, string Estado, DateTime FechaCobro);

    public record EscanearAlqResult(bool Ok, string Mensaje, int? ReservaId, string? Numero);

    /// <summary>El repartidor escanea un QR de alquiler desde su panel (lector continuo, igual que
    /// ventas). Agrega la reserva a su lista (escaneo 'cargado', "el último que escanea se la queda").
    /// Identidad por token del repartidor, sin PIN. Pedido 2026-06-26.</summary>
    [HttpPost("mis-reservas/{tokenRepartidor}/escanear/{reservaToken}")]
    public async Task<IActionResult> EscanearAlq(string tokenRepartidor, string reservaToken)
    {
        var rep = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (rep is null) return Unauthorized(new EscanearAlqResult(false, "Necesitás entrar con tu link", null, null));

        var r = await _db.AlqReservas.FirstOrDefaultAsync(x => x.PublicToken == reservaToken);
        if (r is null) return Ok(new EscanearAlqResult(false, "Alquiler no encontrado (QR inválido)", null, null));

        // Dueño actual = último 'cargado'. Si ya es mío, no duplico.
        var ultimo = await _db.AlqQrEscaneos
            .Where(e => e.ReservaId == r.Id && e.Accion == "cargado")
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync();
        var yaEsMia = ultimo is not null && ultimo.RepartidorId == rep.Id;

        string? transferidoDe = null;
        if (!yaEsMia)
        {
            if (ultimo is not null && !r.RetiradoPorRepartidorId.HasValue)
            {
                var otro = await _db.CafeRepartidores.FindAsync(ultimo.RepartidorId);
                transferidoDe = otro?.Nombre;
            }
            _db.AlqQrEscaneos.Add(new AlqQrEscaneo
            {
                ReservaId = r.Id, RepartidorId = rep.Id, Accion = "cargado",
                CreatedAt = DateTime.UtcNow,
                Ip = Request.HttpContext.Connection.RemoteIpAddress?.ToString()
            });
            await _db.SaveChangesAsync();
        }

        var msg = yaEsMia
            ? $"Ya estaba en tu lista: {r.Numero}"
            : transferidoDe is not null
                ? $"⚠ Lo tenía {transferidoDe} — ahora es tuyo: {r.Numero}"
                : $"✅ Alquiler cargado: {r.Numero}";
        return Ok(new EscanearAlqResult(true, msg, r.Id, r.Numero));
    }
}
