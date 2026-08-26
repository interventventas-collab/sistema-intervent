using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-26: robot de los mensajes programados de WhatsApp. Cada minuto mira si hay alguno
/// PENDIENTE cuya hora ya llegó y lo manda.
///
/// Copia deliberada del robot de comprobantes (EnvioComprobanteBackgroundService): mismo ritmo,
/// mismo criterio de "la espera vive en el servidor". Si la cuenta de los minutos la llevara la
/// pantalla, se moriría al cerrar la pestaña y el mensaje no saldría nunca.
///
/// Se toma de a 50 por vuelta para no clavarse si alguna vez hay una tanda grande.
/// </summary>
public class WhatsAppProgramadosBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WhatsAppProgramadosBackgroundService> _log;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(50);

    public WhatsAppProgramadosBackgroundService(IServiceScopeFactory scopeFactory,
        ILogger<WhatsAppProgramadosBackgroundService> log)
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
            catch (Exception ex) { _log.LogWarning(ex, "[Programados] error en el ciclo (no critico)"); }
            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<WhatsAppProgramadosService>();

        var ahora = DateTime.UtcNow;
        var pendientes = await db.WhatsAppMensajesProgramados
            .Where(x => x.Estado == WhatsAppMensajeProgramado.EstadoPendiente && x.ProgramadoPara <= ahora)
            .OrderBy(x => x.ProgramadoPara)
            .Take(50)
            .ToListAsync();
        if (pendientes.Count == 0) return;

        foreach (var fila in pendientes)
        {
            try
            {
                var (ok, error) = await svc.ProcesarAsync(fila);
                if (ok) _log.LogInformation("[Programados] mensaje {Id} ({Tipo}) enviado a {Numero}", fila.Id, fila.Tipo, fila.Numero);
                else _log.LogWarning("[Programados] mensaje {Id} a {Numero}: {Error}", fila.Id, fila.Numero, error);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[Programados] fallo mandando el mensaje {Id}", fila.Id);
            }
        }
    }
}
