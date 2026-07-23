using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>Servicio de fondo que manda por Telegram, una vez por día a las 08:00 (hora Argentina),
/// el resumen de lo que debe cada cliente. No usa la tabla ScheduledProcesses: se auto-agenda con
/// una traba diaria guardada en AppSettings (clave "deudores.diario.last_sent"), así se despliega
/// solo sin tener que sembrar filas en la base (dev y prod).</summary>
public class DeudoresDiarioService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeudoresDiarioService> _logger;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
    private const int ARG_OFFSET_HOURS = -3;
    private const string LastSentKey = "deudores.diario.last_sent";

    public DeudoresDiarioService(IServiceScopeFactory scopeFactory, ILogger<DeudoresDiarioService> logger)
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
            catch (Exception ex) { _logger.LogWarning(ex, "[DeudoresDiario] error en el ciclo (no critico)"); }
            try { await Task.Delay(Period, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var argNow = DateTime.UtcNow.AddHours(ARG_OFFSET_HOURS);
        var hoyStr = argNow.ToString("yyyy-MM-dd");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 2026-07-23 (Centro de Automatizaciones): interruptor + días + hora configurables
        var cfg = await db.AutoConfigs.AsNoTracking().FirstOrDefaultAsync(x => x.AutoKey == "deudas-diario", ct);
        if (cfg is null || !cfg.Enabled) return;          // apagada desde la pantalla
        if (!cfg.CorreHoy(argNow)) return;                // hoy no es uno de los días elegidos
        if (argNow.Hour < cfg.Hora) return;               // todavía no es la hora

        // Traba diaria: si ya lo mandé hoy, no repito (aunque la app se reinicie).
        var latch = await db.AppSettings.FindAsync(new object?[] { LastSentKey }, ct);
        if (latch is not null && latch.Value == hoyStr) return;

        var notifier = scope.ServiceProvider.GetRequiredService<DeudoresDiarioNotifier>();
        var res = await notifier.EnviarResumenAsync(ct);
        if (!res.Ok)
        {
            // No marco enviado: si todavía no hay bot vinculado, reintenta en los próximos ticks.
            _logger.LogInformation("[DeudoresDiario] no se envió: {Detalle}", res.Detalle);
            return;
        }

        if (latch is null) db.AppSettings.Add(new AppSetting { Key = LastSentKey, Value = hoyStr });
        else latch.Value = hoyStr;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("[DeudoresDiario] resumen enviado ({Clientes} clientes, {Mensajes} mensajes)", res.Clientes, res.Mensajes);
    }
}
