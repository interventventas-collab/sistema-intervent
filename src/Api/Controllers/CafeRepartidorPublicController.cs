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
    private readonly TelegramService _telegram;
    private readonly WhatsAppOutboundService _wa;
    private readonly MapeoEntregasService _entregas;
    private readonly ViajesAutoService _viajes;
    public CafeRepartidorPublicController(AppDbContext db, MeliShipmentService me1Service,
        TelegramService telegram, WhatsAppOutboundService wa, MapeoEntregasService entregas,
        ViajesAutoService viajes)
    {
        _db = db; _me1Service = me1Service; _telegram = telegram; _wa = wa; _entregas = entregas;
        _viajes = viajes;
    }

    /// <summary>
    /// 2026-09-04: lo que lleva ganado hoy el repartidor que cobra POR ENTREGA (Nacho, que va con
    /// su propio auto). Devuelve null-ish (Aplica = false) para todos los demas, que cobran sueldo.
    /// </summary>
    /// <summary>Saldo = lo que se le debe DE VERDAD (todo lo ganado menos todo lo pagado, incluidos
    /// los pagos sueltos a cuenta). ImportePendiente es la suma cruda de los viajes sin cerrar: si
    /// el dueño ya le adelanto plata, los dos numeros NO coinciden y el que vale es Saldo.</summary>
    public record MisViajesDto(bool Aplica, decimal Tarifa, int ViajesHoy, decimal ImporteHoy,
        int ViajesPendientes, decimal ImportePendiente, DateTime? PendienteDesde, decimal Saldo,
        // 05/09/2026: lo que le pagaron y todavia no vio, para avisarle con un cartel.
        decimal PagosSinVer, int CantidadPagosSinVer,
        // 05/09/2026: respuestas del dueño que todavia no leyo.
        int RespuestasSinLeer);

    [HttpGet("mis-pedidos/{tokenRepartidor}/viajes")]
    public async Task<IActionResult> MisViajes(string tokenRepartidor)
    {
        var vacio = new MisViajesDto(false, 0, 0, 0, 0, 0, null, 0, 0, 0, 0);
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (r is null) return Ok(vacio);

        var driverIds = await _db.MapeoDrivers.Where(d => d.CafeRepartidorId == r.Id).Select(d => d.Id).ToListAsync();
        if (driverIds.Count == 0) return Ok(vacio);

        var emp = await _db.ViajesEmpleados.FirstOrDefaultAsync(e =>
            e.IsActive && e.ModoAutomatico && e.MapeoDriverId != null && driverIds.Contains(e.MapeoDriverId.Value));
        if (emp is null) return Ok(vacio);

        await _viajes.SincronizarAsync(emp);

        var hoy = ViajesAutoService.HoyAr();
        var ents = await _db.ViajesEntregas.Where(x => x.EmpleadoId == emp.Id).ToListAsync();
        var hoyEnts = ents.Where(x => x.Fecha == hoy).ToList();
        var pend = ents.Where(x => x.LiquidadoPagoId is null).ToList();

        var registros = await _db.ViajesRegistros.Where(r => r.EmpleadoId == emp.Id)
            .SumAsync(r => (decimal)r.CantidadCABA * r.TarifaCABA + (decimal)r.CantidadPCIA * r.TarifaPCIA);
        var pagado = await _db.ViajesPagos.Where(x => x.EmpleadoId == emp.Id).SumAsync(x => x.Importe);
        var saldo = registros + ents.Sum(x => x.Tarifa) - pagado;

        var sinVer = await _db.ViajesPagos
            .Where(x => x.EmpleadoId == emp.Id && x.VistoPorEmpleadoAt == null && x.Importe > 0)
            .ToListAsync();

        return Ok(new MisViajesDto(true, emp.TarifaViaje,
            hoyEnts.Count, hoyEnts.Sum(x => x.Tarifa),
            pend.Count, pend.Sum(x => x.Tarifa),
            pend.Count == 0 ? null : pend.Min(x => x.Fecha),
            saldo, sinVer.Sum(x => x.Importe), sinVer.Count,
            await _db.ViajesReportes.CountAsync(a => a.EmpleadoId == emp.Id
                && a.Respuesta != null && a.RespuestaVistaAt == null)));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // "Mi cuenta": el detalle completo, para que el repartidor no tenga que preguntar cuánto se
    // le debe ni por qué le bajó. Pedido del dueño el 05/09/2026.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    public record MiDiaDto(DateTime Fecha, int Viajes, decimal Importe, bool Cobrado, List<string> Donde);
    public record MiPagoDto(DateTime Fecha, decimal Importe, string Detalle, string Medio, bool EsNuevo);
    public record MiAvisoDto(DateTime Fecha, string Texto, string? Respuesta, DateTime? RespuestaAt);
    public record MiCuentaDto(bool Aplica, decimal Tarifa, decimal TotalGanado, decimal TotalCobrado,
        decimal Saldo, List<MiDiaDto> Dias, List<MiPagoDto> Pagos, List<MiAvisoDto> Avisos);

    /// <summary>Todo su historial: día por día lo que hizo, y todo lo que cobró.</summary>
    [HttpGet("mis-pedidos/{tokenRepartidor}/viajes/detalle")]
    public async Task<IActionResult> MiCuenta(string tokenRepartidor)
    {
        var emp = await EmpleadoDeViajesAsync(tokenRepartidor);
        if (emp is null) return Ok(new MiCuentaDto(false, 0, 0, 0, 0, new(), new(), new()));

        await _viajes.SincronizarAsync(emp);

        var ents = await _db.ViajesEntregas.Where(x => x.EmpleadoId == emp.Id).ToListAsync();

        var dias = ents.GroupBy(x => x.Fecha)
            .OrderByDescending(g => g.Key)
            .Select(g => new MiDiaDto(
                g.Key, g.Count(), g.Sum(x => x.Tarifa),
                g.All(x => x.LiquidadoPagoId != null),
                g.OrderBy(x => x.Id)
                 .Select(x => !string.IsNullOrWhiteSpace(x.Cliente) ? x.Cliente!
                        : (!string.IsNullOrWhiteSpace(x.Direccion) ? x.Direccion!
                        : (x.Detalle ?? "entrega")))
                 .Take(6).ToList()))
            .ToList();

        var pagos = await _db.ViajesPagos.Where(x => x.EmpleadoId == emp.Id)
            .OrderByDescending(x => x.Fecha).ThenByDescending(x => x.Id).ToListAsync();

        // Cómo le pagaron, dicho en criollo. Sale del tipo de caja de la que salió la plata.
        var tiposCaja = await _db.CafeCajas.ToDictionaryAsync(c => c.Id, c => c.Tipo);

        // Al abrir su cuenta da por vistos los pagos: el cartel de "te pagamos" deja de aparecer.
        var sinVer = pagos.Where(p => p.VistoPorEmpleadoAt == null).ToList();
        foreach (var p in sinVer) p.VistoPorEmpleadoAt = DateTime.UtcNow;
        if (sinVer.Count > 0) await _db.SaveChangesAsync();

        // Sus avisos y lo que le contestaron. Al abrirlos se dan por leídos.
        var avisos = await _db.ViajesReportes.Where(a => a.EmpleadoId == emp.Id)
            .OrderByDescending(a => a.Id).Take(20).ToListAsync();
        var respSinLeer = avisos.Where(a => a.Respuesta != null && a.RespuestaVistaAt == null).ToList();
        foreach (var a in respSinLeer) a.RespuestaVistaAt = DateTime.UtcNow;
        if (respSinLeer.Count > 0) await _db.SaveChangesAsync();

        var registros = await _db.ViajesRegistros.Where(r => r.EmpleadoId == emp.Id)
            .SumAsync(r => (decimal?)((decimal)r.CantidadCABA * r.TarifaCABA + (decimal)r.CantidadPCIA * r.TarifaPCIA)) ?? 0m;
        var ganado = registros + ents.Sum(x => x.Tarifa);
        var cobrado = pagos.Sum(x => x.Importe);

        return Ok(new MiCuentaDto(true, emp.TarifaViaje, ganado, cobrado, ganado - cobrado, dias,
            pagos.Select(p => new MiPagoDto(p.Fecha, p.Importe, p.Descripcion ?? "pago",
                MedioEnCriollo(p.CajaId, tiposCaja, p.Descripcion),
                sinVer.Any(x => x.Id == p.Id))).ToList(),
            avisos.Select(a => new MiAvisoDto(a.CreatedAt, a.Texto, a.Respuesta, a.RespuestaAt)).ToList()));
    }

    /// <summary>"en efectivo", "por transferencia"... para que el repartidor sepa cómo le pagaron.</summary>
    private static string MedioEnCriollo(int? cajaId, Dictionary<int, string> tipos, string? descripcion)
    {
        if (cajaId.HasValue && tipos.TryGetValue(cajaId.Value, out var tipo))
            return tipo switch
            {
                "EFECTIVO" => "en efectivo",
                "BANCO" => "por transferencia",
                "BILLETERA_VIRTUAL" => "por billetera virtual",
                "CHEQUES_CARTERA" => "con cheque",
                "V_PRIVADO" => "redirigido",
                _ => ""
            };
        // Los pagos viejos no dicen de dónde salieron; los redirigidos se reconocen por el texto.
        if (!string.IsNullOrWhiteSpace(descripcion) &&
            descripcion.StartsWith("Cobranza redirigida", StringComparison.OrdinalIgnoreCase))
            return "cobranza que te quedaste vos";
        return "";
    }

    public record ReportarRequest(string Texto);

    /// <summary>El repartidor avisa que algo de su cuenta no le cierra.</summary>
    [HttpPost("mis-pedidos/{tokenRepartidor}/viajes/reporte")]
    public async Task<IActionResult> Reportar(string tokenRepartidor, [FromBody] ReportarRequest req)
    {
        var emp = await EmpleadoDeViajesAsync(tokenRepartidor);
        if (emp is null) return NotFound(new { error = "No encontramos tu ficha" });
        if (string.IsNullOrWhiteSpace(req?.Texto))
            return BadRequest(new { error = "Escribí qué es lo que no te cierra" });

        var texto = req.Texto.Trim();
        if (texto.Length > 500) texto = texto[..500];

        _db.ViajesReportes.Add(new Models.ViajesReporte { EmpleadoId = emp.Id, Texto = texto });
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>La ficha de viajes del repartidor que abrió el link, o null si cobra sueldo.</summary>
    private async Task<Models.ViajesEmpleado?> EmpleadoDeViajesAsync(string tokenRepartidor)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (r is null) return null;
        var driverIds = await _db.MapeoDrivers.Where(d => d.CafeRepartidorId == r.Id).Select(d => d.Id).ToListAsync();
        if (driverIds.Count == 0) return null;
        return await _db.ViajesEmpleados.FirstOrDefaultAsync(e =>
            e.IsActive && e.ModoAutomatico && e.MapeoDriverId != null && driverIds.Contains(e.MapeoDriverId.Value));
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

        // 2026-07-27: candado anti-duplicado. Si el repartidor toca dos veces el boton
        // (doble tap) o el celular reintenta por mala señal, no queremos crear dos cobranzas
        // pendientes identicas. Chequeamos si ya hay una PENDIENTE de la misma venta, del mismo
        // repartidor, por el mismo importe, cargada en los ultimos 5 minutos. Es una ventana corta
        // a proposito: no molesta si mas tarde hay que cargar otro cobro legitimo de la misma venta.
        var hace5min = DateTime.UtcNow.AddMinutes(-5);
        var yaCargada = await _db.CafeCobranzasPendientes
            .Where(p => p.VentaId == v.Id
                        && p.RepartidorId == rep.Id
                        && p.Estado == "PENDIENTE"
                        && p.Importe == importe
                        && p.CreatedAt >= hace5min)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync();
        if (yaCargada is not null)
        {
            // Igual reflejamos la entrega en la venta por si el primer intento no la marco.
            if (marcoEntregado && !v.EntregadoPorRepartidorId.HasValue)
            {
                v.EntregadoPorRepartidorId = rep.Id;
                v.EntregadoAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
            return Ok(new { id = yaCargada.Id, duplicada = true, mensaje = "✓ Ya la habias cargado recien — no la dupliqué" });
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
        string? ClienteMapeoLink,
        // 2026-06-22: si true, el repartidor tiene que pedir firma + nombre al receptor al entregar.
        // El modal de confirmacion se enriquece con canvas de firma + input nombre + opcion "Saltar con motivo".
        bool SolicitarFirmaEntrega = false);

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
                // 2026-07-02: si la venta tiene su propio link de Maps (override), gana sobre el del
                // cliente — y anulamos las coords del cliente para que ese link no sea pisado por el pin fijo.
                ClienteLat = (x.v.MapeoLink != null && x.v.MapeoLink != "") ? (decimal?)null : (x.c != null ? x.c.MapeoLat : null),
                ClienteLng = (x.v.MapeoLink != null && x.v.MapeoLink != "") ? (decimal?)null : (x.c != null ? x.c.MapeoLng : null),
                ClienteMapeoLink = (x.v.MapeoLink != null && x.v.MapeoLink != "") ? x.v.MapeoLink : (x.c != null ? x.c.MapeoLink : null),
                // 2026-06-22: flag para mostrar modal de firma al confirmar entrega
                x.v.SolicitarFirmaEntrega
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

        // 2026-08-12: NO filtramos por la tabla de rechazos acá. Al rechazar ya se borra el escaneo
        // "cargado" (desvincula), así que la venta sale sola de la lista. Si el admin se la REASIGNA,
        // se crea un "cargado" nuevo y tiene que volver a aparecer — filtrar por rechazos la escondería
        // para siempre aunque se la reasignen (bug 2026-08-12).

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
            x.ClienteId, x.ClienteTelefono, x.ClienteLat, x.ClienteLng, x.ClienteMapeoLink,
            x.SolicitarFirmaEntrega
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

    /// <summary>2026-08-13: el repartidor SOLO AVISA que la ubicacion esta mal. Ya NO la modifica
    /// ni la borra (eso no funcionaba en la calle). Dispara el aviso "UBICACION_ERRONEA" de Mis Alertas
    /// (campanita/Telegram/WhatsApp) para que la corrijan desde el sistema (armado de ruta). Si el celu
    /// mando Lat/Lng, van SOLO como pista dentro del aviso; nunca pisan la ubicacion guardada.</summary>
    [HttpPost("mis-pedidos/{tokenRepartidor}/cliente/{clienteId:int}/reportar-error-ubicacion")]
    public async Task<IActionResult> ReportarErrorUbicacion(
        string tokenRepartidor, int clienteId, [FromBody] ReportarErrorUbicacionRequest req)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (r is null) return NotFound(new { error = "Enlace invalido" });

        var c = await _db.CafeClientes.FirstOrDefaultAsync(x => x.Id == clienteId);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });

        // Pista opcional: donde estaba parado el repartidor. NO se guarda como ubicacion del cliente.
        string? hint = null;
        if (req.Lat.HasValue && req.Lng.HasValue &&
            req.Lat.Value >= -90 && req.Lat.Value <= 90 && req.Lng.Value >= -180 && req.Lng.Value <= 180)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            hint = $"https://www.google.com/maps/search/?api=1&query={req.Lat.Value.ToString(ci)},{req.Lng.Value.ToString(ci)}";
        }

        var direccion = !string.IsNullOrWhiteSpace(c.Direccion) ? c.Direccion! : "(sin direccion cargada)";
        await NotificarUbicacionErroneaAsync(r.Nombre, c.Nombre, direccion, hint);
        return Ok(new { ok = true, reemplazado = false, mensaje = "✓ Listo, avisamos a la oficina. Van a corregir la ubicacion." });
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

    // ── 2026-07-27: Ruta del Mapeo (Flex/paradas) en SOLO-LECTURA para el teléfono del repartidor ──
    public record MisPedidosMapeoDto(
        int Id, int? OrderInRoute, string Origin, string? OriginRefId,
        string? Nombre, string Direccion, string? Localidad,
        decimal Latitude, decimal Longitude, string? Telefono,
        string? Comprador, long? NumeroVenta,
        bool Entregado, DateTime? DateDelivered,
        // 2026-09-02: cerrada SIN entregar (cancelada, "no encontró", o MeLi avisó que no se
        // entregó). No cuenta como entregada, pero tampoco le queda pendiente.
        bool NoEntregada = false,
        // 2026-09-02: el repartidor la tildó a mano en su lista. Es SOLO visual — sirve para que la
        // lista avance a la parada siguiente. No toca MercadoLibre, no genera cobranza y no cuenta
        // como entrega en ningún número. Se usa sobre todo en los Flex, que se cierran en la app de
        // MeLi y tardan en confirmarse.
        bool Visto = false,
        // 2026-09-02: lo que el comprador escribió del domicilio ("entregar de 8 a 17 hs", "no hay
        // timbre"). Ya lo guardábamos y no se veía en ningún lado: el repartidor lo necesita ANTES
        // de bajarse. Si la parada se marcó como no entregada, el motivo va adelante con "⚠ ... · ".
        string? Notas = null,
        // Domicilio COMERCIAL segun MercadoLibre (lo que la etiqueta imprime asi).
        bool EsComercial = false
    );

    /// <summary>Paradas del Mapeo asignadas a este repartidor (vía MapeoDrivers.CafeRepartidorId),
    /// en orden de ruta. SOLO LECTURA: el repartidor ve su recorrido; NO marca entregado acá
    /// (los Flex se cierran por la app de Flex de MeLi). No genera cobranzas ni toca el rinde.</summary>
    [HttpGet("mis-pedidos/{tokenRepartidor}/mapeo")]
    public async Task<IActionResult> MisPedidosMapeo(string tokenRepartidor)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (r is null) return NotFound(new { error = "Enlace invalido o repartidor inactivo" });

        // MapeoDrivers vinculados a este repartidor real.
        var driverIds = await _db.MapeoDrivers.Where(d => d.CafeRepartidorId == r.Id).Select(d => d.Id).ToListAsync();
        if (driverIds.Count == 0) return Ok(new List<MisPedidosMapeoDto>());

        // ⚠ 2026-09-03 REGLA DE ORO: al repartidor le llega SOLO el día de hoy. En el mapa se puede
        // estar armando el reparto de mañana o del sábado, con zonas y choferes ya asignados —
        // nada de eso tiene que aparecerle en el celular hasta que llegue ese día.
        var hoyAr = DateTime.UtcNow.AddHours(-3).Date;
        var stops = await _db.MapeoStops
            .Where(s => s.AssignedDriverId != null && driverIds.Contains(s.AssignedDriverId.Value)
                     && s.FechaReparto == hoyAr)
            .OrderBy(s => s.OrderInRoute ?? int.MaxValue).ThenBy(s => s.Id)
            .ToListAsync();

        // Enriquecer Flex/ME1 con datos del envío de MeLi (comprador, nº venta, entregado).
        var refs = stops.Where(s => (s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId != null)
                        .Select(s => long.TryParse(s.OriginRefId, out var v) ? v : 0L).Where(v => v != 0L).Distinct().ToList();
        var ships = refs.Count == 0
            ? new Dictionary<long, MeliShipment>()
            : await _db.MeliShipments.Where(m => refs.Contains(m.MeliShipmentId)).ToDictionaryAsync(m => m.MeliShipmentId);

        // ¿Ya se entregó? Lo contesta el mismo servicio que usan el mapa y el dashboard, así los tres
        // dicen lo mismo — y vale para TODOS los orígenes, no solo los de MeLi. 2026-09-02
        var entregasPorStop = await _entregas.EntregasAsync(stops);
        var noEntregadas = await _entregas.NoEntregadasAsync(stops);
        var vistos = await LeerVistosAsync(r.Id);

        var result = stops.Select(s =>
        {
            string? comprador = null; long? numeroVenta = null;
            entregasPorStop.TryGetValue(s.Id, out var entregadaAt);
            bool entregado = entregadaAt.HasValue;
            DateTime? dd = entregadaAt;
            if ((s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId != null
                && long.TryParse(s.OriginRefId, out var sid) && ships.TryGetValue(sid, out var m))
            {
                comprador = m.BuyerNickname;
                numeroVenta = m.MeliOrderId;
            }
            return new MisPedidosMapeoDto(
                s.Id, s.OrderInRoute, s.Origin, s.OriginRefId,
                s.Alias ?? s.ContactName, s.Direccion, s.Localidad,
                s.Latitude, s.Longitude, s.Telefono,
                comprador, numeroVenta, entregado, dd,
                !entregado && noEntregadas.Contains(s.Id),
                vistos.Contains(s.Id),
                s.Notas,
                (s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId != null
                    && long.TryParse(s.OriginRefId, out var sid2) && ships.TryGetValue(sid2, out var m2)
                    && string.Equals(m2.DeliveryPreference, "business", StringComparison.OrdinalIgnoreCase));
        }).ToList();

        return Ok(result);
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // 2026-09-02: TILDE VISUAL de la lista del repartidor.
    //
    // Los Flex se cierran en la app de MercadoLibre, no acá, y MeLi tarda en confirmarlos. Sin esto
    // el envío ya entregado se le queda arriba de la lista sin tildar mientras él va tres cuadras
    // más adelante, y la lista deja de servirle para ubicarse.
    //
    // El tilde es SOLO visual: hace avanzar su lista y nada más. No toca MercadoLibre, no marca
    // entregado, no genera cobranza y NO cuenta como entrega en ningún número del sistema. Cuando
    // MeLi después confirma de verdad, el renglón pasa solo a entregado real.
    //
    // Se guarda en AppSettings (una fila por repartidor y por día) para no tocar el esquema de la
    // base: agregar una columna obligaría a correr un ALTER a mano en producción. Es el mismo
    // criterio que ya usan los colores de zona del mapa y los chats de Depósito.
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>Clave del día para ese repartidor. La fecha va en hora argentina: la lista es "la de hoy".</summary>
    private static string VistosKey(int repartidorId)
        => $"mapeo.visto.rep{repartidorId}.{DateTime.UtcNow.AddHours(-3):yyyy-MM-dd}";

    private async Task<HashSet<int>> LeerVistosAsync(int repartidorId)
    {
        var s = await _db.AppSettings.FindAsync(VistosKey(repartidorId));
        if (s is null || string.IsNullOrWhiteSpace(s.Value)) return new HashSet<int>();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<int>>(s.Value)?.ToHashSet()
                   ?? new HashSet<int>();
        }
        catch { return new HashSet<int>(); }   // dato raro: arrancamos limpio, no rompemos la pantalla
    }

    public record NoEntregadaRequest(string Motivo);

    /// <summary>
    /// 2026-09-02: "FUI Y NO SE PUDO". El repartidor pasó por el domicilio y no pudo entregar.
    ///
    /// Ojo con la diferencia, que es la que se nos escapó y dejó una ruta abierta toda la noche:
    ///   · RECHAZAR  = "esta no me la llevo" → la parada le sale de la ruta y vuelve al pool.
    ///   · ACÁ       = "fui y no se pudo"    → la parada LE QUEDA, marcada como fallida, porque se
    ///                                          gastó el viaje y eso tiene que verse en el día.
    ///
    /// Cierra la parada (deja de contar como pendiente, así su recorrido puede terminar) pero NO es
    /// una entrega: en el mapa sale con la cruz roja y el motivo. Es todo INTERNO — no le escribe
    /// nada a MercadoLibre; el Flex se sigue cerrando en la app de ellos.
    /// </summary>
    [HttpPost("mis-pedidos/{tokenRepartidor}/no-entregada/{stopId:int}")]
    public async Task<IActionResult> MarcarNoEntregada(string tokenRepartidor, int stopId, [FromBody] NoEntregadaRequest req)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (r is null) return NotFound(new { error = "Enlace invalido o repartidor inactivo" });

        var motivo = (req?.Motivo ?? "").Trim();
        if (motivo.Length == 0) return BadRequest(new { error = "Decinos por qué no se pudo entregar" });
        if (motivo.Length > 200) motivo = motivo.Substring(0, 200);

        var driverIds = await _db.MapeoDrivers.Where(d => d.CafeRepartidorId == r.Id).Select(d => d.Id).ToListAsync();
        var stop = await _db.MapeoStops.FirstOrDefaultAsync(s => s.Id == stopId);
        if (stop is null) return NotFound(new { error = "Parada no encontrada" });
        if (stop.AssignedDriverId == null || !driverIds.Contains(stop.AssignedDriverId.Value))
            return BadRequest(new { error = "Esa parada no es tuya" });

        stop.InternalStatus = "no_encontrado";
        // El motivo va adelante de las notas para que se lea primero en el globito del mapa,
        // sin borrar lo que ya hubiera anotado (nº de venta, "tocar timbre 3 veces", etc).
        var marca = $"⚠ No se pudo entregar: {motivo} ({r.Nombre})";
        stop.Notas = string.IsNullOrWhiteSpace(stop.Notas) ? marca : $"{marca} · {stop.Notas}";
        if (stop.Notas.Length > 500) stop.Notas = stop.Notas.Substring(0, 500);
        stop.UpdatedAt = DateTime.UtcNow;

        // Si la había tildado a mano, ese tilde ya no hace falta.
        var vistos = await LeerVistosAsync(r.Id);
        if (vistos.Remove(stopId))
        {
            var k = VistosKey(r.Id);
            var setting = await _db.AppSettings.FindAsync(k);
            if (setting is not null)
            {
                setting.Value = System.Text.Json.JsonSerializer.Serialize(vistos.OrderBy(x => x).ToList());
                setting.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync();

        var descripcion = (stop.Alias ?? stop.ContactName ?? "Parada") +
            (string.IsNullOrWhiteSpace(stop.Direccion) ? "" : $" · {stop.Direccion}") +
            (string.IsNullOrWhiteSpace(stop.Localidad) ? "" : $" · {stop.Localidad}");
        await NotificarEnvioRechazadoAsync(r.Nombre, descripcion, motivo, noPudoEntregar: true);

        return Ok(new { ok = true, motivo });
    }

    public record VistoRequest(bool Visto);

    /// <summary>Tilda (o destilda) una parada en la lista del repartidor. Solo visual — ver el bloque
    /// de arriba. Valida que la parada sea de él, igual que el resto de la pantalla.</summary>
    [HttpPost("mis-pedidos/{tokenRepartidor}/visto/{stopId:int}")]
    public async Task<IActionResult> MarcarVisto(string tokenRepartidor, int stopId, [FromBody] VistoRequest req)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (r is null) return NotFound(new { error = "Enlace invalido o repartidor inactivo" });

        var driverIds = await _db.MapeoDrivers.Where(d => d.CafeRepartidorId == r.Id).Select(d => d.Id).ToListAsync();
        var stop = await _db.MapeoStops.FirstOrDefaultAsync(s => s.Id == stopId);
        if (stop is null) return NotFound(new { error = "Parada no encontrada" });
        if (stop.AssignedDriverId == null || !driverIds.Contains(stop.AssignedDriverId.Value))
            return BadRequest(new { error = "Esa parada no es tuya" });

        // 2026-09-02: las paradas SUELTAS (las que se cargan a mano en el mapa, sin venta ni envío
        // atrás) no las confirma nadie más: no hay factura, ni MercadoLibre, ni otro circuito que
        // las cierre. Para esas el tilde del repartidor ES la entrega, y la oficina la ve entregada
        // en el mapa. Para todo el resto (ventas, alquileres, Flex, ME1, visitas) el tilde sigue
        // siendo solo visual, porque cada una tiene su propia forma de cerrarse de verdad.
        var esParadaSuelta = stop.Origin is "manual" or "favorito";
        if (esParadaSuelta)
        {
            stop.InternalStatus = req.Visto ? "entregado" : "pending";
            stop.UpdatedAt = DateTime.UtcNow;
            var vistosSuelta = await LeerVistosAsync(r.Id);
            if (vistosSuelta.Remove(stopId))   // si venía tildada a mano, ya no hace falta
            {
                var k = VistosKey(r.Id);
                var st2 = await _db.AppSettings.FindAsync(k);
                if (st2 is not null) st2.Value = System.Text.Json.JsonSerializer.Serialize(vistosSuelta.OrderBy(x => x).ToList());
            }
            await _db.SaveChangesAsync();
            return Ok(new { ok = true, visto = req.Visto, entregada = req.Visto });
        }

        var vistos = await LeerVistosAsync(r.Id);
        if (req.Visto) vistos.Add(stopId); else vistos.Remove(stopId);

        var key = VistosKey(r.Id);
        var valor = System.Text.Json.JsonSerializer.Serialize(vistos.OrderBy(x => x).ToList());
        var setting = await _db.AppSettings.FindAsync(key);
        if (setting is null) _db.AppSettings.Add(new AppSetting { Key = key, Value = valor, UpdatedAt = DateTime.UtcNow });
        else { setting.Value = valor; setting.UpdatedAt = DateTime.UtcNow; }

        // Limpieza: los tildes son del día. Borramos los de más de una semana para no acumular filas.
        var corte = $"mapeo.visto.rep{r.Id}.{DateTime.UtcNow.AddHours(-3).AddDays(-7):yyyy-MM-dd}";
        var viejos = await _db.AppSettings
            .Where(x => x.Key.StartsWith($"mapeo.visto.rep{r.Id}.") && string.Compare(x.Key, corte) < 0)
            .ToListAsync();
        if (viejos.Count > 0) _db.AppSettings.RemoveRange(viejos);

        await _db.SaveChangesAsync();
        return Ok(new { ok = true, visto = req.Visto, entregada = false });
    }

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

    // ─────────────────────────── RECHAZAR ENVÍO (2026-08-12) ───────────────────────────

    /// <summary>Cuerpo del rechazo: qué envío y por qué. El motivo es OBLIGATORIO.</summary>
    public record RechazarRequest(string Origen, int ReferenciaId, string? Motivo);

    /// <summary>2026-08-12: el repartidor RECHAZA un envío que le asignaron, desde el celu.
    /// Sirve para los 3 orígenes que ve en /mis-pedidos:
    ///   - "venta_cafe": venta de café (Cafe_Ventas.Id) — validada por QrEscaneos "cargado".
    ///   - "me1": Flex/ME1 de MercadoLibre (MeliShipments.Id) — validada por RepartidorAsignadoId.
    ///   - "mapeo": parada de la ruta (MapeoStops.Id) — validada por MapeoDrivers.CafeRepartidorId.
    /// Guarda el motivo (obligatorio), saca el envío de su lista (las 3 listas excluyen lo rechazado)
    /// y dispara el aviso "ENVIO_RECHAZADO" al dueño (campanita/Telegram/WhatsApp, según Mis Alertas).
    /// NO se pierde: el admin lo puede reasignar. Auth: PublicToken del repartidor.</summary>
    [HttpPost("mis-pedidos/{tokenRepartidor}/rechazar")]
    public async Task<IActionResult> RechazarEnvio(string tokenRepartidor, [FromBody] RechazarRequest req)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.PublicToken == tokenRepartidor && x.IsActive);
        if (r is null) return NotFound(new { error = "Enlace invalido o repartidor inactivo" });

        var motivo = req?.Motivo?.Trim();
        if (string.IsNullOrWhiteSpace(motivo))
            return BadRequest(new { error = "Escribí el motivo del rechazo" });
        if (motivo.Length > 500) motivo = motivo.Substring(0, 500);

        var origen = (req?.Origen ?? "").Trim().ToLowerInvariant();
        var refId = req?.ReferenciaId ?? 0;
        if (refId <= 0) return BadRequest(new { error = "Envío inválido" });

        string descripcion;

        switch (origen)
        {
            case "venta_cafe":
            {
                var v = await _db.CafeVentas.FirstOrDefaultAsync(x => x.Id == refId);
                if (v is null) return NotFound(new { error = "Venta no encontrada" });
                var enSuLista = await _db.CafeQrEscaneos.AnyAsync(e =>
                    e.VentaId == v.Id && e.RepartidorId == r.Id && e.Accion == "cargado");
                if (!enSuLista) return BadRequest(new { error = "Esta venta no esta en tu lista" });
                descripcion = $"Venta {v.Numero}" +
                    (string.IsNullOrWhiteSpace(v.ClienteNombreSnapshot) ? "" : $" · {v.ClienteNombreSnapshot}") +
                    (string.IsNullOrWhiteSpace(v.ClienteLocalidadSnapshot) ? "" : $" · {v.ClienteLocalidadSnapshot}");
                // Desvincular: borrar los escaneos "cargado" de esta venta, así el listado del admin
                // deja de mostrar "Lo tiene {repartidor}" y pasa a "Asignar" (mismo criterio que
                // reasignar/desvincular a mano). El chip "🚫 Rechazó {repartidor}" lo pone la fila de rechazo.
                var escaneosCargado = await _db.CafeQrEscaneos
                    .Where(e => e.VentaId == v.Id && e.Accion == "cargado").ToListAsync();
                _db.CafeQrEscaneos.RemoveRange(escaneosCargado);
                // 2026-09-02: sacarla TAMBIÉN de su ruta en el mapa. Antes la venta se le iba de la
                // lista pero la parada le quedaba asignada, así que le contaba como pendiente para
                // siempre y su recorrido nunca figuraba terminado. Mismo criterio que rechazar una
                // parada del mapa: vuelve al pool para que la oficina se la dé a otro.
                var refVenta = v.Id.ToString();
                var stopVenta = await _db.MapeoStops
                    .FirstOrDefaultAsync(s => s.Origin == "venta_cafe" && s.OriginRefId == refVenta);
                if (stopVenta is not null && stopVenta.AssignedDriverId.HasValue)
                {
                    stopVenta.AssignedDriverId = null;
                    stopVenta.OrderInRoute = null;
                    stopVenta.UpdatedAt = DateTime.UtcNow;
                }
                break;
            }
            case "me1":
            {
                var ship = await _db.MeliShipments.FirstOrDefaultAsync(s => s.Id == refId);
                if (ship is null) return NotFound(new { error = "Envío no encontrado" });
                if (ship.RepartidorAsignadoId != r.Id) return BadRequest(new { error = "Este envío no está asignado a vos" });
                descripcion = $"MeLi {(ship.MeliOrderId?.ToString() ?? "")}".Trim() +
                    (string.IsNullOrWhiteSpace(ship.ReceiverName) ? "" : $" · {ship.ReceiverName}") +
                    (string.IsNullOrWhiteSpace(ship.AddressLine) ? "" : $" · {ship.AddressLine}");
                // Sacarlo de la lista del repartidor: vuelve al pool para reasignar.
                ship.RepartidorAsignadoId = null;
                break;
            }
            case "mapeo":
            {
                var stop = await _db.MapeoStops.FirstOrDefaultAsync(s => s.Id == refId);
                if (stop is null) return NotFound(new { error = "Parada no encontrada" });
                var driverIds = await _db.MapeoDrivers.Where(d => d.CafeRepartidorId == r.Id).Select(d => d.Id).ToListAsync();
                if (stop.AssignedDriverId == null || !driverIds.Contains(stop.AssignedDriverId.Value))
                    return BadRequest(new { error = "Esta parada no está asignada a vos" });
                descripcion = (stop.Alias ?? stop.ContactName ?? "Parada") +
                    (string.IsNullOrWhiteSpace(stop.Direccion) ? "" : $" · {stop.Direccion}") +
                    (string.IsNullOrWhiteSpace(stop.Localidad) ? "" : $" · {stop.Localidad}");
                // Sacarla de la ruta del repartidor: vuelve al pool para reasignar.
                stop.AssignedDriverId = null;
                break;
            }
            default:
                return BadRequest(new { error = "Origen de envío desconocido" });
        }

        if (descripcion.Length > 300) descripcion = descripcion.Substring(0, 300);

        _db.CafeRepartidorRechazos.Add(new CafeRepartidorRechazo
        {
            RepartidorId = r.Id,
            Origen = origen,
            ReferenciaId = refId,
            Motivo = motivo,
            Descripcion = descripcion,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        await NotificarEnvioRechazadoAsync(r.Nombre, descripcion, motivo);

        return Ok(new { ok = true, mensaje = "Envío rechazado. Ya avisamos." });
    }

    /// <summary>2026-08-12: dispara el aviso "ENVIO_RECHAZADO" de Mis Alertas cuando un repartidor
    /// rechaza un envío. Respeta los canales/destinatarios configurados en Automatizaciones y Alertas
    /// (campanita 🔔 / Telegram 📲 / WhatsApp 📱). Mismo mecanismo que FICHADA / ALTA_CLIENTE.
    /// Nunca rompe el rechazo si un canal falla.</summary>
    private async Task NotificarEnvioRechazadoAsync(string repartidorNombre, string descripcion, string motivo,
        bool noPudoEntregar = false)
    {
        // 2026-09-02: el mismo aviso sirve para los dos casos, con distinto texto — así el usuario
        // no tiene que configurar una alerta nueva (y no hay que sembrar una fila a mano en prod).
        var titulo = noPudoEntregar ? "Un repartidor no pudo entregar" : "Un repartidor rechazó un envío";
        var icono = noPudoEntregar ? "⚠" : "🚫";
        var verbo = noPudoEntregar ? "no pudo entregar" : "rechazó";
        try
        {
            var alerta = await _db.MisAlertas.FirstOrDefaultAsync(x => x.Tipo == "ENVIO_RECHAZADO");
            if (alerta is null || !alerta.Activa) return;
            if (!alerta.CanalCampanita && !alerta.CanalTelegram && !alerta.CanalWhatsApp) return;

            var detalleCorto = $"{repartidorNombre} {verbo}: {descripcion} — motivo: {motivo}";
            var texto = $"{icono} <b>{titulo}</b>\n" +
                        $"🧑 {repartidorNombre}\n" +
                        $"📦 {descripcion}\n" +
                        $"📝 Motivo: {motivo}";

            // Telegram (si está tildado).
            bool enviadoTg = false;
            if (alerta.CanalTelegram)
            {
                var (ok, _) = await _telegram.SendMessageAsync(texto, categoria: "ALERTAS");
                enviadoTg = ok;
            }

            // WhatsApp (si está tildado): a las personas tildadas para esta alerta, por la línea elegida.
            if (alerta.CanalWhatsApp)
            {
                var idsDest = await _db.AutoDestinatarios.Where(d => d.AutoKey == $"alerta:{alerta.Id}")
                    .Select(d => d.PersonaId).ToListAsync();
                var personas = await _db.AutoPersonas
                    .Where(p => p.Activo && idsDest.Contains(p.Id) && p.WhatsAppNumero != null).ToListAsync();
                var textoWa = $"{icono} {titulo}\n🧑 {repartidorNombre}\n📦 {descripcion}\n📝 Motivo: {motivo}";
                foreach (var per in personas)
                {
                    try
                    {
                        var num = per.WhatsAppNumero!.StartsWith("whatsapp:") ? per.WhatsAppNumero : "whatsapp:" + per.WhatsAppNumero;
                        var (sid, canal, lin) = await _wa.SendTextAsync(num, textoWa, lineaOverride: alerta.LineaPhoneId);
                        if (sid != null)
                            _db.WhatsAppTwilioMensajes.Add(new WhatsAppTwilioMensaje
                            {
                                Direccion = "OUTGOING", Numero = num, Cuerpo = textoWa,
                                TwilioMessageSid = sid, Canal = canal, LineaPhoneId = lin, Procesado = true, CreatedAt = DateTime.UtcNow
                            });
                    }
                    catch { /* seguir con el resto */ }
                }
            }

            // Campanita (si está tildada): queda encendida hasta que la mirás.
            if (alerta.CanalCampanita)
            {
                alerta.EstaDisparada = true;
                alerta.Vista = false;
                alerta.DisparadaAt = DateTime.UtcNow;
                alerta.UltimoDetalle = detalleCorto;
                alerta.UpdatedAt = DateTime.UtcNow;
            }

            // Historial: una fila por rechazo.
            _db.MisAlertasHistorial.Add(new MisAlertaHistorial
            {
                AlertaId = alerta.Id,
                Tipo = "ENVIO_RECHAZADO",
                Mensaje = string.IsNullOrWhiteSpace(alerta.Mensaje) ? titulo : alerta.Mensaje,
                Detalle = detalleCorto,
                Alcance = string.IsNullOrWhiteSpace(alerta.Alcance) ? "admin,oficina" : alerta.Alcance,
                PorTelegram = alerta.CanalTelegram,
                EnviadoTelegram = enviadoTg
            });
            await _db.SaveChangesAsync();
        }
        catch { /* nunca romper el rechazo por un aviso */ }
    }

    /// <summary>2026-08-13: dispara el aviso "UBICACION_ERRONEA" de Mis Alertas cuando un repartidor
    /// reporta que la ubicación de un cliente está mal. NO modifica la ubicación: solo avisa para que
    /// la corrijan desde el sistema (armado de ruta). Mismos canales que ENVIO_RECHAZADO. Nunca rompe.</summary>
    private async Task NotificarUbicacionErroneaAsync(string repartidorNombre, string clienteNombre, string direccion, string? hintLink)
    {
        try
        {
            var alerta = await _db.MisAlertas.FirstOrDefaultAsync(x => x.Tipo == "UBICACION_ERRONEA");
            if (alerta is null || !alerta.Activa) return;
            if (!alerta.CanalCampanita && !alerta.CanalTelegram && !alerta.CanalWhatsApp) return;

            var detalleCorto = $"{repartidorNombre} reportó mal la ubicación de {clienteNombre} ({direccion})";
            var hintLine = string.IsNullOrWhiteSpace(hintLink) ? "" : $"\n📌 Estaba parado acá: {hintLink}";
            var texto = $"📍 <b>Ubicación mal cargada — corregir desde el sistema</b>\n" +
                        $"🧑 Reportó: {repartidorNombre}\n" +
                        $"👤 Cliente: {clienteNombre}\n" +
                        $"🏠 {direccion}{hintLine}";

            // Telegram (si está tildado).
            bool enviadoTg = false;
            if (alerta.CanalTelegram)
            {
                var (ok, _) = await _telegram.SendMessageAsync(texto, categoria: "ALERTAS");
                enviadoTg = ok;
            }

            // WhatsApp (si está tildado): a las personas tildadas para esta alerta, por la línea elegida.
            if (alerta.CanalWhatsApp)
            {
                var idsDest = await _db.AutoDestinatarios.Where(d => d.AutoKey == $"alerta:{alerta.Id}")
                    .Select(d => d.PersonaId).ToListAsync();
                var personas = await _db.AutoPersonas
                    .Where(p => p.Activo && idsDest.Contains(p.Id) && p.WhatsAppNumero != null).ToListAsync();
                var textoWa = $"📍 Ubicación mal cargada — corregir desde el sistema\n👤 Cliente: {clienteNombre}\n🏠 {direccion}\n🧑 Reportó: {repartidorNombre}"
                            + (string.IsNullOrWhiteSpace(hintLink) ? "" : $"\n📌 {hintLink}");
                foreach (var per in personas)
                {
                    try
                    {
                        var num = per.WhatsAppNumero!.StartsWith("whatsapp:") ? per.WhatsAppNumero : "whatsapp:" + per.WhatsAppNumero;
                        var (sid, canal, lin) = await _wa.SendTextAsync(num, textoWa, lineaOverride: alerta.LineaPhoneId);
                        if (sid != null)
                            _db.WhatsAppTwilioMensajes.Add(new WhatsAppTwilioMensaje
                            {
                                Direccion = "OUTGOING", Numero = num, Cuerpo = textoWa,
                                TwilioMessageSid = sid, Canal = canal, LineaPhoneId = lin, Procesado = true, CreatedAt = DateTime.UtcNow
                            });
                    }
                    catch { /* seguir con el resto */ }
                }
            }

            // Campanita (si está tildada): queda encendida hasta que la mirás.
            if (alerta.CanalCampanita)
            {
                alerta.EstaDisparada = true;
                alerta.Vista = false;
                alerta.DisparadaAt = DateTime.UtcNow;
                alerta.UltimoDetalle = detalleCorto;
                alerta.UpdatedAt = DateTime.UtcNow;
            }

            // Historial: una fila por reporte.
            _db.MisAlertasHistorial.Add(new MisAlertaHistorial
            {
                AlertaId = alerta.Id,
                Tipo = "UBICACION_ERRONEA",
                Mensaje = string.IsNullOrWhiteSpace(alerta.Mensaje) ? "Repartidor reportó una ubicación mal cargada" : alerta.Mensaje,
                Detalle = detalleCorto,
                Alcance = string.IsNullOrWhiteSpace(alerta.Alcance) ? "admin,oficina" : alerta.Alcance,
                PorTelegram = alerta.CanalTelegram,
                EnviadoTelegram = enviadoTg
            });
            await _db.SaveChangesAsync();
        }
        catch { /* nunca romper el reporte por un aviso */ }
    }
}
