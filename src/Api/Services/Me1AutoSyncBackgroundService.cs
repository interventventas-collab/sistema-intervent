using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.Services;

/// <summary>
/// 2026-07-08: Job que cada 1 hora trae los envios ME1 (envios manuales del vendedor)
/// de los ultimos 2 dias desde MeLi y los guarda localmente. Asi la pantalla
/// /meli/me1/entregas se mantiene al dia sin que el usuario tenga que apretar
/// "Sincronizar con MeLi" a mano.
///
/// Ventana chica (2 dias) a proposito: como corre cada hora, con captar las ventas
/// NUEVAS del ultimo dia/dos alcanza. SyncMe1Async ademas refresca el estado de TODOS
/// los pendientes ya cargados (sin importar la fecha), asi que no se pierde ninguna
/// transicion "despachado -> entregado" de envios viejos. El boton manual sigue
/// trayendo 30 dias por si hace falta recargar todo el mes.
///
/// Mismo flujo que el boton manual (MeliShipmentService.SyncMe1Async). Si MeLi tira
/// un error temporal, el job lo loguea y vuelve a intentar en la siguiente vuelta.
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

                _logger.LogInformation("[ME1 auto-sync] Trayendo envios ME1 de los ultimos 2 dias + refrescando pendientes...");
                var r = await shipmentSvc.SyncMe1Async(daysBack: 2, maxOrdersPerAccount: 300);
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
