using System.Text.Json;
using Api.Services;

namespace Api.BackgroundJobs;

public class SyncMeliItemsJob : IScheduledJob
{
    public string Code => "SyncMeliItems";

    private readonly IServiceScopeFactory _scopeFactory;

    public SyncMeliItemsJob(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<string> ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<MeliItemService>();

        var result = await service.SyncItemsAsync();

        return JsonSerializer.Serialize(new
        {
            sincronizados = result.TotalSynced,
            errores = result.TotalErrors
        });
    }
}
