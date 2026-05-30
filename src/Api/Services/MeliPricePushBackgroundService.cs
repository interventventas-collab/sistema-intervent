using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Api.Data;

namespace Api.Services;

/// <summary>
/// Job de respaldo del push event-driven de PRECIO sistema → MeLi (paralelo al de stock).
///
/// Corre cada 15 minutos. Busca productos con PriceChangedAt seteado y pushea las
/// publicaciones "claimed" (SyncPrecio=true) linkeadas que estén pendientes.
///
/// El flujo normal es event-driven (CafeProductosController dispara fire-and-forget al
/// editar el precio). Este job es la red de seguridad por si falla en el momento.
///
/// KILL SWITCH: chequea AppSettings["meli.price_push.background_enabled"] = "true" antes
/// de cada ciclo. Default = false. Si está apagado, no hace nada.
/// </summary>
public class MeliPricePushBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MeliPricePushBackgroundService> _logger;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(6);

    public MeliPricePushBackgroundService(IServiceScopeFactory scopeFactory,
        ILogger<MeliPricePushBackgroundService> logger)
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
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var settingRow = await db.AppSettings
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Key == "meli.price_push.background_enabled", stoppingToken);
                var enabled = settingRow != null
                    && string.Equals(settingRow.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

                if (!enabled)
                {
                    _logger.LogDebug("[Push price MeLi] DESHABILITADO via AppSettings — saltando ciclo");
                }
                else
                {
                    var pushSvc = scope.ServiceProvider.GetRequiredService<MeliPricePushService>();
                    var (proc, ok) = await pushSvc.PushPendingPrecioAsync(maxProductos: 100, stoppingToken);
                    if (proc > 0)
                    {
                        _logger.LogInformation("[Push price MeLi] {Proc} productos revisados, {Ok} publicaciones pusheadas",
                            proc, ok);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Push price MeLi] Error en el job de respaldo");
            }

            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
