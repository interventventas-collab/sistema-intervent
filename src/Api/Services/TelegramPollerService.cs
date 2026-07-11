using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.Services;

/// <summary>
/// Robot del bot de Telegram. En un loop continuo:
///   - avisa las ventas nuevas de MeLi que faltan avisar,
///   - escucha (long-polling) los mensajes que el dueño le escribe al bot y le responde
///     ("ventas", "saldo", "alertas").
///
/// La respuesta es casi instantánea porque usa long-polling (la llamada a Telegram queda
/// esperando hasta 25s a que llegue un mensaje). Si no hay bot activo, espera y reintenta.
///
/// Mismo andamiaje que los demás BackgroundService (scope por vuelta). Las ALERTAS no se avisan
/// desde acá: se disparan desde MisAlertasBackgroundService, en el mismo momento que saltan.
/// </summary>
public class TelegramPollerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelegramPollerService> _logger;
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(20); // cuando no hay bot activo
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(15);

    public TelegramPollerService(IServiceScopeFactory scopeFactory, ILogger<TelegramPollerService> logger)
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
            bool activo = false;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<TelegramService>();
                activo = await svc.PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Telegram poll] error en el ciclo (no crítico)");
                activo = false;
            }

            // Si hay bot activo, el long-poll ya "esperó" adentro (hasta 25s). Si no, esperamos acá.
            if (!activo)
            {
                try { await Task.Delay(IdleDelay, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
