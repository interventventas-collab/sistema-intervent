using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.Services;

/// <summary>
/// 2026-07-08: Job que cada 1 hora mantiene al dia los envios ME1 (envios manuales del
/// vendedor) sin que el usuario tenga que apretar "Sincronizar con MeLi" a mano.
///
/// Usa SyncMe1FromOrdersAsync: mira la tabla local de ordenes (que ya tiene la marca ME1)
/// y baja SOLO los envios ME1 de los ultimos 7 dias que falten o esten pendientes, ademas
/// de refrescar el estado de todos los ME1 pendientes ya cargados. Es liviano porque ME1
/// es una fraccion chica del total (la mayoria de las ventas son me2/Flex).
///
/// Antes se escaneaban las ~300 ventas mas recientes de MeLi y se bajaba cada envio para
/// recien ahi ver si era ME1; con ~90-100 ventas/dia eso cubria solo ~3 dias y dejaba ciegas
/// las ME1 mas viejas. Esta version no tiene ese punto ciego.
///
/// Si MeLi tira un error temporal, el job lo loguea y reintenta en la siguiente vuelta.
/// </summary>
public class Me1AutoSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<Me1AutoSyncBackgroundService> _logger;
    private static readonly TimeSpan Period = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(3); // esperar a que arranque la app

    public Me1AutoSyncBackgroundService(IServiceScopeFactory scopeFactory, ILogger<Me1AutoSyncBackgroundService> logger)
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
                var shipmentSvc = scope.ServiceProvider.GetRequiredService<MeliShipmentService>();

                _logger.LogInformation("[ME1 auto-sync] Trayendo envios ME1 de los ultimos 7 dias + refrescando pendientes...");
                var r = await shipmentSvc.SyncMe1FromOrdersAsync(daysBack: 7);
                _logger.LogInformation("[ME1 auto-sync] Sincronizados {S} envios ME1 ({E} errores)",
                    r.TotalSynced, r.TotalErrors);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ME1 auto-sync] Error en el job");
            }

            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
