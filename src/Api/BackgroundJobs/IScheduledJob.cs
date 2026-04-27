namespace Api.BackgroundJobs;

public interface IScheduledJob
{
    string Code { get; }
    Task<string> ExecuteAsync(CancellationToken cancellationToken);
}
