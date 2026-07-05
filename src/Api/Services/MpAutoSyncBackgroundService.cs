using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.Services;

/// <summary>
/// Lee el saldo de Mercado Pago solo, en los horarios configurados por el usuario
/// (hora Argentina, UTC-3). Mismo patron que Shell/Galicia. Como MP es API (rapida y
/// confiable), esto es una red de seguridad; a futuro se le suma el webhook para
/// tiempo real. Cada ~2 min revisa si hay un horario vencido hoy sin ejecutar.
/// </summary>
public class MpAutoSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MpAutoSyncBackgroundService> _logger;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    private const int ARG_OFFSET_HOURS = -3;

    public MpAutoSyncBackgroundService(IServiceScopeFactory scopeFactory, ILogger<MpAutoSyncBackgroundService> logger)
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
            catch (Exception ex) { _logger.LogWarning(ex, "[MP auto] error en el ciclo (no critico)"); }
            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<MpAccountService>();

        var acc = await accounts.GetEntityAsync();
        if (acc is null || !acc.AutoSyncEnabled || !acc.IsActive) return;
        var times = MpAccountService.ParseTimes(acc.AutoSyncTimes);
        if (times.Count == 0) return;

        var argNow = DateTime.UtcNow.AddHours(ARG_OFFSET_HOURS);
        var vencidos = times.Where(t => argNow.TimeOfDay >= t).ToList();
        if (vencidos.Count == 0) return;
        var dueArg = argNow.Date + vencidos.Max();
        var lastArg = acc.LastAutoSyncAt?.AddHours(ARG_OFFSET_HOURS);
        if (lastArg.HasValue && lastArg.Value >= dueArg) return;

        _logger.LogInformation("[MP auto] Corriendo sincronización del horario {Hora} (ARG)...", vencidos.Max());
        await accounts.MarcarAutoSyncAsync(DateTime.UtcNow);

        // 1) Cobros de los últimos 7 días (dedup — acumula los nuevos, bajo el tope de 10k).
        try
        {
            var pagos = scope.ServiceProvider.GetRequiredService<MpPagosService>();
            var rp = await pagos.SincronizarAsync(7);
            if (rp.Ok) _logger.LogInformation("[MP auto] Cobros OK — {N} nuevos", rp.Nuevos);
            else _logger.LogWarning("[MP auto] Cobros: {Err}", rp.Error);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[MP auto] error en cobros"); }

        // 2) Movimientos (reporte). El servicio procesa el reporte listo del run anterior
        //    y pide uno fresco; así 2x/día se mantiene al día sin esperas.
        try
        {
            var reportes = scope.ServiceProvider.GetRequiredService<MpReportesService>();
            var rm = await reportes.SincronizarAsync(7);
            if (rm.Ok && !rm.EnProceso) _logger.LogInformation("[MP auto] Movimientos OK — {N} nuevos", rm.Nuevos);
            else if (rm.EnProceso) _logger.LogInformation("[MP auto] Movimientos: reporte pedido, se procesa en la próxima corrida");
            else _logger.LogWarning("[MP auto] Movimientos: {Err}", rm.Error);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[MP auto] error en movimientos"); }
    }
}
