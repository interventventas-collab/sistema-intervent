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

        // Red de seguridad del respondedor post-venta de café: por si el webbook no llegó,
        // barre las ventas elegidas sin avisar y les manda el mensaje de molienda.
        int postventaCafe = 0;
        try { postventaCafe = await service.EnviarPostventaCafePendientesAsync(cancellationToken); }
        catch { /* no fatal */ }

        return JsonSerializer.Serialize(new
        {
            sincronizados = result.TotalSynced,
            errores = result.TotalErrors,
            postventa_cafe = postventaCafe
        });
    }
}
