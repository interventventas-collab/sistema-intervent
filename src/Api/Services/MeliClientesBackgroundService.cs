using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.Services;

/// <summary>
/// 2026-07-17: Robot que cada 20 minutos actualiza la base propia de clientes de MercadoLibre.
/// Toma las ventas nuevas de MeliOrders y las suma a MeliClientes / MeliClienteCompras (con el
/// telefono/direccion de los envios Flex/ME1 cuando existe). Es incremental: solo procesa lo que
/// falta, asi que es liviano. El backfill inicial (traer todo lo antiguo) tambien pasa por aca en
/// la primera vuelta.
/// </summary>
public class MeliClientesBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MeliClientesBackgroundService> _logger;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);

    public MeliClientesBackgroundService(IServiceScopeFactory scopeFactory, ILogger<MeliClientesBackgroundService> logger)
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
                var svc = scope.ServiceProvider.GetRequiredService<MeliClientesService>();
                var procesadas = await svc.SyncAsync();
                if (procesadas > 0)
                    _logger.LogInformation("[Clientes MeLi] {N} ventas nuevas sumadas a la base de clientes.", procesadas);

                // Buscar a MeLi el telefono de los clientes Flex/ME1 que todavia no lo tienen.
                var traidos = await svc.EnrichPhonesAsync(maxLlamadas: 100);
                if (traidos > 0)
                    _logger.LogInformation("[Clientes MeLi] {N} telefonos nuevos traidos de MeLi.", traidos);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Clientes MeLi] Error en el job");
            }

            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
