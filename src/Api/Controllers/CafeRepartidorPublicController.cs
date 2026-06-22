using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Endpoints publicos (sin auth) usados por la pantalla mobile /repartidor/{token}.
/// El "login" del repartidor es el PIN de 3 digitos del DNI — patron tomado de Horas Extras.
/// La sesion del PIN es manejada por el frontend (15 min de inactividad). Aca cada endpoint
/// pide el PIN cada vez (el backend no guarda sesion), pero el frontend lo guarda y reenvia.
/// </summary>
[ApiController]
[Route("api/cafe/repartidor-public")]
[AllowAnonymous]
public class CafeRepartidorPublicController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly MeliShipmentService _me1Service;
    public CafeRepartidorPublicController(AppDbContext db, MeliShipmentService me1Service)
    {
        _db = db; _me1Service = me1Service;
    }

    public record RepartidorListItemDto(int Id, string Nombre);
    public record InfoVentaDto(int VentaId, string Numero, DateTime Fecha,
        string? ClienteNombre, string? ClienteDireccion, string? ClienteLocalidad, string? ClienteCiudad,
        decimal TotalCobrable, decimal SaldoPendiente,
        bool YaEntregada, string? EntregadoPor,
        List<ItemSimpleDto> Items);
    public record ItemSimpleDto(int Cantidad, string Nombre, string Formato, string? Molienda, bool EsDoyPack, bool EsEnvasePlateado);

    /// <summary>Lista de repartidores activos para el primer paso "¿Quien sos?".
    /// Solo Nombre + Id, sin PIN.</summary>
    [HttpGet("repartidores")]
    public async Task<IActionResult> Repartidores()
    {
        var l = await _db.CafeRepartidores.Where(r => r.IsActive)
            .OrderBy(r => r.Nombre)
            .Select(r => new RepartidorListItemDto(r.Id, r.Nombre))
            .ToListAsync();
        return Ok(l);
    }

    public record LoginRequest(int RepartidorId, string Pin);

    /// <summary>Valida que el PIN coincida con el repartidor. Devuelve el nombre si OK
    /// (el frontend lo usa para mostrar "Hola, Maxi"). NO devuelve token — el frontend
    /// guarda RepartidorId + Pin en sessionStorage y reenvía en cada request.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.Id == req.RepartidorId && x.IsActive);
        if (r is null) return BadRequest(new { error = "Repartidor no encontrado" });
        if (string.IsNullOrEmpty(r.DniUltimos3)) return BadRequest(new { error = "Este repartidor no tiene PIN configurado. Avisale al admin." });
        if ((req.Pin ?? "").Trim() != r.DniUltimos3) return Unauthorized(new { error = "PIN incorrecto" });
        return Ok(new { id = r.Id, nombre = r.Nombre });
    }

    /// <summary>Devuelve info de la venta para el repartidor (al escanear el QR).
    /// NO pide PIN — la info no es sensible (cliente, importe, items).</summary>
    [HttpGet("venta/{token}")]
    public async Task<IActionResult> InfoVenta(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return BadRequest();
        var v = await _db.CafeVentas
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.PublicToken == token);
        if (v is null) return NotFound(new { error = "Venta no encontrada (token invalido)" });

        var totalCobrable = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
        var pagado = await _db.CafeCobranzasComprobantes
            .Where(c => c.VentaId == v.Id && c.Cobranza!.Estado == "VIGENTE").SumAsync(c => (decimal?)c.Importe) ?? 0m;
        var saldo = totalCobrable - pagado;

        string? entregadoPor = null;
        if (v.EntregadoPorRepartidorId.HasValue)
            entregadoPor = await _db.CafeRepartidores.Where(r => r.Id == v.EntregadoPorRepartidorId.Value)
                .Select(r => r.Nombre).FirstOrDefaultAsync();

        var items = v.Items.Select(i => new ItemSimpleDto(
            i.Cantidad, i.ProductoNombreSnapshot, i.Formato, i.Molienda, i.EsDoyPack, i.EsEnvasePlateado)).ToList();

        // 2026-06-18: para el repartidor lo que importa es DONDE entregar (DomicilioEntrega), no
        // la direccion fiscal. Si esta vacio caemos a Direccion. Snapshot porque es lo que se
        // guardo cuando se cargo la venta — consistente.
        var dirParaRepartidor = string.IsNullOrWhiteSpace(v.ClienteDomicilioEntregaSnapshot)
            ? v.ClienteDireccionSnapshot
            : v.ClienteDomicilioEntregaSnapshot;
        return Ok(new InfoVentaDto(
            v.Id, v.Numero, v.Fecha,
            v.ClienteNombreSnapshot, dirParaRepartidor, v.ClienteLocalidadSnapshot, v.ClienteCiudadSnapshot,
            totalCobrable, saldo,
            v.EntregadoPorRepartidorId.HasValue, entregadoPor,
            items));
    }

    public record CobrarRequest(int RepartidorId, string Pin, bool MarcarEntregado, decimal? Importe, string? Notas);

    /// <summary>Carga una cobranza pendiente. Valida PIN del repartidor en cada request.
    /// El Importe es opcional — si no viene, se asume que no cobro (solo entrego).
    /// Si MarcarEntregado=true y la venta esta en flujo de Preparacion, se setea a "ENTREGADO".
    /// </summary>
    [HttpPost("cobrar/{token}")]
    public async Task<IActionResult> Cobrar(string token, [FromBody] CobrarRequest req)
    {
        if (string.IsNullOrWhiteSpace(token)) return BadRequest();
        var v = await _db.CafeVentas.FirstOrDefaultAsync(x => x.PublicToken == token);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });

        // Validar PIN
        var rep = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.Id == req.RepartidorId && x.IsActive);
        if (rep is null) return BadRequest(new { error = "Repartidor no valido" });
        if (string.IsNullOrEmpty(rep.DniUltimos3) || (req.Pin ?? "").Trim() != rep.DniUltimos3)
            return Unauthorized(new { error = "PIN incorrecto" });

        var importe = Math.Max(0m, req.Importe ?? 0m);
        var marcoEntregado = req.MarcarEntregado;

        if (importe <= 0m && !marcoEntregado)
            return BadRequest(new { error = "No marcaste 'entregue' ni cargaste importe — no hay nada que guardar" });

        // Si solo marca entregado (sin importe), actualizar directo la venta sin crear cobranza pendiente
        if (importe <= 0m && marcoEntregado)
        {
            v.EntregadoPorRepartidorId = rep.Id;
            v.EntregadoAt = DateTime.UtcNow;
            if (v.EstadoPreparacion != null)
            {
                var estadoAntE = v.EstadoPreparacion;
                v.EstadoPreparacion = "ENTREGADO";
                v.PreparacionUpdatedAt = DateTime.UtcNow;
                // 2026-06-09 log
                _db.CafeVentaPreparacionLogs.Add(new CafeVentaPreparacionLog
                {
                    VentaId = v.Id, EstadoAnterior = estadoAntE, EstadoNuevo = "ENTREGADO",
                    OperadorNombre = $"repartidor: {rep.Nombre}",
                    Notas = "Repartidor marco entregada (sin cobro)",
                    CreatedAt = DateTime.UtcNow
                });
            }
            await _db.SaveChangesAsync();
            return Ok(new { soloEntrega = true, mensaje = $"✓ Marcaste como entregada (sin cobro)" });
        }

        // Sino, crear cobranza pendiente que el admin aprueba despues
        var pend = new CafeCobranzaPendiente
        {
            VentaId = v.Id,
            RepartidorId = rep.Id,
            Importe = importe,
            MarcadoEntregado = marcoEntregado,
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas!.Trim(),
            Estado = "PENDIENTE",
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeCobranzasPendientes.Add(pend);

        // Si marca entregado, anotar repartidor en la venta tambien (info inmediata aunque
        // la cobranza este pendiente de aprobar)
        if (marcoEntregado)
        {
            v.EntregadoPorRepartidorId = rep.Id;
            v.EntregadoAt = DateTime.UtcNow;
            if (v.EstadoPreparacion != null)
            {
                var estadoAntC = v.EstadoPreparacion;
                v.EstadoPreparacion = "ENTREGADO";
                v.PreparacionUpdatedAt = DateTime.UtcNow;
                // 2026-06-09 log
                _db.CafeVentaPreparacionLogs.Add(new CafeVentaPreparacionLog
                {
                    VentaId = v.Id, EstadoAnterior = estadoAntC, EstadoNuevo = "ENTREGADO",
                    OperadorNombre = $"repartidor: {rep.Nombre}",
                    Notas = "Repartidor marco entregada + cobranza precargada",
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await _db.SaveChangesAsync();
        return Ok(new { id = pend.Id, mensaje = $"✓ Cobranza precargada — el admin la va a aprobar despues" });
    }

    // ============================================================
    // 2026-06-05: Flujo nuevo "Mis Pedidos" del repartidor
    // ============================================================
    //  /sesion/login          → genera SessionToken (8 hs) tras validar PIN
    //  /sesion/me             → devuelve nombre del repartidor logueado (valida token)
    //  /sesion/logout         → revoca el token actual
    //  /escanear/{tokenVenta} → agrega la venta a la lista del repartidor logueado
    //  /mis-pedidos/{tokenRepartidor}                       → GET lista (publico, no auth)
    //  /mis-pedidos/{tokenRepartidor}/entregar/{ventaId}    → POST con PIN
    //  /mis-pedidos/{tokenRepartidor}/cobrar/{ventaId}      → POST con PIN + importe

    private const string SessionHeader = "X-Repartidor-Session";
    private static readonly TimeSpan SessionDuration = TimeSpan.FromHours(8);

    private string? ReadSessionToken() =>
        Request.Headers.TryGetValue(SessionHeader, out var v) ? v.ToString() : null;

    private async Task<CafeRepartidorSesion?> ResolverSesion(CancellationToken ct = default)
    {
        var token = ReadSessionToken();
        if (string.IsNullOrWhiteSpace(token)) return null;
        var s = await _db.CafeRepartidorSesiones
            .Include(x => x.Repartidor)
            .FirstOrDefaultAsync(x => x.SessionToken == token && !x.Revoked, ct);
        if (s is null || s.ExpiresAt <= DateTime.UtcNow) return null;
        // Touch last-used (no critical, no esperamos al save)
        s.LastUsedAt = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(ct); } catch { }
        return s;
    }

    public record SesionLoginResult(string SessionToken, DateTime ExpiresAt, int RepartidorId, string Nombre, string? PublicToken);

    [HttpPost("sesion/login")]
    public async Task<IActionResult> SesionLogin([FromBody] LoginRequest req)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.Id == req.RepartidorId && x.IsActive);
        if (r is null) return BadRequest(new { error = "Repartidor no encontrado" });
        if (string.IsNullOrEmpty(r.DniUltimos3))
            return BadRequest(new { error = "Este repartidor no tiene PIN configurado. Avisale al admin." });
        if ((req.Pin ?? "").Trim() != r.DniUltimos3)
            return Unauthorized(new { error = "PIN incorrecto" });

        var sessionToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N").Substring(0, 16);
        var now = DateTime.UtcNow;
        var ua = Request.Headers["User-Agent"].ToString();
        var device = string.IsNullOrEmpty(ua) ? null : (ua.Length > 180 ? ua.Substring(0, 180) : ua);

        var sesion = new CafeRepartidorSesion
        {
            RepartidorId = r.Id,
            SessionToken = sessionToken,
            DeviceInfo = device,
            CreatedAt = now,
            ExpiresAt = now.Add(SessionDuration),
            LastUsedAt = now,
            Revoked = false
        };
        _db.CafeRepartidorSesiones.Add(sesion);
        await _db.SaveChangesAsync();

        // Si no tiene PublicToken, generarlo ya (para el enlace fijo /mis-pedidos)
        if (string.IsNullOrEmpty(r.PublicToken))
        {
            r.PublicToken = Guid.NewGuid().ToString("N");
            r.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return Ok(new SesionLoginResult(sessionToken, sesion.ExpiresAt, r.Id, r.Nombre, r.PublicToken));
    }

    [HttpGet("sesion/me")]
    public async Task<IActionResult> SesionMe()
    {
        var s = await ResolverSesion();
        if (s is null) return Unauthorized(new { error = "Sesion expirada o invalida" });
        return Ok(new { repartidorId = s.RepartidorId, nombre = s.Repartidor?.Nombre, expiresAt = s.ExpiresAt });
    }

    [HttpPost("sesion/logout")]
    public async Task<IActionResult> SesionLogout()
    {
        var s = await ResolverSesion();
        if (s is null) return Ok(); // ya no esta
        s.Revoked = true;
        await _db.SaveChangesAsync();
        return Ok();
    }

    public record EscanearResult(bool Ok, string? Mensaje, int? VentaId, string? Numero,
        string? ClienteNombre, decimal? Total, bool YaEntregada);

    /// <summary>Escaneo de un QR de venta cuando ya hay sesion activa. Agrega la venta a la
    /// lista del repartidor (insert en QrEscaneos con accion=cargado). NO marca entregado —
    /// eso lo hace el repartidor despues en /mis-pedidos confirmando con PIN.</summary>
    [HttpPost("escanear/{tokenVenta}")]
    public async Task<IActionResult> Escanear(string tokenVenta)
    {
        var s = await ResolverSesion();
        if (s is null) return Unauthorized(new { error = "Necesitas loguearte primero (sesion vencida)" });

        var v = await _db.CafeVentas.FirstOrDefaultAsync(x => x.PublicToken == tokenVenta);
        if (v is null) return NotFound(new EscanearResult(false, "Venta no encontrada (QR invalido)", null, null, null, null, false));

        // 2026-06-12: regla "el último que escanea se lo queda". El dueño del pedido es el
        // repartidor del ÚLTIMO escaneo 'cargado'. Si otro lo tenía, al escanear pasa a mi
        // lista y desaparece de la del anterior (la lista filtra por dueño actual).
        // Si lo tenía yo mismo, no se agrega duplicado.
        var ultimoCargado = await _db.CafeQrEscaneos
            .Where(e => e.VentaId == v.Id && e.Accion == "cargado")
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync();
        var yaEsMia = ultimoCargado is not null && ultimoCargado.RepartidorId == s.RepartidorId;

        string? transferidoDe = null;
        if (!yaEsMia)
        {
            if (ultimoCargado is not null && !v.EntregadoPorRepartidorId.HasValue)
            {
                var otro = await _db.CafeRepartidores.FindAsync(ultimoCargado.RepartidorId);
                transferidoDe = otro?.Nombre;
            }
            _db.CafeQrEscaneos.Add(new CafeQrEscaneo
            {
                VentaId = v.Id,
                RepartidorId = s.RepartidorId,
                Accion = "cargado",
                CreatedAt = DateTime.UtcNow,
                Ip = Request.HttpContext.Connection.RemoteIpAddress?.ToString()
            });
            await _db.SaveChangesAsync();
        }

        var totalCobrable = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
        var msg = yaEsMia
            ? $"Ya estaba en tu lista: {v.Numero}"
            : transferidoDe is not null
                ? $"⚠ Este pedido lo tenía {transferidoDe} — ahora pasó a TU lista: {v.Numero}"
                : $"✅ Cargado: {v.Numero}";
        return Ok(new EscanearResult(true, msg, v.Id, v.Numero,
            v.ClienteNombreSnapshot, totalCobrable, v.EntregadoPorRepartidorId.HasValue));
    }

    public record MisPedidosVentaDto(int Id, string Numero, DateTime Fecha,
        string? ClienteNombre, string? ClienteDireccion, string? ClienteLocalidad,
        decimal Total, decimal Saldo,
        bool YaEntregada, DateTime? EntregadoAt, string? EstadoPreparacion,
        DateTime CargadoAt,
        // 2026-06-08: comentario que dejó el repartidor al marcar entregado (opcional)
        string? ComentarioEntrega,
        // 2026-06-08 v2: true si la venta tiene PublicToken (el PDF se genera al toque).
        // En la práctica es true para todas las ventas creadas con el sistema actual.
        bool TienePdf,
        // 2026-06-11: datos del cliente para mostrar accesos rápidos (teléfono, Maps/Waze)
        // y para poder capturar la ubicación si todavía no la tiene.
        int? ClienteId,
        string? ClienteTelefono,
        decimal? ClienteLat,
        decimal? ClienteLng,
        // 2026-06-11: link de Maps que vos pegaste manual en la ficha del cliente — si existe
        // se prioriza sobre la búsqueda por dirección (más exacto). Lat/Lng siempre gana si están.
        string? ClienteMapeoLink);

    /// <summary>2026-06-09: cobros hechos por el repartidor (siempre en efectivo) — para el arqueo.</summary>
    public record MisPedidosCobroDto(int VentaId, decimal Importe, string Estado, DateTime FechaCobro);

    public record MisPedidosResult(int RepartidorId, string Nombre,
        List<MisPedidosVentaDto> Pedidos,
        // 2026-06-09: cobros del repartidor (todos en efectivo) — el frontend filtra por fecha y arma arqueo
        List<MisPedidosCobroDto> Cobros);

    /// <summary>Devuelve la lista de pedidos cargados por el repartidor. URL publica con
    /// token fijo del repartidor — sin PIN. El PIN solo se pide al CONFIRMAR entrega/cobro.</summary>
    [HttpGet("mis-pedidos/{tokenRepartidor}")]
    public async Task<IActionResult> MisPedidos(string tokenRepartidor, [FromQuery] int dias = 14)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (r is null) return NotFound(new { error = "Enlace invalido o repartidor inactivo" });

        var desde = DateTime.UtcNow.AddDays(-Math.Max(1, dias));
        // Traer todos los QrEscaneos "cargados" recientes del repartidor + datos de la venta
        // + LEFT JOIN con CafeClientes para traer telefono y coordenadas (Mapeo) si las tiene.
        var rows = await _db.CafeQrEscaneos
            .Where(e => e.RepartidorId == r.Id && e.Accion == "cargado" && e.CreatedAt >= desde)
            .OrderByDescending(e => e.CreatedAt)
            .Join(_db.CafeVentas, e => e.VentaId, v => v.Id, (e, v) => new { e, v })
            .GroupJoin(_db.CafeClientes, ev => ev.v.ClienteId, c => c.Id,
                       (ev, cs) => new { ev.e, ev.v, c = cs.FirstOrDefault() })
            .Select(x => new {
                x.v.Id, x.v.Numero, x.v.Fecha,
                ClienteNombre = x.v.ClienteNombreSnapshot,
                // 2026-06-18: para el repartidor importa DONDE entregar. Prioridad:
                //   1. DomicilioEntrega de la ficha actual del cliente (puede haber sido actualizado)
                //   2. DomicilioEntregaSnapshot (capturado al cargar la venta)
                //   3. Direccion de la ficha actual (fiscal)
                //   4. DireccionSnapshot
                ClienteDireccion = (x.c != null && x.c.DomicilioEntrega != null && x.c.DomicilioEntrega != "")
                    ? x.c.DomicilioEntrega
                    : ((x.v.ClienteDomicilioEntregaSnapshot != null && x.v.ClienteDomicilioEntregaSnapshot != "")
                        ? x.v.ClienteDomicilioEntregaSnapshot
                        : ((x.c != null && x.c.Direccion != null && x.c.Direccion != "")
                            ? x.c.Direccion
                            : x.v.ClienteDireccionSnapshot)),
                ClienteLocalidad = x.v.ClienteLocalidadSnapshot,
                Total = (x.v.ArcaImpTotal.HasValue && x.v.ArcaImpTotal.Value > 0m) ? x.v.ArcaImpTotal.Value : x.v.Total,
                x.v.EntregadoAt,
                YaEntregada = x.v.EntregadoPorRepartidorId.HasValue,
                x.v.EstadoPreparacion,
                CargadoAt = x.e.CreatedAt,
                x.v.ComentarioEntrega,
                // 2026-06-08 v2: con PublicToken alcanza — el PDF se genera al toque, no depende de Drive
                TienePdf = !string.IsNullOrEmpty(x.v.PublicToken),
                // 2026-06-11: campos para accesos rápidos del repartidor
                ClienteId = x.v.ClienteId,
                ClienteTelefono = (x.c != null ? x.c.Telefono : null) ?? x.v.ClienteTelefonoSnapshot,
                ClienteLat = x.c != null ? x.c.MapeoLat : null,
                ClienteLng = x.c != null ? x.c.MapeoLng : null,
                ClienteMapeoLink = x.c != null ? x.c.MapeoLink : null
            })
            .ToListAsync();

        // 2026-06-12: regla "el último que escanea se lo queda" — un pedido PENDIENTE solo
        // aparece en la lista de su dueño actual (el repartidor del último escaneo 'cargado').
        // Las ya entregadas no se filtran: quedan en el historial del que las trabajó.
        var idsParaDuenio = rows.Where(x => !x.YaEntregada).Select(x => x.Id).Distinct().ToList();
        if (idsParaDuenio.Count > 0)
        {
            var duenios = await _db.CafeQrEscaneos
                .Where(e => idsParaDuenio.Contains(e.VentaId) && e.Accion == "cargado")
                .Select(e => new { e.VentaId, e.RepartidorId, e.CreatedAt, e.Id })
                .ToListAsync();
            var duenioActual = duenios
                .GroupBy(e => e.VentaId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).First().RepartidorId);
            rows = rows.Where(x => x.YaEntregada
                || !duenioActual.TryGetValue(x.Id, out var owner) || owner == r.Id).ToList();
        }
        // Dedupe: si el repartidor escaneó la misma venta varias veces, mostrarla una sola vez (el escaneo más reciente)
        rows = rows.GroupBy(x => x.Id).Select(g => g.OrderByDescending(x => x.CargadoAt).First()).ToList();

        // Saldos: sumar cobranzas vigentes por venta
        var ventaIds = rows.Select(x => x.Id).Distinct().ToList();
        var pagosDic = ventaIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await _db.CafeCobranzasComprobantes
                .Where(c => c.VentaId.HasValue && ventaIds.Contains(c.VentaId.Value) && c.Cobranza!.Estado == "VIGENTE")
                .GroupBy(c => c.VentaId!.Value)
                .Select(g => new { g.Key, S = g.Sum(x => x.Importe) })
                .ToDictionaryAsync(x => x.Key, x => x.S);

        var pedidos = rows.Select(x => new MisPedidosVentaDto(
            x.Id, x.Numero, x.Fecha,
            x.ClienteNombre, x.ClienteDireccion, x.ClienteLocalidad,
            x.Total, x.Total - (pagosDic.TryGetValue(x.Id, out var pg) ? pg : 0m),
            x.YaEntregada, x.EntregadoAt, x.EstadoPreparacion,
            x.CargadoAt,
            x.ComentarioEntrega,
            x.TienePdf,
            x.ClienteId, x.ClienteTelefono, x.ClienteLat, x.ClienteLng, x.ClienteMapeoLink
        )).ToList();

        // 2026-06-09: cobros del repartidor (siempre en efectivo, viven en Cafe_CobranzasPendientes).
        // Si el admin aprobo -> Estado='APROBADA'. Si rechazo -> 'RECHAZADA'. Sino 'PENDIENTE'.
        // Para el arqueo del repartidor sumamos PENDIENTE + APROBADA (todo lo que cobro en mano).
        var cobros = await _db.CafeCobranzasPendientes
            .Where(p => p.RepartidorId == r.Id
                     && p.Estado != "RECHAZADA"
                     && p.CreatedAt >= desde)
            .Select(p => new MisPedidosCobroDto(p.VentaId, p.Importe, p.Estado, p.CreatedAt))
            .ToListAsync();

        return Ok(new MisPedidosResult(r.Id, r.Nombre, pedidos, cobros));
    }

    public record MisPedidosResumenItemDto(string ProductoNombre, string? Formato, decimal Cantidad, decimal Subtotal);
    public record MisPedidosResumenDto(int VentaId, string Numero, string? ClienteNombre, string? ClienteDireccion,
        string? ClienteLocalidad, decimal Total, string? Observaciones, string? ComentarioEntrega,
        List<MisPedidosResumenItemDto> Items);

    /// <summary>2026-06-10: Devuelve el resumen de items + observaciones de una venta para
    /// que el repartidor lo vea en un modal dentro de Mis Pedidos (sin descargar PDF).
    /// Valida que la venta este en la lista del repartidor (QrEscaneos accion='cargado').</summary>
    [HttpGet("mis-pedidos/{tokenRepartidor}/venta/{ventaId:int}/resumen")]
    public async Task<IActionResult> MisPedidosResumen(string tokenRepartidor, int ventaId)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (r is null) return NotFound(new { error = "Enlace invalido o repartidor inactivo" });

        var enSuLista = await _db.CafeQrEscaneos.AnyAsync(e =>
            e.VentaId == ventaId && e.RepartidorId == r.Id && e.Accion == "cargado");
        if (!enSuLista) return Forbid();

        var v = await _db.CafeVentas
            .Include(x => x.Items!).ThenInclude(it => it.ProductoNav)
            .FirstOrDefaultAsync(x => x.Id == ventaId);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });

        // Items desglosados (el repartidor ve TODO lo que tiene que llevar, sin agrupar por combo)
        var items = new List<MisPedidosResumenItemDto>();
        if (v.Items is not null)
        {
            foreach (var it in v.Items.OrderBy(x => x.Id))
            {
                var nombre = !string.IsNullOrWhiteSpace(it.ProductoNombreSnapshot)
                    ? it.ProductoNombreSnapshot
                    : (it.ProductoNav?.Nombre ?? (it.ServicioId.HasValue ? "Servicio" : "(producto)"));
                items.Add(new MisPedidosResumenItemDto(
                    nombre!,
                    it.Formato,
                    it.Cantidad,
                    it.Subtotal));
            }
        }

        var totalCobrable = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;

        // 2026-06-18: mismo criterio que las otras cards — el repartidor necesita DomicilioEntrega prioritario.
        var dirResumen = string.IsNullOrWhiteSpace(v.ClienteDomicilioEntregaSnapshot)
            ? v.ClienteDireccionSnapshot
            : v.ClienteDomicilioEntregaSnapshot;
        return Ok(new MisPedidosResumenDto(
            v.Id, v.Numero,
            v.ClienteNombreSnapshot, dirResumen, v.ClienteLocalidadSnapshot,
            totalCobrable,
            v.Observaciones,
            v.ComentarioEntrega,
            items));
    }

    /// <summary>2026-06-05: Escaneo de QR desde la pantalla Mis Pedidos del repartidor.
    /// Usa el publicToken del repartidor (la URL fija) como autorizacion — no requiere PIN
    /// porque solo "carga" el pedido a la lista, no confirma entrega.</summary>
    [HttpPost("mis-pedidos/{tokenRepartidor}/escanear/{tokenVenta}")]
    public async Task<IActionResult> MisPedidosEscanear(string tokenRepartidor, string tokenVenta)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (r is null) return NotFound(new EscanearResult(false, "Enlace invalido o repartidor inactivo", null, null, null, null, false));

        var v = await _db.CafeVentas.FirstOrDefaultAsync(x => x.PublicToken == tokenVenta);
        if (v is null) return NotFound(new EscanearResult(false, "Venta no encontrada (QR invalido)", null, null, null, null, false));

        // Idempotente: no agregar duplicado
        var yaCargada = await _db.CafeQrEscaneos.AnyAsync(e =>
            e.VentaId == v.Id && e.RepartidorId == r.Id && e.Accion == "cargado");
        if (!yaCargada)
        {
            _db.CafeQrEscaneos.Add(new CafeQrEscaneo
            {
                VentaId = v.Id,
                RepartidorId = r.Id,
                Accion = "cargado",
                CreatedAt = DateTime.UtcNow,
                Ip = Request.HttpContext.Connection.RemoteIpAddress?.ToString()
            });
            await _db.SaveChangesAsync();
        }

        var totalCobrable = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
        var msg = yaCargada
            ? $"Ya estaba en tu lista: {v.Numero}"
            : $"✅ Cargado: {v.Numero}";
        return Ok(new EscanearResult(true, msg, v.Id, v.Numero,
            v.ClienteNombreSnapshot, totalCobrable, v.EntregadoPorRepartidorId.HasValue));
    }

    /// <summary>2026-06-08: Comentario opcional al marcar entrega ("dejé con el casero").</summary>
    // 2026-06-22: extender EntregarRequest con datos de firma + receptor. Todos opcionales para
    // compatibilidad con clientes viejos que no manden esos campos. Cuando v.SolicitarFirmaEntrega=true
    // el frontend deberia enviar FirmaBase64 + NombreReceptor, o MotivoSinFirma si se salto.
    public record EntregarRequest(
        string? Pin,
        string? Comentario = null,
        string? FirmaBase64 = null,
        string? NombreReceptor = null,
        string? DniReceptor = null,
        string? MotivoSinFirma = null);

    /// <summary>Marca como entregada una venta de la lista del repartidor.
    /// 2026-06-08: ya NO valida PIN — el publicToken (URL única del repartidor) basta como auth.
    /// El frontend muestra un botón "¿Confirmás?" para evitar tap accidental.
    /// Registra en QrEscaneos accion=entregado.</summary>
    [HttpPost("mis-pedidos/{tokenRepartidor}/entregar/{ventaId:int}")]
    public async Task<IActionResult> MisPedidosEntregar(string tokenRepartidor, int ventaId, [FromBody] EntregarRequest? req)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (r is null) return NotFound(new { error = "Enlace invalido" });

        var v = await _db.CafeVentas.FirstOrDefaultAsync(x => x.Id == ventaId);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });

        // Verificar que sea de su lista
        var enSuLista = await _db.CafeQrEscaneos.AnyAsync(e =>
            e.VentaId == v.Id && e.RepartidorId == r.Id && e.Accion == "cargado");
        if (!enSuLista) return BadRequest(new { error = "Esta venta no esta en tu lista" });

        v.EntregadoPorRepartidorId = r.Id;
        v.EntregadoAt = DateTime.UtcNow;
        // 2026-06-08: comentario del repartidor (opcional)
        var comentario = req?.Comentario?.Trim();
        if (!string.IsNullOrWhiteSpace(comentario))
        {
            v.ComentarioEntrega = comentario.Length > 500 ? comentario.Substring(0, 500) : comentario;
        }
        // 2026-06-22: datos de firma + receptor (opcionales). Si vienen, los persistimos en la venta.
        var firma = req?.FirmaBase64?.Trim();
        var nombreReceptor = req?.NombreReceptor?.Trim();
        var motivoSin = req?.MotivoSinFirma?.Trim();
        if (!string.IsNullOrEmpty(firma))
        {
            v.FirmaBase64 = firma;
            v.EntregaFirmadaAt = DateTime.UtcNow;
        }
        if (!string.IsNullOrEmpty(nombreReceptor))
        {
            v.NombreReceptor = nombreReceptor.Length > 200 ? nombreReceptor.Substring(0, 200) : nombreReceptor;
        }
        if (!string.IsNullOrEmpty(req?.DniReceptor))
        {
            v.DniReceptor = req.DniReceptor.Length > 50 ? req.DniReceptor.Substring(0, 50) : req.DniReceptor;
        }
        if (!string.IsNullOrEmpty(motivoSin))
        {
            v.MotivoSinFirma = motivoSin.Length > 300 ? motivoSin.Substring(0, 300) : motivoSin;
        }
        if (v.EstadoPreparacion != null)
        {
            var estadoAntE2 = v.EstadoPreparacion;
            v.EstadoPreparacion = "ENTREGADO";
            v.PreparacionUpdatedAt = DateTime.UtcNow;
            // 2026-06-09 log
            _db.CafeVentaPreparacionLogs.Add(new CafeVentaPreparacionLog
            {
                VentaId = v.Id, EstadoAnterior = estadoAntE2, EstadoNuevo = "ENTREGADO",
                OperadorNombre = $"repartidor: {r.Nombre}",
                Notas = "Repartidor marco entregado desde /mis-pedidos" + (string.IsNullOrWhiteSpace(comentario) ? "" : $" — comentario: {comentario}"),
                CreatedAt = DateTime.UtcNow
            });
        }
        _db.CafeQrEscaneos.Add(new CafeQrEscaneo
        {
            VentaId = v.Id,
            RepartidorId = r.Id,
            Accion = "entregado",
            CreatedAt = DateTime.UtcNow,
            Ip = Request.HttpContext.Connection.RemoteIpAddress?.ToString()
        });
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, mensaje = $"✓ Marcado como entregado" });
    }

    /// <summary>2026-06-08: Devuelve la URL del PDF de la venta en Google Drive (preview).
    /// Lo usa el botón "📄 Ver comprobante" en /mis-pedidos para que el repartidor vea
    /// la mercadería sin tener que armar UI nueva — reusa el PDF que ya existe.
    /// Solo accesible si la venta está en su lista.</summary>
    [HttpGet("mis-pedidos/{tokenRepartidor}/comprobante/{ventaId:int}")]
    public async Task<IActionResult> MisPedidosComprobante(string tokenRepartidor, int ventaId)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (r is null) return NotFound(new { error = "Enlace invalido" });
        var enSuLista = await _db.CafeQrEscaneos.AnyAsync(e =>
            e.VentaId == ventaId && e.RepartidorId == r.Id && e.Accion == "cargado");
        if (!enSuLista) return BadRequest(new { error = "Esta venta no esta en tu lista" });

        var v = await _db.CafeVentas
            .Where(x => x.Id == ventaId)
            .Select(x => new { x.PublicToken, x.Numero })
            .FirstOrDefaultAsync();
        if (v is null) return NotFound(new { error = "Venta no encontrada" });
        if (string.IsNullOrEmpty(v.PublicToken))
            return Ok(new { url = (string?)null, numero = v.Numero, mensaje = "Esta venta no tiene token público" });

        // 2026-06-08 v2: usar el endpoint público que GENERA el PDF al toque (no depende de Drive).
        // Así siempre funciona, incluso si la venta es nueva y todavía no se subió a Drive.
        var url = $"/api/cafe/ventas/publica/{v.PublicToken}/pdf";
        return Ok(new { url, numero = v.Numero });
    }

    // 2026-06-10: SoloCobrar=true → carga la cobranza pero NO marca esta venta como entregada
    // (sirve para "cobranza suelta" que el admin imputa a otra venta del cliente)
    // 2026-06-22: extender con datos de firma + receptor (opcional). Solo se persisten si NO es SoloCobrar.
    public record CobrarRequestV2(
        string? Pin,
        decimal Importe,
        string? Notas,
        bool SoloCobrar = false,
        string? FirmaBase64 = null,
        string? NombreReceptor = null,
        string? DniReceptor = null,
        string? MotivoSinFirma = null);

    /// <summary>Carga un cobro pendiente para esta venta.
    /// 2026-06-08: ya NO valida PIN — el cobro queda como PRE-CARGA pendiente de aprobación del admin
    /// (no toca plata real hasta que admin la aprueba en /cafe/cobranzas-pendientes). El frontend
    /// muestra "¿Confirmás?" para evitar tap accidental.
    /// Registra en QrEscaneos accion=cobrado. Marca entregado tambien.</summary>
    [HttpPost("mis-pedidos/{tokenRepartidor}/cobrar/{ventaId:int}")]
    public async Task<IActionResult> MisPedidosCobrar(string tokenRepartidor, int ventaId, [FromBody] CobrarRequestV2 req)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (r is null) return NotFound(new { error = "Enlace invalido" });

        var v = await _db.CafeVentas.FirstOrDefaultAsync(x => x.Id == ventaId);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });

        var importe = Math.Max(0m, req.Importe);
        if (importe <= 0m) return BadRequest(new { error = "Ingresá un importe mayor a 0" });

        // Verificar que sea de su lista
        var enSuLista = await _db.CafeQrEscaneos.AnyAsync(e =>
            e.VentaId == v.Id && e.RepartidorId == r.Id && e.Accion == "cargado");
        if (!enSuLista) return BadRequest(new { error = "Esta venta no esta en tu lista" });

        // Crear cobranza pendiente (admin aprueba despues)
        var pend = new CafeCobranzaPendiente
        {
            VentaId = v.Id,
            RepartidorId = r.Id,
            Importe = importe,
            // 2026-06-10: SoloCobrar → no se marca como entregado (cobranza suelta)
            MarcadoEntregado = !req.SoloCobrar,
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas!.Trim(),
            Estado = "PENDIENTE",
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeCobranzasPendientes.Add(pend);

        // Marcar entregado en la venta (solo si NO es "cobranza suelta")
        if (!req.SoloCobrar)
        {
            v.EntregadoPorRepartidorId = r.Id;
            v.EntregadoAt = DateTime.UtcNow;
            if (v.EstadoPreparacion != null)
            {
                var estadoAntCob = v.EstadoPreparacion;
                v.EstadoPreparacion = "ENTREGADO";
                v.PreparacionUpdatedAt = DateTime.UtcNow;
                // 2026-06-09 log
                _db.CafeVentaPreparacionLogs.Add(new CafeVentaPreparacionLog
                {
                    VentaId = v.Id, EstadoAnterior = estadoAntCob, EstadoNuevo = "ENTREGADO",
                    OperadorNombre = $"repartidor: {r.Nombre}",
                    Notas = $"Cobro precargado desde /mis-pedidos — importe ${importe:N2}",
                    CreatedAt = DateTime.UtcNow
                });
            }
            // 2026-06-22: datos de firma + receptor (igual que en MisPedidosEntregar)
            var firma2 = req.FirmaBase64?.Trim();
            var nombreRec2 = req.NombreReceptor?.Trim();
            var motivoSin2 = req.MotivoSinFirma?.Trim();
            if (!string.IsNullOrEmpty(firma2))
            {
                v.FirmaBase64 = firma2;
                v.EntregaFirmadaAt = DateTime.UtcNow;
            }
            if (!string.IsNullOrEmpty(nombreRec2))
            {
                v.NombreReceptor = nombreRec2.Length > 200 ? nombreRec2.Substring(0, 200) : nombreRec2;
            }
            if (!string.IsNullOrEmpty(req.DniReceptor))
            {
                v.DniReceptor = req.DniReceptor.Length > 50 ? req.DniReceptor.Substring(0, 50) : req.DniReceptor;
            }
            if (!string.IsNullOrEmpty(motivoSin2))
            {
                v.MotivoSinFirma = motivoSin2.Length > 300 ? motivoSin2.Substring(0, 300) : motivoSin2;
            }
        }

        // Log de escaneo: accion cobrado (o cobrado_suelto si no se entrego)
        _db.CafeQrEscaneos.Add(new CafeQrEscaneo
        {
            VentaId = v.Id,
            RepartidorId = r.Id,
            Accion = req.SoloCobrar ? "cobrado_suelto" : "cobrado",
            CreatedAt = DateTime.UtcNow,
            Ip = Request.HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        await _db.SaveChangesAsync();
        return Ok(new { ok = true, pendienteId = pend.Id, mensaje = $"✓ Cobro $ {importe:N2} cargado (pendiente de aprobar)" });
    }

    public record CapturarUbicacionRequest(decimal Lat, decimal Lng);

    /// <summary>2026-06-11: el repartidor parado en la puerta del cliente captura su GPS actual
    /// y lo guarda en CafeCliente.MapeoLat/Lng. Sólo guarda si el cliente AÚN no tiene coords —
    /// no pisa nada. La próxima visita ya muestra los enlaces de Maps/Waze.</summary>
    [HttpPost("mis-pedidos/{tokenRepartidor}/cliente/{clienteId:int}/capturar-ubicacion")]
    public async Task<IActionResult> CapturarUbicacionCliente(
        string tokenRepartidor, int clienteId, [FromBody] CapturarUbicacionRequest req)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (r is null) return NotFound(new { error = "Enlace invalido" });

        if (req.Lat == 0 && req.Lng == 0)
            return BadRequest(new { error = "Coordenadas vacias" });
        if (req.Lat < -90 || req.Lat > 90 || req.Lng < -180 || req.Lng > 180)
            return BadRequest(new { error = "Coordenadas fuera de rango" });

        var c = await _db.CafeClientes.FirstOrDefaultAsync(x => x.Id == clienteId);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });

        if (c.MapeoLat.HasValue && c.MapeoLng.HasValue)
            return Ok(new { ok = true, yaExistia = true, mensaje = "Este cliente ya tenia ubicacion guardada" });

        c.MapeoLat = req.Lat;
        c.MapeoLng = req.Lng;
        if (string.IsNullOrEmpty(c.MapeoLink))
            c.MapeoLink = $"https://www.google.com/maps/search/?api=1&query={req.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{req.Lng.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        await _db.SaveChangesAsync();
        return Ok(new { ok = true, yaExistia = false, mensaje = "✓ Ubicacion guardada. La proxima entrega ya va a tener el enlace de Maps." });
    }

    public record ReportarErrorUbicacionRequest(decimal? Lat, decimal? Lng);

    /// <summary>2026-06-11: el repartidor avisa que la ubicacion guardada esta mal.
    /// Si manda Lat/Lng nuevas, las REEMPLAZA. Si las manda null, simplemente BORRA las
    /// existentes (queda sin ubicacion para que el admin la corrija manualmente).
    /// A diferencia de capturar-ubicacion, este endpoint si pisa coords existentes.</summary>
    [HttpPost("mis-pedidos/{tokenRepartidor}/cliente/{clienteId:int}/reportar-error-ubicacion")]
    public async Task<IActionResult> ReportarErrorUbicacion(
        string tokenRepartidor, int clienteId, [FromBody] ReportarErrorUbicacionRequest req)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (r is null) return NotFound(new { error = "Enlace invalido" });

        var c = await _db.CafeClientes.FirstOrDefaultAsync(x => x.Id == clienteId);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });

        if (req.Lat.HasValue && req.Lng.HasValue)
        {
            if (req.Lat.Value < -90 || req.Lat.Value > 90 || req.Lng.Value < -180 || req.Lng.Value > 180)
                return BadRequest(new { error = "Coordenadas fuera de rango" });

            c.MapeoLat = req.Lat.Value;
            c.MapeoLng = req.Lng.Value;
            c.MapeoLink = $"https://www.google.com/maps/search/?api=1&query={req.Lat.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)},{req.Lng.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            await _db.SaveChangesAsync();
            return Ok(new { ok = true, reemplazado = true, mensaje = "✓ Ubicacion reemplazada por tu GPS actual." });
        }
        else
        {
            c.MapeoLat = null;
            c.MapeoLng = null;
            c.MapeoLink = null;
            await _db.SaveChangesAsync();
            return Ok(new { ok = true, reemplazado = false, mensaje = "✓ Ubicacion borrada. Avisale al admin para que la corrija." });
        }
    }

    // ============================================================
    // 2026-06-17: ME1 (MercadoLibre) en /mis-pedidos del repartidor.
    // El admin asigna desde /meli/me1/entregas, el repartidor las ve
    // como cards amarillas y marca entregado desde el celu.
    // ============================================================

    public record MisPedidosMe1Dto(
        int Id, long MeliShipmentId, long? MeliOrderId,
        string? ReceiverName, string? BuyerNickname, string? ReceiverPhone,
        string? AddressLine, string? Neighborhood, string? City, string? State, string? ZipCode,
        decimal? OrderTotal, string? ItemsSummary, string? TrackingNumber,
        string? Status, string? Substatus,
        DateTime? DateShipped, DateTime? DateDelivered,
        bool YaEntregada, DateTime? EntregadoAt
    );

    /// <summary>2026-06-17: lista las ME1 asignadas al repartidor (pendientes + las que el repartidor entrego).
    /// Misma autenticacion que /mis-pedidos: PublicToken del repartidor + repartidor activo.</summary>
    [HttpGet("mis-pedidos/{tokenRepartidor}/me1")]
    public async Task<IActionResult> MisPedidosMe1(string tokenRepartidor, [FromQuery] int dias = 14)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (r is null) return NotFound(new { error = "Enlace invalido o repartidor inactivo" });

        var desde = DateTime.UtcNow.AddDays(-Math.Max(1, dias));

        // Trae ME1 asignadas al repartidor (pendientes) + las que el repartidor marco entregadas
        // dentro del rango (para que vea su historial reciente).
        var list = await _db.MeliShipments
            .Where(s => s.Mode == "me1" &&
                ((s.RepartidorAsignadoId == r.Id && s.Status != "delivered" && s.Status != "not_delivered" && s.Status != "cancelled")
                 || (s.EntregadoPorRepartidorId == r.Id && s.EntregadoPorRepartidorAt >= desde)))
            .OrderByDescending(s => s.DateCreated ?? s.LastSyncedAt)
            .ToListAsync();

        return Ok(list.Select(s => new MisPedidosMe1Dto(
            s.Id, s.MeliShipmentId, s.MeliOrderId,
            s.ReceiverName, s.BuyerNickname, s.ReceiverPhone,
            s.AddressLine, s.Neighborhood, s.City, s.State, s.ZipCode,
            s.OrderTotal, s.ItemsSummary, s.TrackingNumber,
            s.Status, s.Substatus,
            s.DateShipped, s.DateDelivered,
            s.EntregadoPorRepartidorId.HasValue, s.EntregadoPorRepartidorAt
        )));
    }

    /// <summary>2026-06-17: marca un envio ME1 como entregado desde el celu del repartidor.
    /// Llama a la API de MeLi (irreversible). Doble confirmacion se hace en el frontend.
    /// Valida que el envio este asignado a este repartidor y que aun no este entregado.</summary>
    [HttpPost("mis-pedidos/{tokenRepartidor}/me1/{shipmentId:int}/entregar")]
    public async Task<IActionResult> EntregarMe1(string tokenRepartidor, int shipmentId)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (r is null) return NotFound(new { error = "Enlace invalido o repartidor inactivo" });

        var ship = await _db.MeliShipments.FirstOrDefaultAsync(s => s.Id == shipmentId);
        if (ship is null) return NotFound(new { error = "Envio no encontrado" });
        if (ship.RepartidorAsignadoId != r.Id) return Forbid();
        if (ship.Status == "delivered") return BadRequest(new { error = "Este envio ya esta entregado en MeLi" });

        var (ok, error) = await _me1Service.SetMe1StatusAsync(
            shipmentId, "delivered", null, null, null,
            $"Entregado por {r.Nombre} via app");
        if (!ok) return BadRequest(new { error = error ?? "No se pudo marcar entregado en MeLi" });

        ship.EntregadoPorRepartidorId = r.Id;
        ship.EntregadoPorRepartidorAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { ok = true });
    }
}
