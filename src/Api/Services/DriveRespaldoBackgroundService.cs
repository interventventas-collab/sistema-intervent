namespace Api.Services;

/// <summary>
/// 30/09/2026: robot del respaldo de comprobantes en Google Drive (ver DriveRespaldoService).
/// Cada 2 minutos sube hasta 25 comprobantes nuevos o editados, los más nuevos primero. Así lo del
/// día aparece enseguida y lo viejo de 2026 se va completando de a poco sin cargar al servidor.
///
/// No hace nada si el interruptor de Integraciones → Google Drive está apagado o si Drive no está
/// conectado (por ejemplo en desarrollo). Si Google corta el permiso, deja de intentar hasta el
/// ciclo siguiente — el aviso de "Drive desconectado" lo da la nubecita (alerta DRIVE_CAIDO).
/// </summary>
public class DriveRespaldoBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DriveRespaldoBackgroundService> _log;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(90);
    private const int PorCiclo = 25;

    public DriveRespaldoBackgroundService(IServiceScopeFactory scopeFactory, ILogger<DriveRespaldoBackgroundService> log)
    {
        _scopeFactory = scopeFactory; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(FirstDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "[DriveRespaldo] error en el ciclo (no critico)"); }
            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<DriveRespaldoService>();
        var drive = scope.ServiceProvider.GetRequiredService<GoogleDriveService>();

        if (!await svc.EstaActivoAsync()) return;
        if (!await drive.EstaConfiguradoAsync()) return;

        var pendientes = await svc.CandidatosAsync(PorCiclo);
        if (pendientes.Count == 0) return;

        int ok = 0, fallas = 0;
        foreach (var c in pendientes)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await svc.SubirAsync(c);
                ok++;
            }
            catch (Exception ex)
            {
                fallas++;
                _log.LogWarning("[DriveRespaldo] {Tipo} {Id}: {Error}", c.Tipo, c.Id, ex.Message);
                // Permiso de Google vencido/revocado: no tiene sentido seguir con el resto del lote.
                if (ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("invalid_client", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("expired or revoked", StringComparison.OrdinalIgnoreCase))
                    break;
            }
        }
        _log.LogInformation("[DriveRespaldo] ciclo: {Ok} subidos, {Fallas} con error", ok, fallas);
    }
}
