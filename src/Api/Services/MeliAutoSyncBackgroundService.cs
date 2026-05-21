using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.Services;

/// <summary>
/// Job que cada 30 minutos:
///   1. Sincroniza ordenes nuevas de MeLi (últimas 6h por safety).
///   2. Descuenta stock de las que llegaron con MeliItemComponente.
///
/// Pensado para el modo "shadow" — el usuario NO tiene que apretar nada manual.
/// Si MeLi tira un error temporal, el job sigue tratando cada 30 min.
/// </summary>
public class MeliAutoSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MeliAutoSyncBackgroundService> _logger;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2); // esperar a que arranque la app

    public MeliAutoSyncBackgroundService(IServiceScopeFactory scopeFactory, ILogger<MeliAutoSyncBackgroundService> logger)
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
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var orderSvc = scope.ServiceProvider.GetRequiredService<MeliOrderService>();
                var stockSvc = scope.ServiceProvider.GetRequiredService<MeliStockSyncService>();

                var to = DateTime.UtcNow;
                var from = to.AddHours(-6); // ventana generosa por safety
                _logger.LogInformation("[MeLi auto-sync] Trayendo ordenes desde {From}...", from);
                var syncResult = await orderSvc.SyncOrdersAsync(from, to);
                _logger.LogInformation("[MeLi auto-sync] Sincronizadas {N} ordenes ({E} errores)",
                    syncResult.TotalSynced, syncResult.TotalErrors);

                var stockResult = await stockSvc.ProcessPendingAsync(maxBatch: 500);
                if (stockResult.Procesadas > 0)
                {
                    _logger.LogInformation("[MeLi auto-sync] Stock: {P} ordenes procesadas (cafe={C}, otros={O}, sin link={S})",
                        stockResult.Procesadas, stockResult.DescontadasCafe, stockResult.DescontadasOtros, stockResult.SinLinkear);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MeLi auto-sync] Error en el job");
            }

            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
