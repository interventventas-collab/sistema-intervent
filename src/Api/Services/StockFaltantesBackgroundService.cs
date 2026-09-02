using Api.Data;

namespace Api.Services;

/// <summary>2026-09-02 — Robot que revisa cada 10 minutos si algun producto quedo por debajo de
/// su stock ideal y lo anota en la lista de "para pedir" (Cafe_StockFaltantes).
///
/// Existe para que la lista se arme sola aunque nadie tenga la pantalla abierta: si el stock baja
/// un sabado a la noche, el lunes el producto ya esta anotado. La pantalla igual engancha al
/// entrar, asi que el robot es el respaldo, no la unica via.
///
/// No manda avisos ni notificaciones — eso quedo para despues, a pedido del usuario.
/// Mismo andamiaje que MisAlertasBackgroundService.</summary>
public class StockFaltantesBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StockFaltantesBackgroundService> _logger;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);

    public StockFaltantesBackgroundService(IServiceScopeFactory scopeFactory,
        ILogger<StockFaltantesBackgroundService> logger)
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
                var svc = scope.ServiceProvider.GetRequiredService<StockFaltantesService>();
                var nuevos = await svc.EngancharAsync(stoppingToken);
                if (nuevos > 0)
                    _logger.LogInformation("[StockFaltantes] {N} productos nuevos para pedir", nuevos);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StockFaltantes] error en el ciclo (no critico)");
            }

            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
