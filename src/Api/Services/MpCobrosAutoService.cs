namespace Api.Services;

/// <summary>
/// Trae solo los cobros de Mercado Pago de los últimos 3 días cada 30 minutos, para que la
/// pantalla "Cobranzas Mercado Pago" esté al día sin tocar nada. Independiente de los horarios
/// de MpAutoSyncBackgroundService (que además lee saldo y movimientos). Pedido 2026-10-13.
/// </summary>
public class MpCobrosAutoService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MpCobrosAutoService> _logger;
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(30);

    public MpCobrosAutoService(IServiceScopeFactory scopeFactory, ILogger<MpCobrosAutoService> logger)
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
                var accounts = scope.ServiceProvider.GetRequiredService<MpAccountService>();
                var acc = await accounts.GetEntityAsync();
                if (acc is not null && acc.IsActive && !string.IsNullOrEmpty(acc.AccessToken))
                {
                    var pagos = scope.ServiceProvider.GetRequiredService<MpPagosService>();
                    var r = await pagos.SincronizarAsync(3);
                    if (!r.Ok) _logger.LogWarning("[MP cobros auto] {Err}", r.Error);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[MP cobros auto] error (no critico)"); }
            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
