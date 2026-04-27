using System.Text.Json;
using Api.Services;

namespace Api.BackgroundJobs;

public class SyncMeliOrdersJob : IScheduledJob
{
    public string Code => "SyncMeliOrders";

    private readonly IServiceScopeFactory _scopeFactory;

    public SyncMeliOrdersJob(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<string> ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<MeliOrderService>();

        var from = DateTime.UtcNow.AddDays(-7);
        var to = DateTime.UtcNow;
        var result = await service.SyncOrdersAsync(from, to);

        return JsonSerializer.Serialize(new
        {
            sincronizados = result.TotalSynced,
            errores = result.TotalErrors
        });
    }
}
