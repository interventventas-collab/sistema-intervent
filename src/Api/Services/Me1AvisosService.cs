using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-11-18: avisos de estado de los envíos ME1 a MercadoLibre (Métricas → Envíos → Envíos ME1).
/// MeLi pide como obligatorios "En camino" y "Salió a distribución" además de "Entregado"; nosotros solo
/// mandábamos "Entregado" y la Experiencia de envío daba 0%.
///
///   - Al imprimir la etiqueta se pregunta "¿Sale hoy?": Sí → "En camino" en el acto; otro día → se guarda
///     Me1SaleFecha y ese día a la mañana (desde las 7 AR) el job lo avisa.
///   - En el mapa de HOY también se pregunta "¿Sale hoy?" (2026-11-20: a conciencia, el mapa solo no avisa).
///     Ya confirmado y con chofer asignado → "Salió a distribución".
///   - Repartidor con ME1 sin marcar → se los mostramos al fichar la salida (Pendientes).
/// </summary>
public class Me1AvisosService
{
    private readonly AppDbContext _db;
    private readonly MeliShipmentService _ship;
    private readonly ILogger<Me1AvisosService> _log;

    public Me1AvisosService(AppDbContext db, MeliShipmentService ship, ILogger<Me1AvisosService> log)
    {
        _db = db; _ship = ship; _log = log;
    }

    public static DateTime HoyAr() => DateTime.UtcNow.AddHours(-3).Date;

    private static readonly string[] Finales = { "delivered", "not_delivered", "cancelled" };

    public record Me1SinSalidaDto(long ShipmentId, string? Nombre, string? Localidad, string? Productos);

    /// <summary>De los envíos recién impresos, los ME1 que todavía no salieron y no tienen día de salida
    /// elegido: a esos se les pregunta "¿Sale hoy?".</summary>
    public async Task<List<Me1SinSalidaDto>> SinDiaDeSalidaAsync(IEnumerable<long> meliShipmentIds)
    {
        var ids = meliShipmentIds.Distinct().ToList();
        if (ids.Count == 0) return new();
        var lista = await _db.MeliShipments.AsNoTracking()
            .Where(s => ids.Contains(s.MeliShipmentId) && s.Mode == "me1"
                        && (s.Status == "ready_to_ship" || s.Status == "handling")
                        && s.Me1SaleFecha == null)
            .Select(s => new { s.MeliShipmentId, s.ReceiverName, s.BuyerNickname, s.City, s.ItemsSummary })
            .ToListAsync();
        return lista.Select(s => new Me1SinSalidaDto(s.MeliShipmentId, s.ReceiverName ?? s.BuyerNickname, s.City, s.ItemsSummary)).ToList();
    }

    /// <summary>Guarda el día en que salen. Si es hoy (o antes), avisa "En camino" ya mismo.
    /// Devuelve cuántos se avisaron y los errores de MeLi, si hubo.</summary>
    public async Task<(int avisados, int programados, List<string> errores)> ElegirDiaSalidaAsync(IEnumerable<long> meliShipmentIds, DateTime fecha)
    {
        var hoy = HoyAr();
        fecha = fecha.Date;
        var ids = meliShipmentIds.Distinct().ToList();
        var envios = await _db.MeliShipments
            .Where(s => ids.Contains(s.MeliShipmentId) && s.Mode == "me1")
            .ToListAsync();
        int avisados = 0, programados = 0;
        var errores = new List<string>();
        foreach (var s in envios)
        {
            s.Me1SaleFecha = fecha;
            await _db.SaveChangesAsync();
            if (fecha > hoy) { programados++; continue; }
            if (!MeliShipmentService.Me1SinDespachar(s.Status)) continue;
            var (ok, err) = await _ship.SetMe1StatusAsync(s.Id, "shipped", null, null, null, "Despachado");
            if (ok) avisados++;
            else errores.Add($"{s.ReceiverName ?? s.MeliShipmentId.ToString()}: {err}");
        }
        // Si ya tiene repartidor en el mapa de hoy, que el "Salió a distribución" salga enseguida.
        if (avisados > 0) Me1AvisosBackgroundService.Despertar();
        return (avisados, programados, errores);
    }

    /// <summary>Lo que corre el job cada 10 minutos.</summary>
    public async Task<int> ProcesarAutomaticoAsync(CancellationToken ct = default)
    {
        var hoy = HoyAr();
        var horaAr = DateTime.UtcNow.AddHours(-3).Hour;
        int n = 0;

        // 1) Día de salida elegido al imprimir que ya llegó → "En camino" (desde las 7 de la mañana).
        if (horaAr >= 7)
        {
            var programados = await _db.MeliShipments.AsNoTracking()
                .Where(s => s.Mode == "me1" && s.Me1SaleFecha != null && s.Me1SaleFecha <= hoy
                            && (s.Status == "ready_to_ship" || s.Status == "handling"))
                .Select(s => s.Id).ToListAsync(ct);
            foreach (var id in programados)
            {
                var (ok, err) = await _ship.SetMe1StatusAsync(id, "shipped", null, null, null, "Despachado");
                if (ok) n++; else _log.LogWarning("[ME1 avisos] En camino (día elegido) envío {Id}: {Err}", id, err);
            }
        }

        // 2) En el mapeo de HOY, ya confirmado ("Sí, sale hoy") y con chofer → "Salió a distribución".
        var paradas = await _db.MapeoStops.AsNoTracking()
            .Where(p => p.Origin == "me1" && p.FechaReparto == hoy && p.OriginRefId != null)
            .Select(p => new { p.OriginRefId, p.AssignedDriverId })
            .ToListAsync(ct);
        var conChofer = new Dictionary<long, bool>();
        foreach (var p in paradas)
            if (long.TryParse(p.OriginRefId, out var sid))
                conChofer[sid] = (conChofer.TryGetValue(sid, out var ya) && ya) || p.AssignedDriverId != null;

        if (conChofer.Count > 0)
        {
            var ids = conChofer.Keys.ToList();
            var envios = await _db.MeliShipments.AsNoTracking()
                .Where(s => ids.Contains(s.MeliShipmentId) && s.Mode == "me1"
                            && s.Status != null && !Finales.Contains(s.Status) && s.Status != "pending")
                .Select(s => new { s.Id, s.MeliShipmentId, s.Status, s.Substatus, s.Me1SalioDistribucionFecha, s.Me1VisitaFallidaAt })
                .ToListAsync(ct);
            // 2026-11-20: registro de cada decisión (el 20/11 en prod no salió ningún aviso y no se pudo saber por qué).
            var faltan = ids.Except(envios.Select(e => e.MeliShipmentId)).ToList();
            if (faltan.Count > 0)
            {
                var info = await _db.MeliShipments.AsNoTracking().Where(x => faltan.Contains(x.MeliShipmentId))
                    .Select(x => x.MeliShipmentId + "=" + (x.Mode ?? "?") + "/" + (x.Status ?? "?")).ToListAsync(ct);
                _log.LogInformation("[ME1 avisos] mapeo de hoy: {N} envío(s) no se avisan: {Info} (sin fila: {Sin})",
                    faltan.Count, string.Join(", ", info), faltan.Count - info.Count);
            }
            foreach (var s in envios)
            {
                _log.LogInformation("[ME1 avisos] mapeo de hoy: envío {Sid} estado {St}/{Sub}, chofer {Ch}, salió a distr. {Sd:yyyy-MM-dd}",
                    s.MeliShipmentId, s.Status, s.Substatus ?? "-", conChofer[s.MeliShipmentId], s.Me1SalioDistribucionFecha);
                // 2026-11-20: "a conciencia" (pedido del usuario): estar en el mapa NO avisa "En camino". Ese
                // aviso sale solo cuando alguien contesta "Sí, sale hoy" (al imprimir o en el mapa). Si nadie
                // confirmó todavía, tampoco se manda "Salió a distribución".
                if (MeliShipmentService.Me1SinDespachar(s.Status)) continue;
                bool fallidaHoy = s.Me1VisitaFallidaAt.HasValue && s.Me1VisitaFallidaAt.Value.AddHours(-3).Date == hoy;
                if (conChofer[s.MeliShipmentId] && s.Me1SalioDistribucionFecha != hoy && !fallidaHoy)
                {
                    var (ok, err) = await _ship.SetMe1StatusAsync(s.Id, "shipped", "out_for_delivery", null, null, "Salio a distribucion");
                    if (ok) { n++; _log.LogInformation("[ME1 avisos] Salió a distribución OK envío {Sid}", s.MeliShipmentId); }
                    else _log.LogWarning("[ME1 avisos] Salió a distribución envío {Id}: {Err}", s.Id, err);
                }
            }
        }
        return n;
    }

    public record Me1PendienteDto(int Id, long ShipmentId, string? Nombre, string? Direccion, string? Localidad);

    /// <summary>ME1 que el repartidor tiene cargados y todavía no marcó (ni entregado ni visita fallida hoy).
    /// No cuenta los que tienen día de salida a futuro.</summary>
    public async Task<List<Me1PendienteDto>> PendientesDeRepartidorAsync(int repartidorId)
    {
        var hoy = HoyAr();
        var desdeUtc = hoy.AddHours(3);   // 00:00 AR en UTC
        var lista = await _db.MeliShipments.AsNoTracking()
            .Where(s => s.RepartidorAsignadoId == repartidorId && s.Mode == "me1"
                        && s.Status != null && !Finales.Contains(s.Status) && s.Status != "pending"
                        && (s.Me1SaleFecha == null || s.Me1SaleFecha <= hoy)
                        && (s.Me1VisitaFallidaAt == null || s.Me1VisitaFallidaAt < desdeUtc))
            .OrderBy(s => s.ReceiverName)
            .Select(s => new { s.Id, s.MeliShipmentId, s.ReceiverName, s.BuyerNickname, s.AddressLine, s.StreetName, s.StreetNumber, s.City })
            .ToListAsync();
        return lista.Select(s => new Me1PendienteDto(s.Id, s.MeliShipmentId, s.ReceiverName ?? s.BuyerNickname,
            s.AddressLine ?? $"{s.StreetName} {s.StreetNumber}".Trim(), s.City)).ToList();
    }
}

/// <summary>2026-11-18: cada 10 minutos manda los avisos automáticos de ME1 (ver Me1AvisosService).</summary>
public class Me1AvisosBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<Me1AvisosBackgroundService> _logger;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(10);

    // 2026-11-20: se suma un ME1 al mapeo → despierta el robot ya (ver AppDbContext.HayParadaMe1Tocada).
    private static readonly SemaphoreSlim _despertar = new(0, 1);
    public static void Despertar()
    {
        try { if (_despertar.CurrentCount == 0) _despertar.Release(); } catch (SemaphoreFullException) { }
    }

    public Me1AvisosBackgroundService(IServiceScopeFactory scopeFactory, ILogger<Me1AvisosBackgroundService> logger)
    {
        _scopeFactory = scopeFactory; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<Me1AvisosService>();
                var n = await svc.ProcesarAutomaticoAsync(stoppingToken);
                if (n > 0) _logger.LogInformation("[ME1 avisos] {N} avisos mandados a MeLi", n);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "[ME1 avisos] Error en el job"); }

            try
            {
                // Espera la vuelta de 10 minutos, o menos si alguien sumó un ME1 al mapeo. En ese caso deja
                // pasar unos segundos para que terminen de asignarle el chofer (y no avisar dos veces).
                if (await _despertar.WaitAsync(Period, stoppingToken))
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }
}
