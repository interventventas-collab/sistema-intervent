using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-20: robot de la cola de envío de comprobantes. Cada minuto mira si hay envíos
/// PENDIENTE cuya hora ya llegó y los manda.
///
/// Por qué la espera vive en el servidor y no en la pantalla: si la cuenta de los minutos la
/// llevara el navegador, se moriría al cerrar la pestaña o recargar, y el envío no saldría
/// nunca. Acá queda anotado en la base, así que sobrevive incluso a un reinicio del servidor.
///
/// Nota sobre entornos: desarrollo y producción tienen bases separadas, así que cada uno
/// procesa SOLO lo que se encoló en él. Nada se encola solo: siempre hay alguien que emitió
/// una venta con el canal tildado.
/// </summary>
public class EnvioComprobanteBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EnvioComprobanteBackgroundService> _log;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(45);

    public EnvioComprobanteBackgroundService(IServiceScopeFactory scopeFactory,
        ILogger<EnvioComprobanteBackgroundService> log)
    {
        _scopeFactory = scopeFactory; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(FirstDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "[EnvioComprobante] error en el ciclo (no critico)"); }
            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<EnvioComprobanteService>();

        var ahora = DateTime.UtcNow;
        var pendientes = await db.CafeVentasEnvios
            .Where(x => x.Estado == CafeVentaEnvio.EstadoPendiente
                        && x.ProgramadoPara != null && x.ProgramadoPara <= ahora)
            .OrderBy(x => x.ProgramadoPara)
            .Take(50)
            .ToListAsync();
        if (pendientes.Count == 0) return;

        foreach (var fila in pendientes)
        {
            try
            {
                var (ok, error) = await svc.ProcesarAsync(fila);
                if (ok) _log.LogInformation("[EnvioComprobante] venta {VentaId} enviada por {Canal} a {Destino}",
                    fila.VentaId, fila.Canal, fila.Destino);
                else _log.LogWarning("[EnvioComprobante] venta {VentaId} por {Canal}: {Error}",
                    fila.VentaId, fila.Canal, error);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[EnvioComprobante] fallo mandando la venta {VentaId} por {Canal}", fila.VentaId, fila.Canal);
            }
        }
    }
}
