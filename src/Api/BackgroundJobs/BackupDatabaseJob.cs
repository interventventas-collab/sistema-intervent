using System.Text.Json;
using Api.Services;

namespace Api.BackgroundJobs;

public class BackupDatabaseJob : IScheduledJob
{
    public string Code => "BackupDatabase";

    private readonly IServiceScopeFactory _scopeFactory;

    public BackupDatabaseJob(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<string> ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var backups = scope.ServiceProvider.GetRequiredService<BackupService>();

        var backup = await backups.CreateBackupAsync("Programado", null, cancellationToken);
        var cleaned = await backups.CleanupOldAsync(cancellationToken);

        return JsonSerializer.Serialize(new
        {
            archivo = backup.FileName,
            tamanoBytes = backup.SizeBytes,
            estado = backup.Status,
            antiguosEliminados = cleaned
        });
    }
}
