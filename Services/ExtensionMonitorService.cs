using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Hubs;
using FamilyGuardian.Api.Models.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Services;

public class ExtensionMonitorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly ILogger<ExtensionMonitorService> _logger;

    public ExtensionMonitorService(
        IServiceProvider serviceProvider,
        IHubContext<NotificationHub> hubContext,
        ILogger<ExtensionMonitorService> logger)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckExtensions(); }
            catch (Exception ex) { _logger.LogError(ex, "Error in ExtensionMonitorService"); }

            await Task.Delay(10_000, stoppingToken); // Kiểm tra mỗi 10 giây
        }
    }

    private async Task CheckExtensions()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Con nào extension đang active nhưng ping đã ngừng > 20 giây
        var threshold = DateTime.UtcNow.AddSeconds(-20);

        var offlineChildren = await db.UserOnlineStatuses
            .Where(o => o.ExtensionActive == true && o.ExtensionLastSeen < threshold)
            .Include(o => o.User)
            .ToListAsync();

        foreach (var status in offlineChildren)
        {
            // Đánh dấu extension đã tắt
            status.ExtensionActive = false;
            await db.SaveChangesAsync();

            _logger.LogWarning("Extension offline: Child={ChildId} ({Name})",
                status.UserId, status.User?.FullName);

            // Tìm tất cả guardian của con này
            var guardianIds = await db.GuardianChildRelationships
                .Where(r => r.ChildId == status.UserId)
                .Select(r => r.GuardianId)
                .ToListAsync();

            foreach (var guardianId in guardianIds)
            {
                // ✅ Lưu vào bảng notifications
                var notification = new Notification
                {
                    GuardianId = guardianId,
                    ChildId = status.UserId,
                    Title = "⚠️ Extension bị tắt",
                    Message = $"{status.User?.FullName ?? "Con"} vừa tắt extension bộ lọc web",
                    Type = NotificationType.Warning,
                    IsRead = false,
                    CreatedAt = DateTime.UtcNow,
                    SentAt = DateTime.UtcNow
                };
                db.Notifications.Add(notification);
                await db.SaveChangesAsync();

                _logger.LogInformation(
                    "Notification saved: Guardian={GuardianId}, Child={ChildId}, NotificationId={NotifId}",
                    guardianId, status.UserId, notification.Id);

                // ✅ Push SignalR tới guardian
                await _hubContext.Clients
                    .Group($"guardian_{guardianId}")
                    .SendAsync("ExtensionOffline", new
                    {
                        childId = status.UserId,
                        childName = status.User?.FullName ?? "Con",
                        detectedAt = DateTime.UtcNow,
                        notificationId = notification.Id
                    });
            }
        }
    }
}