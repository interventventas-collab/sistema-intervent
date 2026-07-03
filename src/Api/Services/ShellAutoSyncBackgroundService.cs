using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.Services;

/// <summary>
/// Lee el saldo de Shell Flota solo, en los horarios configurados por el usuario
/// (hora Argentina, UTC-3). Mismo patrón que GaliciaAutoSyncBackgroundService.
/// </summary>
public class ShellAutoSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ShellAutoSyncBackgroundService> _logger;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(3);
    private const int ARG_OFFSET_HOURS = -3;

    public ShellAutoSyncBackgroundService(IServiceScopeFactory scopeFactory, ILogger<ShellAutoSyncBackgroundService> logger)
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
            catch (Exception ex) { _logger.LogWarning(ex, "[Shell auto] error en el ciclo (no crítico)"); }
            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<ShellAccountService>();

        var acc = await accounts.GetEntityAsync();
        if (acc is null || !acc.AutoSyncEnabled) return;
        var times = ShellAccountService.ParseTimes(acc.AutoSyncTimes);
        if (times.Count == 0) return;

        var argNow = DateTime.UtcNow.AddHours(ARG_OFFSET_HOURS);
        var vencidos = times.Where(t => argNow.TimeOfDay >= t).ToList();
        if (vencidos.Count == 0) return;
        var dueArg = argNow.Date + vencidos.Max();
        var lastArg = acc.LastAutoSyncAt?.AddHours(ARG_OFFSET_HOURS);
        if (lastArg.HasValue && lastArg.Value >= dueArg) return;

        _logger.LogInformation("[Shell auto] Leyendo saldo del horario {Hora} (ARG)...", vencidos.Max());
        await accounts.MarcarAutoSyncAsync(DateTime.UtcNow);

        var sync = scope.ServiceProvider.GetRequiredService<ShellSyncService>();
        var r = await sync.SincronizarAsync();
        if (r.Ok) _logger.LogInformation("[Shell auto] OK — saldo {Saldo}", r.Saldo);
        else _logger.LogWarning("[Shell auto] No se pudo: {Err}", r.Error);
    }
}
