using Api.Services;

namespace Api.BackgroundJobs;

/// <summary>
/// Sincroniza el stock Full (meli_facility) de las publicaciones linkeadas cada 30 minutos.
/// Pobla Cafe_StockPorDeposito[Full MeLi] con el stock que MeLi gestiona en sus depósitos.
/// No toca el stock propio (9 de Abril) ni los pushes — solo lectura.
/// </summary>
public class MeliFullStockSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MeliFullStockSyncBackgroundService> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2); // dar tiempo a que arranque la app

    public MeliFullStockSyncBackgroundService(IServiceScopeFactory scopeFactory,
        ILogger<MeliFullStockSyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<MeliFullStockSyncService>();
                var r = await svc.SyncAllAsync(null, stoppingToken);
                _logger.LogInformation(
                    "Full stock sync: {Upgs} UPGs procesados, {Full} con Full, {Updated} productos actualizados, {Err} errores",
                    r.UpgsProcesados, r.UpgsFull, r.ProductosActualizados, r.Errores);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MeliFullStockSyncBackgroundService: error en ciclo");
            }
            try { await Task.Delay(Interval, stoppingToken); } catch { break; }
        }
    }
}
