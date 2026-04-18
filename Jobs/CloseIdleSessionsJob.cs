using FamilyGuardian.Api.Proxy;
using Quartz;

namespace FamilyGuardian.Api.Jobs;

[DisallowConcurrentExecution]
public class CloseIdleSessionsJob : IJob
{
    private readonly ISessionTracker _tracker;
    private readonly ILogger<CloseIdleSessionsJob> _logger;

    public CloseIdleSessionsJob(ISessionTracker tracker, ILogger<CloseIdleSessionsJob> logger)
    {
        _tracker = tracker;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            _logger.LogInformation("Executing CloseIdleSessionsJob...");
            await _tracker.CloseIdleSessionsAsync(context.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing CloseIdleSessionsJob");
        }
    }
}
