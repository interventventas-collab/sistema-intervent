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
    private readonly Api.Services.QrRepartidorService _qr;
    private readonly Api.Services.AlqReservaPdfService _pdf;
    private readonly Api.Services.ArcaInvoiceService _arca;
    private static readonly string[] EstadosValidos = { "reservado", "confirmado", "entregado", "finalizado", "cancelado" };

    public AlqReservasController(AppDbContext db, Api.Services.QrRepartidorService qr, Api.Services.AlqReservaPdfService pdf, Api.Services.ArcaInvoiceService arca) { _db = db; _qr = qr; _pdf = pdf; _arca = arca; }

    /// <summary>PDF descargable del comprobante de la reserva (como el de ventas). Pedido 2026-06-29.</summary>
    [HttpGet("{id:int}/pdf")]
    public async Task<IActionResult> Pdf(int id)
    {
        var r = await _db.AlqReservas
            .Include(x => x.ClienteNav)
            .Include(x => x.Items).ThenInclude(i => i.EquipoNav)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return NotFound(new { error = "Reserva no encontrada" });
        if (string.IsNullOrEmpty(r.PublicToken))
        {
            r.PublicToken = Guid.NewGuid().ToString("N");
            r.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        var cfg = await _db.CafeSettings.FindAsync(1);
        var qr = await _qr.GenerarQrAlquilerAsync(r.PublicToken);
        var condiciones = (await _db.AppSettings.FindAsync("alq.condiciones"))?.Value;
        var bytes = _pdf.Generar(r, cfg, qr, condiciones);
        return File(bytes, "application/pdf", $"Reserva-{r.Numero}.pdf");
    }

    public record AlqProximoDto(int Id, string Numero, string ClienteNombre, DateTime Fecha, string Tipo);
    public record AlqDashboardResumen(
        int EnCalle, decimal MontoEnCalle, decimal SaldoACobrar,
        int ARetirarHoy, int Vencidos, int EntregasHoy, int RetirosHoy,
        List<AlqProximoDto> Proximos);

    /// <summary>Resumen para la card del dashboard: equipos en calle, vencidos, movimientos de hoy
    /// y próximos. Pedido 2026-06-26.</summary>
    [HttpGet("resumen-dashboard")]
    public async Task<IActionResult> ResumenDashboard()
    {
        var hoy = DateTime.Today;
        var activas = await _db.AlqReservas
            .Include(r => r.ClienteNav)
            .Where(r => r.Estado != "cancelado")
            .ToListAsync();

        var enCalleList = activas.Where(r => r.EntregadoPorRepartidorId.HasValue && !r.RetiradoPorRepartidorId.HasValue).ToList();
        var saldoACobrar = activas.Where(r => r.Estado != "finalizado")
            .Sum(r => Math.Max(0m, r.MontoTotal - r.Sena - r.MontoCobrado));

        // Movimientos programados de hoy
        var entregasHoy = activas.Count(r => r.FechaEntrega.Date == hoy && !r.EntregadoPorRepartidorId.HasValue);
        var retirosHoy = enCalleList.Count(r => r.FechaRetiro.Date == hoy);
        var aRetirarHoy = enCalleList.Count(r => r.FechaRetiro.Date == hoy);
        var vencidos = enCalleList.Count(r => r.FechaRetiro.Date < hoy);

        // Próximos: entregas pendientes y retiros en calle, ordenados por fecha
        var proximos = new List<AlqProximoDto>();
        proximos.AddRange(activas.Where(r => !r.EntregadoPorRepartidorId.HasValue && r.FechaEntrega.Date >= hoy)
            .Select(r => new AlqProximoDto(r.Id, r.Numero, r.ClienteNav?.Nombre ?? "—", r.FechaEntrega, "entrega")));
        proximos.AddRange(enCalleList.Where(r => r.FechaRetiro.Date >= hoy)
            .Select(r => new AlqProximoDto(r.Id, r.Numero, r.ClienteNav?.Nombre ?? "—", r.FechaRetiro, "retiro")));
        proximos = proximos.OrderBy(p => p.Fecha).Take(5).ToList();

        return Ok(new AlqDashboardResumen(
            enCalleList.Count, enCalleList.Sum(r => r.MontoTotal), saldoACobrar,
            aRetirarHoy, vencidos, entregasHoy, retirosHoy, proximos));
    }

    /// <summary>PNG del QR que va en el comprobante. Lleva a /alquiler/{token}. Si la reserva
    /// es vieja y no tiene token, se lo genera al vuelo.</summary>
    [HttpGet("{id:int}/qr")]
    public async Task<IActionResult> Qr(int id)
    {
        var r = await _db.AlqReservas.FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return NotFound();
        if (string.IsNullOrEmpty(r.PublicToken))
        {
            r.PublicToken = Guid.NewGuid().ToString("N");
            r.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        var png = await _qr.GenerarQrAlquilerAsync(r.PublicToken);
        if (png is null) return NotFound(new { error = "No hay URL publica configurada (mapeo.public_base_url)" });
        return File(png, "image/png");
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? estado = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null)
    {
        var q = _db.AlqReservas
            .Include(r => r.ClienteNav)
            .Include(r => r.EntregadoPorRepartidor)
            .Include(r => r.RetiradoPorRepartidor)
            .Include(r => r.Items).ThenInclude(i => i.EquipoNav)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(estado))
        {
            var e = estado.Trim().ToLowerInvariant();
            q = q.Where(r => r.Estado == e);
        }
        // Filtro de rango: trae las reservas que se solapan con [from, to]
        if (from.HasValue)
        {
            var f = from.Value.Date;
            q = q.Where(r => r.FechaRetiro >= f);
        }
        if (to.HasValue)
        {
            var t = to.Value.Date;
            q = q.Where(r => r.FechaEntrega <= t);
        }
        var list = await q.OrderByDescending(r => r.FechaEntrega).ThenByDescending(r => r.Id).ToListAsync();
        var asignados = await CalcularAsignadosAsync(list.Select(r => r.Id).ToList());
        return Ok(list.Select(r =>
        {
            asignados.TryGetValue(r.Id, out var a);
            return Map(r, a.Id == 0 ? (int?)null : a.Id, a.Nombre);
        }).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var r = await _db.AlqReservas
            .Include(r => r.ClienteNav)
            .Include(r => r.EntregadoPorRepartidor)
            .Include(r => r.RetiradoPorRepartidor)
            .Include(r => r.Items).ThenInclude(i => i.EquipoNav)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (r is null) return NotFound(new { error = "Reserva no encontrada" });
        var asign = await CalcularAsignadosAsync(new List<int> { r.Id });
        asign.TryGetValue(r.Id, out var aa);
        return Ok(Map(r, aa.Id == 0 ? (int?)null : aa.Id, aa.Nombre));
    }

    public record AsignarRepartoRequest(int? NuevoRepartidorId);

    /// <summary>Asignar la reserva a un repartidor desde el panel admin (sin que escanee el QR).
    /// Reemplaza el escaneo 'cargado' actual. Si NuevoRepartidorId es null, la deja sin asignar.
    /// Hace que la reserva aparezca en el panel "Mis pedidos" de ese repartidor. Pedido 2026-06-26.</summary>
    [HttpPost("{id:int}/asignar-reparto")]
    public async Task<IActionResult> AsignarReparto(int id, [FromBody] AsignarRepartoRequest req)
    {
        var r = await _db.AlqReservas.FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return NotFound(new { error = "Reserva no encontrada" });

        var existentes = await _db.AlqQrEscaneos
            .Where(e => e.ReservaId == id && e.Accion == "cargado").ToListAsync();
        _db.AlqQrEscaneos.RemoveRange(existentes);

        string mensaje;
        if (req.NuevoRepartidorId.HasValue)
        {
            var rep = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.Id == req.NuevoRepartidorId.Value && x.IsActive);
            if (rep is null) return NotFound(new { error = "Repartidor no encontrado" });
            _db.AlqQrEscaneos.Add(new AlqQrEscaneo
            {
                ReservaId = id,
                RepartidorId = rep.Id,
                Accion = "cargado",
                CreatedAt = DateTime.UtcNow,
                Ip = "admin-asignar"
            });
            mensaje = $"Reserva asignada a {rep.Nombre}";
        }
        else
        {
            mensaje = "Reserva sin asignar (que nadie la tenga)";
        }

        await _db.SaveChangesAsync();
        return Ok(new { ok = true, mensaje });
    }

    public record LimpiarRepartoRequest(string Tipo);   // "entrega" | "retiro"

    /// <summary>Deshace la entrega o el retiro marcado por un repartidor (saca el "Entregó X" /
    /// "Retiró X") y reacomoda el estado de la reserva. Pedido 2026-06-26.</summary>
    [HttpPost("{id:int}/limpiar-reparto")]
    public async Task<IActionResult> LimpiarReparto(int id, [FromBody] LimpiarRepartoRequest req)
    {
        var r = await _db.AlqReservas.FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return NotFound(new { error = "Reserva no encontrada" });

        var tipo = (req.Tipo ?? "").Trim().ToLowerInvariant();
        if (tipo == "retiro")
        {
            r.RetiradoPorRepartidorId = null;
            r.RetiradoAt = null;
            r.ComentarioRetiro = null;
            if (r.Estado == "finalizado") r.Estado = "entregado";
        }
        else // "entrega" (deshace entrega; el retiro no puede existir sin entrega, así que también lo limpia)
        {
            r.EntregadoPorRepartidorId = null;
            r.EntregadoAt = null;
            r.ComentarioEntrega = null;
            r.RetiradoPorRepartidorId = null;
            r.RetiradoAt = null;
            r.ComentarioRetiro = null;
            if (r.Estado == "entregado" || r.Estado == "finalizado") r.Estado = "confirmado";
        }
        r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
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

        var cliente = await _db.CafeClientes.FindAsync(req.ClienteId);
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
        var total = req.MontoTotalManual.HasValue
            ? Math.Max(0m, req.MontoTotalManual.Value)
            : Math.Max(0m, subtotal - Math.Max(0m, req.Descuento));

        var reserva = new AlqReserva
        {
            Numero = await GenerarNumeroAsync(),
            PublicToken = Guid.NewGuid().ToString("N"),
            ClienteId = req.ClienteId,
            FechaEvento = req.FechaEvento?.Date,
            FechaEntrega = req.FechaEntrega.Date,
            FechaRetiro = req.FechaRetiro.Date,
            HoraInicio = NormHora(req.HoraInicio),
            HoraFin = NormHora(req.HoraFin),
            DireccionEvento = string.IsNullOrWhiteSpace(req.DireccionEvento) ? null : req.DireccionEvento.Trim(),
            MapeoLink = string.IsNullOrWhiteSpace(req.MapeoLink) ? null : req.MapeoLink.Trim(),
            Descuento = Math.Max(0m, req.Descuento),
            Sena = Math.Max(0m, req.Sena),
            MontoTotal = total,
            Estado = estado,
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            // ARCA (2026-07-04): configuración de facturación (la emisión es aparte, por botón).
            TipoComprobante = NormTipoComprobante(req.TipoComprobante),
            CondicionIva = NormCondIva(req.CondicionIva),
            Concepto = (req.Concepto is 1 or 2 or 3) ? req.Concepto.Value : 2,
            ArcaWebserviceAccountId = req.ArcaWebserviceAccountId is > 0 ? req.ArcaWebserviceAccountId : null,
            CreatedAt = DateTime.UtcNow,
            Items = consolidados.Select(i => new AlqReservaItem
            {
                EquipoId = i.EquipoId,
                Cantidad = i.Cantidad,
                PrecioUnitario = i.PrecioUnitario
            }).ToList()
        };
        _db.AlqReservas.Add(reserva);

        // Si pidió guardar el link tambien en la ficha del cliente (para futuras entregas)
        if (req.GuardarMapeoEnCliente && !string.IsNullOrWhiteSpace(req.MapeoLink))
        {
            cliente.MapeoLink = req.MapeoLink.Trim();
            cliente.MapeoLat = null; cliente.MapeoLng = null; // se re-resuelven del nuevo link cuando corresponda
        }

        await _db.SaveChangesAsync();

        // Recargo con includes para devolver el DTO completo
        var saved = await _db.AlqReservas
            .Include(r => r.ClienteNav)
            .Include(r => r.EntregadoPorRepartidor)
            .Include(r => r.RetiradoPorRepartidor)
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
        if (req.FechaEventoSet) reserva.FechaEvento = req.FechaEvento?.Date;
        if (reserva.FechaRetiro < reserva.FechaEntrega)
            return BadRequest(new { error = "La fecha de retiro no puede ser anterior a la de entrega" });
        if (req.HoraInicio is not null) reserva.HoraInicio = NormHora(req.HoraInicio);
        if (req.HoraFin is not null) reserva.HoraFin = NormHora(req.HoraFin);
        if (req.DireccionEvento is not null) reserva.DireccionEvento = string.IsNullOrWhiteSpace(req.DireccionEvento) ? null : req.DireccionEvento.Trim();
        if (req.MapeoLink is not null) reserva.MapeoLink = string.IsNullOrWhiteSpace(req.MapeoLink) ? null : req.MapeoLink.Trim();
        if (req.GuardarMapeoEnCliente && !string.IsNullOrWhiteSpace(req.MapeoLink))
        {
            var cli = await _db.CafeClientes.FindAsync(reserva.ClienteId);
            if (cli is not null) { cli.MapeoLink = req.MapeoLink.Trim(); cli.MapeoLat = null; cli.MapeoLng = null; }
        }
        if (req.Descuento.HasValue) reserva.Descuento = Math.Max(0m, req.Descuento.Value);
        if (req.Sena.HasValue) reserva.Sena = Math.Max(0m, req.Sena.Value);
        if (req.Notas is not null) reserva.Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim();
        // ARCA (2026-07-04): configuración de facturación. No se toca si la reserva YA fue autorizada (tiene CAE).
        if (reserva.ArcaEstado != "autorizado")
        {
            if (req.TipoComprobante is not null) reserva.TipoComprobante = NormTipoComprobante(req.TipoComprobante);
            if (req.CondicionIva is not null) reserva.CondicionIva = NormCondIva(req.CondicionIva);
            if (req.Concepto is 1 or 2 or 3) reserva.Concepto = req.Concepto.Value;
            if (req.ArcaWebserviceAccountId.HasValue)
                reserva.ArcaWebserviceAccountId = req.ArcaWebserviceAccountId.Value > 0 ? req.ArcaWebserviceAccountId.Value : null;
        }
        if (req.Estado is not null)
        {
            var ne = NormalizarEstado(req.Estado);
            if (ne is null) return BadRequest(new { error = $"Estado invalido. Validos: {string.Join(", ", EstadosValidos)}" });
            reserva.Estado = ne;
        }

        // 2026-06-26: no permitir que el estado quede por debajo de la realidad del reparto.
        // Si un repartidor ya entregó/retiró, el estado se mantiene consistente (evita desincronización
        // al editar la reserva). El admin puede deshacer el reparto desde "Asignar reparto".
        if (reserva.Estado != "cancelado")
        {
            if (reserva.RetiradoPorRepartidorId.HasValue) reserva.Estado = "finalizado";
            else if (reserva.EntregadoPorRepartidorId.HasValue && (reserva.Estado == "reservado" || reserva.Estado == "confirmado"))
                reserva.Estado = "entregado";
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
            reserva.MontoTotal = req.MontoTotalManual.HasValue
                ? Math.Max(0m, req.MontoTotalManual.Value)
                : Math.Max(0m, subtotal - reserva.Descuento);
        }
        else
        {
            // Sin items nuevos: usar el total a mano si vino, si no recalcular desde los items existentes
            var subtotal = reserva.Items.Sum(i => i.Cantidad * i.PrecioUnitario);
            reserva.MontoTotal = req.MontoTotalManual.HasValue
                ? Math.Max(0m, req.MontoTotalManual.Value)
                : Math.Max(0m, subtotal - reserva.Descuento);
        }

        reserva.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var saved = await _db.AlqReservas
            .Include(r => r.ClienteNav)
            .Include(r => r.EntregadoPorRepartidor)
            .Include(r => r.RetiradoPorRepartidor)
            .Include(r => r.Items).ThenInclude(i => i.EquipoNav)
            .FirstAsync(r => r.Id == reserva.Id);
        return Ok(Map(saved));
    }

    public record EliminarReservaRequest(string? Password);

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromBody] EliminarReservaRequest? req = null)
    {
        var r = await _db.AlqReservas.FindAsync(id);
        if (r is null) return NotFound(new { error = "Reserva no encontrada" });

        // 1) Bloquear si tiene cobranzas cargadas (pendientes o aprobadas). Hay que anularlas/rechazarlas primero.
        var cobranzas = await _db.AlqCobranzasPendientes
            .Where(c => c.ReservaId == id && c.Estado != "RECHAZADA").CountAsync();
        if (cobranzas > 0 || r.MontoCobrado > 0)
            return BadRequest(new { error = "Esta reserva tiene cobranzas aplicadas. Primero rechazá/anulá las cobranzas en 'Cobros repartidor' y después podés eliminarla." });

        // 2) Pedir clave del usuario actual (acción sensible).
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                       ?? User.FindFirst("sub")?.Value;
        if (!int.TryParse(userIdClaim, out var userId))
            return Unauthorized(new { error = "Sesión inválida" });
        var user = await _db.Users.FindAsync(userId);
        if (user is null) return Unauthorized(new { error = "Usuario no encontrado" });
        if (string.IsNullOrEmpty(req?.Password) || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
            return BadRequest(new { error = "Clave incorrecta" });

        _db.AlqReservas.Remove(r);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    /// <summary>
    /// Texto editable con las condiciones de servicio que aparece al pie del PDF de la reserva.
    /// Vive en AppSettings con la key "alq.condiciones".
    /// </summary>
    [HttpGet("condiciones")]
    public async Task<IActionResult> GetCondiciones()
    {
        var s = await _db.AppSettings.FindAsync("alq.condiciones");
        return Ok(new { texto = s?.Value ?? "" });
    }

    [HttpPut("condiciones")]
    public async Task<IActionResult> SetCondiciones([FromBody] SetCondicionesRequest req)
    {
        var s = await _db.AppSettings.FindAsync("alq.condiciones");
        if (s is null)
        {
            s = new AppSetting { Key = "alq.condiciones", Value = req.Texto ?? "" };
            _db.AppSettings.Add(s);
        }
        else
        {
            s.Value = req.Texto ?? "";
        }
        await _db.SaveChangesAsync();
        return Ok(new { texto = s.Value });
    }

    public class SetCondicionesRequest { public string? Texto { get; set; } }

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

    private static AlqReservaDto Map(AlqReserva r, int? asignadoId = null, string? asignadoNombre = null) => new(
        r.Id, r.Numero,
        r.ClienteId, r.ClienteNav?.Nombre ?? "—", r.ClienteNav?.Telefono,
        r.FechaEntrega, r.FechaRetiro, r.HoraInicio, r.HoraFin,
        r.DireccionEvento,
        r.MontoTotal, r.Descuento, r.Sena,
        r.Estado, r.Notas,
        r.CreatedAt, r.UpdatedAt,
        r.Items.Select(i => new AlqReservaItemDto(
            i.Id, i.EquipoId, i.EquipoNav?.Sku ?? "—", i.EquipoNav?.Nombre ?? "—",
            i.Cantidad, i.PrecioUnitario)).ToList(),
        r.FechaEvento,
        r.MapeoLink,
        r.PublicToken, r.MontoCobrado,
        r.EntregadoPorRepartidorId, r.EntregadoPorRepartidor?.Nombre, r.EntregadoAt, r.ComentarioEntrega,
        r.RetiradoPorRepartidorId, r.RetiradoPorRepartidor?.Nombre, r.RetiradoAt, r.ComentarioRetiro,
        asignadoId, asignadoNombre,
        // ARCA (2026-07-04)
        r.TipoComprobante, r.CondicionIva, r.Concepto,
        r.ArcaEstado, r.ArcaCae, r.ArcaCaeVto, r.ArcaPtoVta,
        r.ArcaWebserviceAccountId, r.ArcaCbteNro, r.ArcaCbteTipoNum,
        r.ArcaError, r.ArcaImpTotal);

    private static string NormTipoComprobante(string? t)
    {
        var v = (t ?? "").Trim().ToUpperInvariant();
        return v is "FA" or "FB" or "FC" or "X" ? v : "X";
    }

    private static string NormCondIva(string? c)
    {
        var v = (c ?? "").Trim().ToUpperInvariant();
        return v is "CF" or "RI" or "MO" or "EX" ? v : "CF";
    }

    public class EmitirArcaReservaRequest
    {
        /// <summary>Certificado/CUIT con el que se factura. Si null, usa el guardado en la reserva o el default.</summary>
        public int? ArcaWebserviceAccountId { get; set; }
    }

    /// <summary>
    /// 2026-07-04 — Emite la reserva como factura electrónica AFIP, reusando el mismo motor que Café.
    /// El alquiler se factura como Servicio (Concepto 2) con período = FechaEntrega→FechaRetiro.
    /// Los equipos van como renglones de la factura. El total prorratea para coincidir con MontoTotal.
    /// NO tira excepción: si ARCA rechaza, deja la reserva "pendiente" con el error.
    /// </summary>
    [HttpPost("{id:int}/emitir-arca")]
    public async Task<IActionResult> EmitirArca(int id, [FromBody] EmitirArcaReservaRequest? body)
    {
        var r = await _db.AlqReservas
            .Include(x => x.ClienteNav)
            .Include(x => x.Items).ThenInclude(i => i.EquipoNav)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return NotFound(new { error = "Reserva no encontrada" });
        if (r.ArcaEstado == "autorizado" && !string.IsNullOrEmpty(r.ArcaCae))
            return BadRequest(new { error = $"Esta reserva ya está facturada (CAE {r.ArcaCae}). Para corregir, se emite una Nota de Crédito." });
        if (r.TipoComprobante is not ("FA" or "FB" or "FC"))
            return BadRequest(new { error = "Elegí un tipo de factura (A, B o C) en la reserva antes de emitir." });

        // Permitir override de empresa en el momento de emitir; si no, usar la guardada.
        if (body?.ArcaWebserviceAccountId is > 0) r.ArcaWebserviceAccountId = body.ArcaWebserviceAccountId;

        // 1. Resolver certificado/CUIT (igual que Café: elegido o fallback al CUIT del negocio).
        Api.Models.ArcaWebserviceAccount? arcaAccount;
        if (r.ArcaWebserviceAccountId is > 0)
        {
            arcaAccount = await _db.ArcaWebserviceAccounts
                .FirstOrDefaultAsync(a => a.Id == r.ArcaWebserviceAccountId!.Value && a.IsActive);
            if (arcaAccount is null)
                return BadRequest(new { error = "El certificado ARCA elegido no existe o está desactivado." });
        }
        else
        {
            var cfg = await _db.CafeSettings.FindAsync(1);
            var cuitDigits = new string((cfg?.NegocioCuit ?? "").Where(char.IsDigit).ToArray());
            if (cuitDigits.Length != 11) return BadRequest(new { error = "Elegí con qué empresa facturar (o cargá el CUIT del negocio)." });
            arcaAccount = await _db.ArcaWebserviceAccounts
                .Where(a => a.Cuit == cuitDigits && a.IsActive)
                .OrderByDescending(a => a.Environment == "production").FirstOrDefaultAsync();
            if (arcaAccount is null) return BadRequest(new { error = $"No hay certificado ARCA activo para el CUIT {cuitDigits}." });
        }
        r.ArcaWebserviceAccountId = arcaAccount.Id;

        int cbteTipo = r.TipoComprobante switch { "FA" => 1, "FB" => 6, "FC" => 11, _ => 0 };

        // 2. Receptor (cliente de Cafe_Clientes). CUIT → DocTipo 80, sino Consumidor Final (99/0).
        var cuitCli = new string((r.ClienteNav?.Cuit ?? "").Where(char.IsDigit).ToArray());
        int docTipo = cuitCli.Length == 11 ? 80 : 99;
        string docNro = cuitCli.Length == 11 ? cuitCli : "0";

        // Condición IVA del receptor: la de la reserva, o la del cliente si la tiene cargada.
        var condIva = r.CondicionIva;
        if (!string.IsNullOrEmpty(r.ClienteNav?.CondicionIvaDefault)) condIva = r.ClienteNav!.CondicionIvaDefault!;
        r.CondicionIva = condIva;
        int condIvaReceptor = condIva switch { "RI" => 1, "EX" => 4, "CF" => 5, "MO" => 6, _ => 5 };

        // 3. Items: un renglón por equipo. Prorrateo para que el total AFIP == MontoTotal de la reserva
        //    (contempla descuento global y total manual).
        decimal subtotalItems = r.Items.Sum(i => i.Cantidad * i.PrecioUnitario);
        decimal factor = (subtotalItems > 0m && r.MontoTotal > 0m) ? (r.MontoTotal / subtotalItems) : 1m;
        var items = new List<EmitirComprobanteItemDto>();
        foreach (var it in r.Items)
        {
            var pu = factor != 1m
                ? Math.Round(it.PrecioUnitario * factor, 2, MidpointRounding.AwayFromZero)
                : it.PrecioUnitario;
            items.Add(new EmitirComprobanteItemDto
            {
                Descripcion = it.EquipoNav?.Nombre ?? "Alquiler",
                Cantidad = it.Cantidad,
                PrecioUnitario = pu,
                AlicIvaId = 5, // 21%
            });
        }
        if (items.Count == 0)
            return BadRequest(new { error = "La reserva no tiene equipos para facturar." });

        // 4. Fechas. Emisión = hoy (ART). Servicio = entrega→retiro. FchVtoPago nunca anterior a la emisión.
        var fechaEmision = DateTime.UtcNow.AddHours(-3).Date;
        DateTime? servDesde = r.Concepto != 1 ? r.FechaEntrega : null;
        DateTime? servHasta = r.Concepto != 1 ? r.FechaRetiro : null;
        DateTime? vtoPago = r.Concepto != 1 ? (r.FechaRetiro >= fechaEmision ? r.FechaRetiro : fechaEmision) : null;

        var req = new EmitirComprobanteRequest
        {
            PtoVta = arcaAccount.PtoVta,
            CbteTipo = cbteTipo,
            Concepto = r.Concepto,
            DocTipo = docTipo,
            DocNro = docNro,
            ReceptorNombre = r.ClienteNav?.RazonSocial ?? r.ClienteNav?.Nombre ?? "Consumidor Final",
            ReceptorDomicilio = r.ClienteNav?.Direccion,
            CondicionIVAReceptorId = condIvaReceptor,
            Items = items,
            Fecha = fechaEmision,
            FchServDesde = servDesde,
            FchServHasta = servHasta,
            FchVtoPago = vtoPago,
        };

        // 5. Emitir + persistir.
        try
        {
            var res = await _arca.EmitirComprobanteAsync(arcaAccount.Id, req);
            if (res.Success)
            {
                r.ArcaEstado = "autorizado";
                r.ArcaCae = res.Cae;
                if (DateTime.TryParseExact(res.CaeVto, "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var caeVto))
                    r.ArcaCaeVto = caeVto;
                r.ArcaPtoVta = res.PtoVta;
                r.ArcaCbteNro = res.CbteNro;
                r.ArcaCbteTipoNum = res.CbteTipo;
                r.ArcaImpNeto = res.ImpNeto;
                r.ArcaImpIVA = res.ImpIVA;
                r.ArcaImpTotal = res.ImpTotal;
                r.ArcaError = null;
            }
            else
            {
                r.ArcaEstado = "pendiente";
                r.ArcaError = res.Error ?? "ARCA rechazó la factura.";
            }
        }
        catch (Exception ex)
        {
            r.ArcaEstado = "pendiente";
            r.ArcaError = "Error inesperado: " + ex.Message;
        }
        r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var saved = await _db.AlqReservas
            .Include(x => x.ClienteNav)
            .Include(x => x.EntregadoPorRepartidor).Include(x => x.RetiradoPorRepartidor)
            .Include(x => x.Items).ThenInclude(i => i.EquipoNav)
            .FirstAsync(x => x.Id == r.Id);
        if (r.ArcaEstado == "autorizado") return Ok(Map(saved));
        return BadRequest(new { error = r.ArcaError, reserva = Map(saved) });
    }

    /// <summary>Dado un set de reservas, devuelve el repartidor "dueño" actual de cada una
    /// (el del ultimo escaneo 'cargado' en Alq_QrEscaneos). Igual regla que ventas.</summary>
    private async Task<Dictionary<int, (int Id, string Nombre)>> CalcularAsignadosAsync(List<int> reservaIds)
    {
        var res = new Dictionary<int, (int, string)>();
        if (reservaIds.Count == 0) return res;
        var escaneos = await _db.AlqQrEscaneos
            .Where(e => reservaIds.Contains(e.ReservaId) && e.Accion == "cargado")
            .Select(e => new { e.ReservaId, e.RepartidorId, e.CreatedAt, e.Id })
            .ToListAsync();
        var owners = escaneos
            .GroupBy(e => e.ReservaId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).First().RepartidorId);
        if (owners.Count == 0) return res;
        var repIds = owners.Values.Distinct().ToList();
        var reps = await _db.CafeRepartidores.Where(r => repIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Nombre);
        foreach (var kv in owners)
            if (reps.TryGetValue(kv.Value, out var nombre)) res[kv.Key] = (kv.Value, nombre);
        return res;
    }
}
