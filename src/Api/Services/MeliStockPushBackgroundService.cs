using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Api.Data;

namespace Api.Services;

/// <summary>
/// Job de respaldo del push event-driven stock sistema → MeLi.
///
/// Corre cada 15 minutos y revisa si hay productos con StockChangedAt > LastPushedToMeli
/// que NO se pushearon en el momento por algun motivo (excepcion, MeLi caido, etc).
///
/// Esto es la red de seguridad: el flujo normal es event-driven (CafeVentas / MeliStock
/// disparan el push apenas cambia el stock). Si todo va bien, este job no encuentra nada.
///
/// KILL SWITCH (2026-05-23): chequea AppSettings["meli.stock_push.background_enabled"] antes de cada ciclo.
/// Si está en "false" (o no existe), el job NO corre. Default = false (seguridad).
/// Para activar: setear esa clave en "true" desde la pantalla admin de AppSettings.
/// </summary>
public class MeliStockPushBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MeliStockPushBackgroundService> _logger;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(5);

    public MeliStockPushBackgroundService(IServiceScopeFactory scopeFactory,
        ILogger<MeliStockPushBackgroundService> logger)
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

                // KILL SWITCH: chequear AppSettings antes de cada ciclo.
                // Default = false (seguridad). Para habilitar: setear "meli.stock_push.background_enabled" = "true".
                var settingRow = await db.AppSettings
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Key == "meli.stock_push.background_enabled", stoppingToken);
                var enabled = settingRow != null
                    && string.Equals(settingRow.Value?.Trim(), "true", System.StringComparison.OrdinalIgnoreCase);

                if (!enabled)
                {
                    _logger.LogDebug("[Push stock MeLi] DESHABILITADO via AppSettings — saltando ciclo");
                }
                else
                {
                    var pushSvc = scope.ServiceProvider.GetRequiredService<MeliStockPushService>();
                    var r = await pushSvc.PushPendingAsync(maxProductos: 200, stoppingToken);
                    if (r.Procesadas > 0)
                    {
                        _logger.LogInformation("[Push stock MeLi] {P} publicaciones procesadas (ok={O}, skipped={S}, err={E})",
                            r.Procesadas, r.Ok, r.Skipped, r.Errores);
                    }
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "[Push stock MeLi] Error en el job de respaldo");
            }

            try { await Task.Delay(Period, stoppingToken); }
            catch (System.OperationCanceledException) { break; }
        }
    }
}
