using Microsoft.AspNetCore.SignalR;

namespace Api.Hubs;

/// <summary>
/// 2026-08-06: Barredora en segundo plano de la presencia de WhatsApp. Cada ~10 s saca a los que
/// no mandan heartbeat hace más de 30 s (pestaña colgada / sin internet) y apaga el "escribiendo…"
/// que quedó pegado. Reemite la presencia de las conversaciones que cambiaron.
/// </summary>
public class PresenceSweeper : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);         // sin heartbeat 30 s → afuera
    private static readonly TimeSpan TypingTtl = TimeSpan.FromSeconds(6);    // "escribiendo" pegado 6 s → apagar

    private readonly PresenceTracker _tracker;
    private readonly IHubContext<PresenceHub> _hub;
    private readonly ILogger<PresenceSweeper> _logger;

    public PresenceSweeper(PresenceTracker tracker, IHubContext<PresenceHub> hub, ILogger<PresenceSweeper> logger)
    {
        _tracker = tracker;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Intervalo, stoppingToken);
                foreach (var convId in _tracker.Sweep(Ttl, TypingTtl))
                {
                    var viewers = _tracker.Viewers(convId)
                        .Select(v => new { userId = v.UserId, userName = v.UserName, isTyping = v.IsTyping })
                        .ToList();
                    await _hub.Clients.Group($"conv-{convId}").SendAsync("Presence", convId, viewers, stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "PresenceSweeper: error barriendo presencia"); }
        }
    }
}
