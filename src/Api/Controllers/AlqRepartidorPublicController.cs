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

        // ENTREGA y RETIRO son dos etapas con dueño propio (2026-07-08):
        //   - dueño de entrega = ultimo escaneo 'cargado'
        //   - dueño de retiro  = ultimo escaneo 'cargado_retiro'
        // Este repartidor ve una reserva si: le toca entregarla, le toca retirarla, o él mismo la
        // entregó/retiró (para que le quede en su historial de "Entregadas", igual que en ventas).
        var idsScan = await _db.AlqQrEscaneos
            .Where(e => e.RepartidorId == rep.Id
                        && (e.Accion == "cargado" || e.Accion == "cargado_retiro")
                        && e.CreatedAt >= desde)
            .Select(e => e.ReservaId).Distinct().ToListAsync();
        var idsHechos = await _db.AlqReservas
            .Where(x => (x.EntregadoPorRepartidorId == rep.Id && x.EntregadoAt >= desde)
                     || (x.RetiradoPorRepartidorId == rep.Id && x.RetiradoAt >= desde))
            .Select(x => x.Id).ToListAsync();
        var candidatos = idsScan.Union(idsHechos).Distinct().ToList();
        if (candidatos.Count == 0)
            return Ok(new { repartidorId = rep.Id, nombre = rep.Nombre, reservas = new List<MisReservaDto>() });

        // Todos los escaneos de asignacion de esas reservas (para saber el dueño actual de cada etapa).
        var escaneos = await _db.AlqQrEscaneos
            .Where(e => candidatos.Contains(e.ReservaId) && (e.Accion == "cargado" || e.Accion == "cargado_retiro"))
            .Select(e => new { e.ReservaId, e.RepartidorId, e.Accion, e.CreatedAt, e.Id })
            .ToListAsync();
        int? DuenoDe(int reservaId, string accion) => escaneos
            .Where(e => e.ReservaId == reservaId && e.Accion == accion)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Select(e => (int?)e.RepartidorId).FirstOrDefault();
        DateTime? UltimoCargadoRetiro(int reservaId) => escaneos
            .Where(e => e.ReservaId == reservaId && e.Accion == "cargado_retiro")
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Select(e => (DateTime?)e.CreatedAt).FirstOrDefault();

        var reservas = await _db.AlqReservas
            .Include(x => x.ClienteNav)
            .Include(x => x.Items).ThenInclude(i => i.EquipoNav)
            .Where(x => candidatos.Contains(x.Id))
            .ToListAsync();

        var list = new List<MisReservaDto>();
        foreach (var x in reservas)
        {
            var entregado = x.EntregadoPorRepartidorId.HasValue;
            var retirado = x.RetiradoPorRepartidorId.HasValue;
            var duenoEntrega = DuenoDe(x.Id, "cargado");
            var duenoRetiro = DuenoDe(x.Id, "cargado_retiro");
            var hizoEntrega = x.EntregadoPorRepartidorId == rep.Id;
            var hizoRetiro = x.RetiradoPorRepartidorId == rep.Id;

            // ¿Qué le toca a ESTE repartidor con esta reserva?
            var pendienteEntrega = !entregado && duenoEntrega == rep.Id;   // aún no entregada y es suya
            var pendienteRetiro = entregado && !retirado && duenoRetiro == rep.Id;  // ya entregada, le toca retirar
            // Se la mostramos si le toca hacer algo, o si él la trabajó (queda en su historial).
            if (!(pendienteEntrega || pendienteRetiro || hizoEntrega || hizoRetiro)) continue;

            // "Para retirar" SOLO si a él le toca el retiro (no al que entregó: ese ya se desvinculó).
            var paraRetiro = pendienteRetiro;
            // En SU vista, EntregadoAt/RetiradoAt solo si lo hizo él → así "Entregadas" muestra lo suyo
            // y no lo que hizo otro repartidor sobre la misma reserva.
            var entregadoAtMio = hizoEntrega ? x.EntregadoAt : (DateTime?)null;
            var retiradoAtMio = hizoRetiro ? x.RetiradoAt : (DateTime?)null;
            var cargadoAt = UltimoCargadoRetiro(x.Id) ?? x.CreatedAt;

            list.Add(new MisReservaDto(
                x.Id, x.Numero, x.PublicToken ?? "", x.Estado,
                x.ClienteNav?.Nombre ?? "—", x.ClienteNav?.Telefono, x.DireccionEvento,
                x.FechaEntrega, x.FechaRetiro,
                x.MontoTotal, Math.Max(0m, x.MontoTotal - x.Sena - x.MontoCobrado),
                entregado, retirado, cargadoAt,
                entregadoAtMio, retiradoAtMio,
                x.Items.Select(i => new ItemDto(i.Cantidad, i.EquipoId.HasValue ? (i.EquipoNav?.Sku ?? "—") : "", i.EquipoId.HasValue ? (i.EquipoNav?.Nombre ?? "—") : (i.Descripcion ?? "—"))).ToList(),
                paraRetiro,
                string.IsNullOrWhiteSpace(x.MapeoLink) ? x.ClienteNav?.MapeoLink : x.MapeoLink));
        }
        list = list
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
