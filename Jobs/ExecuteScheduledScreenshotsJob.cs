using FamilyGuardian.Api.Services.Interfaces;
using Quartz;

namespace FamilyGuardian.Api.Jobs;

[DisallowConcurrentExecution]
public class ExecuteScheduledScreenshotsJob : IJob
{
    private readonly IScreenshotService _screenshotService;
    private readonly ILogger<ExecuteScheduledScreenshotsJob> _logger;

    public ExecuteScheduledScreenshotsJob(
        IScreenshotService screenshotService,
        ILogger<ExecuteScheduledScreenshotsJob> logger)
    {
        _screenshotService = screenshotService;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogDebug("ExecuteScheduledScreenshotsJob running");
        await _screenshotService.ExecutePendingScheduledAsync();
    }
}
