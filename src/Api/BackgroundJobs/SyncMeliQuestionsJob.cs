using System.Text.Json;
using Api.Services;

namespace Api.BackgroundJobs;

public class SyncMeliQuestionsJob : IScheduledJob
{
    public string Code => "SyncMeliQuestions";

    private readonly IServiceScopeFactory _scopeFactory;

    public SyncMeliQuestionsJob(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<string> ExecuteAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<MeliQuestionService>();
        var result = await service.SyncAsync();
        return JsonSerializer.Serialize(new
        {
            sincronizadas = result.TotalSynced,
            nuevas = result.TotalNew,
            errores = result.TotalErrors
        });
    }
}
