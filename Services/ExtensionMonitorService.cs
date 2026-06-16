using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Hubs;
using FamilyGuardian.Api.Models.DTOs.Notifications;
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
            try
            {
                await CheckExtensions();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ExtensionMonitorService");
            }

            try
            {
                await CleanupExpiredTempAccess();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up temp access");
            }

            await Task.Delay(10_000, stoppingToken); // Kiểm tra mỗi 10 giây
        }
    }

    private async Task CheckExtensions()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Con nào extension đang active nhưng ping đã ngừng > 20 giây
        var threshold = DateTime.Now.AddSeconds(-20);

        var offlineChildren = await db.UserOnlineStatuses
            .Where(o => o.ExtensionActive == true && o.ExtensionLastSeen < threshold)
            .Include(o => o.User)
            .ToListAsync();

        foreach (var status in offlineChildren)
        {
            var wasPreviouslyActive =
                status.ExtensionActive
                && status.ExtensionLastSeen.HasValue
                && status.ExtensionLastSeen.Value > DateTime.Now.AddMinutes(-2);

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
                if (wasPreviouslyActive)
                {
                    await CreateTamperAlertAsync(db, status.UserId, guardianId, status.User?.FullName ?? "Con");
                    continue;
                }

                await CreateExtensionOfflineNotificationAsync(db, status.UserId, guardianId, status.User?.FullName ?? "Con");
            }
        }
    }

    private async Task CreateExtensionOfflineNotificationAsync(AppDbContext db, int childId, int guardianId, string childName)
    {
        var notification = new Notification
        {
            GuardianId = guardianId,
            ChildId = childId,
            Title = "⚠️ Extension bị tắt",
            Message = $"{childName} vừa tắt extension bộ lọc web",
            Type = NotificationType.Warning,
            AlertType = "extension_offline",
            IsRead = false,
            CreatedAt = DateTime.Now,
            SentAt = DateTime.Now
        };

        db.Notifications.Add(notification);
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Notification saved: Guardian={GuardianId}, Child={ChildId}, NotificationId={NotifId}",
            guardianId, childId, notification.Id);

        await _hubContext.Clients
            .Group($"guardian_{guardianId}")
            .SendAsync("ExtensionOffline", new
            {
                childId,
                childName,
                detectedAt = DateTime.Now,
                notificationId = notification.Id,
                notificationType = notification.AlertType
            });
    }

    private async Task CreateTamperAlertAsync(AppDbContext db, int childId, int guardianId, string childName)
    {
        var notification = new Notification
        {
            GuardianId = guardianId,
            ChildId = childId,
            Title = "⚠️ Cảnh báo: Tiện ích bị tắt",
            Message = $"Tiện ích Family Guardian trên máy của {childName} vừa bị ngắt kết nối đột ngột. Có thể tiện ích đã bị tắt hoặc gỡ bỏ.",
            Type = NotificationType.Warning,
            AlertType = "tamper_alert",
            IsRead = false,
            CreatedAt = DateTime.Now,
            SentAt = DateTime.Now
        };

        db.Notifications.Add(notification);
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Tamper notification saved: Guardian={GuardianId}, Child={ChildId}, NotificationId={NotifId}",
            guardianId, childId, notification.Id);

        await _hubContext.Clients
            .Group($"guardian_{guardianId}")
            .SendAsync("ReceiveNotification", new NotificationDto
            {
                Id = notification.Id,
                Title = notification.Title,
                Message = notification.Message,
                Type = notification.Type.ToString().ToLower(),
                NotificationType = notification.AlertType,
                IsRead = false,
                CreatedAt = notification.CreatedAt
            });

        await _hubContext.Clients
            .Group($"guardian_{guardianId}")
            .SendAsync("TamperAlert", new
            {
                ChildId = childId,
                ChildName = childName,
                DetectedAt = DateTime.Now
            });
    }

    // ── Feature 2: Cleanup temp access đã hết hạn ─────────────────────────────────
    private async Task CleanupExpiredTempAccess()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var expiredTempAccess = await db.AllowedWebsites
            .Where(w => w.TempExpiresAt != null && w.TempExpiresAt < DateTime.Now)
            .ToListAsync();

        foreach (var w in expiredTempAccess)
        {
            w.IsActive = false;
            w.TempExpiresAt = null;
        }

        if (expiredTempAccess.Any())
        {
            await db.SaveChangesAsync();
            _logger.LogInformation("Cleaned up {Count} expired temp access entries", expiredTempAccess.Count);
        }
    }
}
