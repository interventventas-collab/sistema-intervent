using Microsoft.EntityFrameworkCore;
using Api.Data;

namespace Api.Services;

/// <summary>
/// 2026-08-25 — Refresca de madrugada las comisiones que quedaron viejas.
///
/// Por qué existe: la comisión de cada publicación se guarda con el precio que tenía al
/// consultarla. Cuando el precio cambia, ese número deja de valer y el margen que muestra
/// el sistema pasa a ser mentira. Medido el 25/08: 1.771 publicaciones con la comisión
/// desfasada más de un 5% — casi un tercio del catálogo con márgenes dudosos.
///
/// Qué hace: a la hora configurada busca las publicaciones activas cuya comisión quedó
/// vieja (o nunca se cargó) y les vuelve a preguntar a MeLi. No toca precios ni stock.
///
/// Horario: por defecto 06:00 UTC = 03:00 en Argentina, cuando no hay nadie trabajando.
/// Configurable en AppSettings["meli.comisiones.hora_utc"].
///
/// KILL SWITCH: AppSettings["meli.comisiones.nocturno_enabled"] = "true". Default = true
/// (se prende solo), pero si molesta se apaga sin tocar código.
/// </summary>
public class MeliComisionesNocturnoService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MeliComisionesNocturnoService> _logger;

    // Cuánto desfasaje de precio hace que una comisión deje de valer.
    private const decimal TOLERANCIA = 0.05m;
    // Tope por noche: no tiene sentido machacar la API de MeLi de una sola vez.
    private const int MAX_POR_NOCHE = 800;

    public MeliComisionesNocturnoService(IServiceScopeFactory scopeFactory,
        ILogger<MeliComisionesNocturnoService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Arranque escalonado: que no salga corriendo apenas levanta la API.
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTime? ultimaCorrida = null;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var horaUtc = await LeerHoraAsync(db, stoppingToken);
                var ahora = DateTime.UtcNow;

                var apagado = await LeerApagadoAsync(db, stoppingToken);
                var yaCorrioHoy = await CorrioHoyAsync(db, ahora, stoppingToken);

                if (!apagado && !yaCorrioHoy && ahora.Hour == horaUtc)
                {
                    // En desarrollo no hay cuenta de MeLi conectada: no hay nada que refrescar.
                    var hayCuentas = await db.MeliAccounts.AnyAsync(stoppingToken);
                    if (!hayCuentas)
                    {
                        _logger.LogInformation("[Comisiones nocturno] Sin cuentas MeLi conectadas — no hay nada que hacer");
                        await MarcarCorridaAsync(db, ahora, "sin cuentas", stoppingToken);
                    }
                    else
                    {
                        ultimaCorrida = ahora;
                        await RefrescarAsync(scope, db, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Comisiones nocturno] Falló el ciclo");
            }

            // Se despierta cada 20 minutos a mirar la hora. Barato y simple.
            try { await Task.Delay(TimeSpan.FromMinutes(20), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RefrescarAsync(IServiceScope scope, AppDbContext db, CancellationToken ct)
    {
        var itemSvc = scope.ServiceProvider.GetRequiredService<MeliItemService>();

        // Las que más lo necesitan primero: sin comisión, y después las más desfasadas.
        var candidatas = await db.MeliItems.AsNoTracking()
            .Where(m => m.VariationId == null && m.Status == "active" && m.Price > 0
                        && (m.SaleFeeAmount == null || m.SaleFeeAmount <= 0
                            || m.SaleFeePriceSnapshot == null || m.SaleFeePriceSnapshot <= 0
                            || (m.Price - m.SaleFeePriceSnapshot.Value) / m.Price > TOLERANCIA
                            || (m.SaleFeePriceSnapshot.Value - m.Price) / m.Price > TOLERANCIA))
            .OrderBy(m => m.SaleFeeCapturedAt)
            .Take(MAX_POR_NOCHE)
            .Select(m => m.MeliItemId)
            .ToListAsync(ct);

        if (candidatas.Count == 0)
        {
            _logger.LogInformation("[Comisiones nocturno] Todas las comisiones están al día");
            await MarcarCorridaAsync(db, DateTime.UtcNow, "nada pendiente", ct);
            return;
        }

        _logger.LogWarning("[Comisiones nocturno] Arranca: {N} publicaciones con la comisión vieja o sin cargar", candidatas.Count);

        int ok = 0, err = 0;
        foreach (var mla in candidatas)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                if (await itemSvc.RefreshSaleFeeAsync(mla)) ok++; else err++;
            }
            catch { err++; }
            // Freno para no pasarse del límite de MeLi (~3 por segundo).
            try { await Task.Delay(350, ct); } catch (OperationCanceledException) { break; }
        }

        _logger.LogWarning("[Comisiones nocturno] Terminó: {Ok} actualizadas, {Err} con error (de {Total})",
            ok, err, candidatas.Count);
        await MarcarCorridaAsync(db, DateTime.UtcNow, $"{ok} actualizadas, {err} con error", ct);
    }

    private static async Task<int> LeerHoraAsync(AppDbContext db, CancellationToken ct)
    {
        var s = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == "meli.comisiones.hora_utc", ct);
        return s != null && int.TryParse(s.Value, out var h) && h is >= 0 and <= 23 ? h : 6; // 06 UTC = 03 ARG
    }

    private static async Task<bool> LeerApagadoAsync(AppDbContext db, CancellationToken ct)
    {
        var s = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == "meli.comisiones.nocturno_enabled", ct);
        if (s is null) return false; // default: prendido
        var v = s.Value?.Trim().ToLowerInvariant();
        return v is "false" or "0" or "off";
    }

    /// <summary>Se apoya en AppSettings para no repetir la corrida si la API se reinicia.</summary>
    private static async Task<bool> CorrioHoyAsync(AppDbContext db, DateTime ahora, CancellationToken ct)
    {
        var s = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == "meli.comisiones.ultima_corrida", ct);
        if (s?.Value is null) return false;
        var fecha = s.Value.Length >= 10 ? s.Value[..10] : "";
        return fecha == ahora.ToString("yyyy-MM-dd");
    }

    private static async Task MarcarCorridaAsync(AppDbContext db, DateTime ahora, string detalle, CancellationToken ct)
    {
        var s = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == "meli.comisiones.ultima_corrida", ct);
        var valor = $"{ahora:yyyy-MM-dd HH:mm} UTC · {detalle}";
        if (s is null)
            db.AppSettings.Add(new Models.AppSetting { Key = "meli.comisiones.ultima_corrida", Value = valor });
        else
            s.Value = valor;
        await db.SaveChangesAsync(ct);
    }
}
