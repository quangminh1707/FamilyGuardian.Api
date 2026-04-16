using FamilyGuardian.Api.Services.Interfaces;
using Quartz;

namespace FamilyGuardian.Api.Jobs;

[DisallowConcurrentExecution]
public class SendScheduledNotificationsJob : IJob
{
    private readonly INotificationService _notificationService;
    private readonly ILogger<SendScheduledNotificationsJob> _logger;

    public SendScheduledNotificationsJob(INotificationService notificationService,
        ILogger<SendScheduledNotificationsJob> logger)
    {
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        // Bỏ qua nếu app đang shutdown
        if (context.CancellationToken.IsCancellationRequested)
            return;

        try
        {
            _logger.LogDebug("Running SendScheduledNotificationsJob");
            await _notificationService.SendScheduledNotificationsAsync();
        }
        catch (OperationCanceledException)
        {
            // Bình thường khi app shutdown – không log error
        }
        catch (Exception ex)
        {
            // Log lỗi nhưng KHÔNG propagate để Quartz tiếp tục chạy lần sau
            // (thường gặp khi DB chưa được setup hoặc kết nối tạm thời mất)
            _logger.LogWarning(ex,
                "SendScheduledNotificationsJob failed (DB chưa ready hoặc lỗi tạm thời). " +
                "Sẽ thử lại vào lần chạy tiếp theo.");
        }
    }
}
