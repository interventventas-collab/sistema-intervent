using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.BackgroundJobs;

/// <summary>
/// 2026-07-06: limpieza automática de los adjuntos del chat (fotos/archivos/audios).
/// Una vez por día borra los ARCHIVOS de /data/files/chat de mensajes con más de N días.
/// El mensaje queda (se ve "📎 archivo vencido"); solo se libera el archivo del disco.
/// N configurable en AppSettings["chat.adjuntos.dias"] (default 30 = 1 mes).
/// </summary>
public class ChatAdjuntosCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChatAdjuntosCleanupService> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly string ChatFilesRoot = "/data/files/chat";
    private const int DefaultDias = 30;

    public ChatAdjuntosCleanupService(IServiceScopeFactory scopeFactory,
        ILogger<ChatAdjuntosCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var dias = await GetDiasAsync(db, stoppingToken);
                if (dias > 0)
                {
                    var cutoff = DateTime.UtcNow.AddDays(-dias);
                    var viejos = await db.ChatMensajes
                        .Where(m => m.AdjuntoArchivo != null && m.CreatedAt < cutoff)
                        .Select(m => m.AdjuntoArchivo!)
                        .ToListAsync(stoppingToken);

                    var borrados = 0;
                    foreach (var archivo in viejos)
                    {
                        try
                        {
                            var path = Path.Combine(ChatFilesRoot, archivo);
                            if (File.Exists(path)) { File.Delete(path); borrados++; }
                        }
                        catch { /* archivo trabado o ya no está: seguimos */ }
                    }
                    if (borrados > 0)
                        _logger.LogInformation("Limpieza adjuntos chat: {N} archivos borrados (más de {Dias} días)", borrados, dias);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ChatAdjuntosCleanupService: error en el ciclo de limpieza");
            }
            try { await Task.Delay(Interval, stoppingToken); } catch { break; }
        }
    }

    private static async Task<int> GetDiasAsync(AppDbContext db, CancellationToken ct)
    {
        try
        {
            var s = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == "chat.adjuntos.dias", ct);
            if (s != null && int.TryParse(s.Value, out var n) && n > 0) return n;
        }
        catch { /* si falla la lectura, usamos el default */ }
        return DefaultDias;
    }
}
