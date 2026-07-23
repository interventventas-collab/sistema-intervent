using Api.Data;
using Api.Models;

namespace Api.Services;

/// <summary>2026-07-23 (pedido Osmar): manda por Telegram, una vez por día a las 08:00 (hora
/// Argentina), el resumen financiero de la mañana (Galicia + Shell Flota + cheques por cubrir).
/// Mismo patrón que DeudoresDiarioService: NO usa ScheduledProcesses, se auto-agenda con una
/// traba diaria en AppSettings, así se despliega solo en dev y prod sin sembrar filas.</summary>
public class ResumenFinancieroDiarioService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ResumenFinancieroDiarioService> _logger;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
    private const int ARG_OFFSET_HOURS = -3;
    private const int HORA_ENVIO = 8;   // 08:00 hora Argentina. Cambiar acá si se quiere otra hora.
    private const string LastSentKey = "resumen.financiero.last_sent";

    public ResumenFinancieroDiarioService(IServiceScopeFactory scopeFactory, ILogger<ResumenFinancieroDiarioService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(FirstDelay, stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "[ResumenFinanciero] error en el ciclo (no critico)"); }
            try { await Task.Delay(Period, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var argNow = DateTime.UtcNow.AddHours(ARG_OFFSET_HOURS);
        if (argNow.Hour < HORA_ENVIO) return;   // todavía no es la hora de hoy
        var hoyStr = argNow.ToString("yyyy-MM-dd");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Traba diaria: si ya lo mandé hoy, no repito (aunque la app se reinicie).
        var latch = await db.AppSettings.FindAsync(new object?[] { LastSentKey }, ct);
        if (latch is not null && latch.Value == hoyStr) return;

        var notifier = scope.ServiceProvider.GetRequiredService<ResumenFinancieroNotifier>();
        var (ok, detalle) = await notifier.EnviarResumenAsync(ct);
        if (!ok)
        {
            // No marco enviado: si todavía no hay bot vinculado, reintenta en los próximos ticks.
            _logger.LogInformation("[ResumenFinanciero] no se envió: {Detalle}", detalle);
            return;
        }

        if (latch is null) db.AppSettings.Add(new AppSetting { Key = LastSentKey, Value = hoyStr });
        else latch.Value = hoyStr;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("[ResumenFinanciero] resumen enviado");
    }
}
