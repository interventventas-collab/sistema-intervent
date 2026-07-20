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
        using var scope = _scopeFactory.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<GaliciaAccountService>();

        var acc = await accounts.GetEntityAsync();
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

        _logger.LogInformation("[Galicia auto] Corriendo sincronización del horario {Hora} (ARG)...", dueTime);
        // Marcar YA (antes de correr) para que aunque tarde ~1 min no dispare de nuevo en el próximo tick.
        await accounts.MarcarAutoSyncAsync(DateTime.UtcNow);

        var sync = scope.ServiceProvider.GetRequiredService<GaliciaSyncService>();
        var r = await sync.SincronizarAsync();
        if (r.Ok)
            _logger.LogInformation("[Galicia auto] OK — {N} movimientos nuevos ({S} ya estaban)", r.Nuevos, r.SinCambios);
        else
            _logger.LogWarning("[Galicia auto] No se pudo (movimientos): {Err}", r.Error);

        // Después de los movimientos, traer también los cheques (mismo horario, misma sesión de robot).
        // El robot es de una sola corrida a la vez; como SincronizarAsync ya terminó, ahora sí puede correr cheques.
        try
        {
            var rc = await sync.SincronizarChequesAsync();
            if (rc.Ok)
                _logger.LogInformation("[Galicia auto] OK — cheques: {N} nuevos, {A} actualizados ({S} sin cambios)", rc.Nuevos, rc.Actualizados, rc.SinCambios);
            else
                _logger.LogWarning("[Galicia auto] No se pudo (cheques): {Err}", rc.Error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Galicia auto] error trayendo cheques (no crítico)");
        }
    }
}
