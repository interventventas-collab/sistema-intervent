using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.Services;

/// <summary>
/// Corre los movimientos de Galicia solo, en los horarios que el usuario configuró
/// (hora Argentina, UTC-3). Cada ~2 min revisa si hay un horario "vencido" hoy que
/// todavía no se ejecutó, y si sí, dispara la sincronización. Marca LastAutoSyncAt
/// para no repetir el mismo horario (haya salido bien o mal, así no martilla al banco).
/// </summary>
public class GaliciaAutoSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GaliciaAutoSyncBackgroundService> _logger;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    // Argentina es UTC-3 fijo (sin horario de verano).
    private const int ARG_OFFSET_HOURS = -3;

    public GaliciaAutoSyncBackgroundService(IServiceScopeFactory scopeFactory, ILogger<GaliciaAutoSyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(FirstDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Galicia auto] error en el ciclo (no crítico)"); }

            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync()
    {
        // 2026-11-18: una vuelta por cada cuenta (una por sociedad), de a una: el robot hace
        // una sola cosa a la vez.
        List<int> ids;
        using (var scope0 = _scopeFactory.CreateScope())
        {
            var accounts0 = scope0.ServiceProvider.GetRequiredService<GaliciaAccountService>();
            ids = (await accounts0.ListEntitiesAsync())
                .Where(a => a.IsActive && a.AutoSyncEnabled).Select(a => a.Id).ToList();
        }
        foreach (var id in ids)
        {
            try { await TickCuentaAsync(id); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Galicia auto] error con la cuenta {Id} (no crítico)", id); }
        }
    }

    private async Task TickCuentaAsync(int accountId)
    {
        using var scope = _scopeFactory.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<GaliciaAccountService>();

        var acc = await accounts.GetEntityAsync(accountId);
        if (acc is null || !acc.AutoSyncEnabled) return;
        var times = GaliciaAccountService.ParseTimes(acc.AutoSyncTimes);
        if (times.Count == 0) return;

        // Hora Argentina actual.
        var argNow = DateTime.UtcNow.AddHours(ARG_OFFSET_HOURS);

        // El horario "vencido" más reciente de hoy (el mayor <= ahora).
        var vencidos = times.Where(t => argNow.TimeOfDay >= t).ToList();
        if (vencidos.Count == 0) return; // todavía no llegó ningún horario de hoy
        var dueTime = vencidos.Max();
        var dueArg = argNow.Date + dueTime;

        // ¿Ya corrimos para este horario? LastAutoSyncAt está en UTC.
        var lastArg = acc.LastAutoSyncAt?.AddHours(ARG_OFFSET_HOURS);
        if (lastArg.HasValue && lastArg.Value >= dueArg) return; // ya lo hicimos

        var quien = acc.Alias ?? acc.Usuario;
        _logger.LogInformation("[Galicia auto] {Quien}: corriendo el horario {Hora} (ARG)...", quien, dueTime);
        // Marcar YA (antes de correr) para que aunque tarde ~1 min no dispare de nuevo en el próximo tick.
        await accounts.MarcarAutoSyncAsync(accountId, DateTime.UtcNow);

        var sync = scope.ServiceProvider.GetRequiredService<GaliciaSyncService>();
        var r = await sync.SincronizarAsync(accountId);
        if (r.Ok)
            _logger.LogInformation("[Galicia auto] {Quien}: OK — {N} movimientos nuevos ({S} ya estaban)", quien, r.Nuevos, r.SinCambios);
        else
            _logger.LogWarning("[Galicia auto] {Quien}: no se pudo (movimientos): {Err}", quien, r.Error);

        // Después de los movimientos, traer también los cheques (mismo horario).
        try
        {
            var rc = await sync.SincronizarChequesAsync(accountId);
            if (rc.Ok)
                _logger.LogInformation("[Galicia auto] {Quien}: OK — cheques: {N} nuevos, {A} actualizados ({S} sin cambios)", quien, rc.Nuevos, rc.Actualizados, rc.SinCambios);
            else
                _logger.LogWarning("[Galicia auto] {Quien}: no se pudo (cheques): {Err}", quien, rc.Error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Galicia auto] {Quien}: error trayendo cheques (no crítico)", quien);
        }
    }
}
