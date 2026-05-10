using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Hubs;
using FamilyGuardian.Api.Models.DTOs.Notifications;
using FamilyGuardian.Api.Models.Entities;
using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Services;

public class NotificationService : INotificationService
{
    private readonly AppDbContext _db;
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(AppDbContext db, IHubContext<NotificationHub> hubContext, ILogger<NotificationService> logger)
    {
        _db = db;
        _hubContext = hubContext;
        _logger = logger;
    }

    private IQueryable<Notification> BuildNotificationQuery(int userId, string filter)
    {
        var query = _db.Notifications.Where(n => n.GuardianId == userId);

        filter = string.IsNullOrWhiteSpace(filter) ? "all" : filter.Trim().ToLowerInvariant();
        if (filter == "unread")
            query = query.Where(n => !n.IsRead);
        else if (filter == "read")
            query = query.Where(n => n.IsRead);

        return query;
    }

    public async Task<List<NotificationDto>> GetNotificationsAsync(int userId, string filter, int page, int pageSize)
    {
        return await BuildNotificationQuery(userId, filter)
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new NotificationDto
            {
                Id = n.Id,
                Title = n.Title,
                Message = n.Message,
                Type = n.Type.ToString().ToLower(),
                IsRead = n.IsRead,
                ScheduledAt = n.ScheduledAt,
                SentAt = n.SentAt,
                CreatedAt = n.CreatedAt
            }).ToListAsync();
    }

    public async Task<List<NotificationDto>> GetUnreadNotificationsAsync(int userId)
    {
        return await _db.Notifications
            .Where(n => n.GuardianId == userId
                && !n.IsRead
                && (n.ScheduledAt == null || n.ScheduledAt <= DateTime.Now))
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new NotificationDto
            {
                Id = n.Id,
                Title = n.Title,
                Message = n.Message,
                Type = n.Type.ToString().ToLower(),
                IsRead = n.IsRead,
                ScheduledAt = n.ScheduledAt,
                SentAt = n.SentAt,
                CreatedAt = n.CreatedAt
            }).ToListAsync();
    }

    public async Task<List<NotificationDto>> GetNotificationHistoryAsync(int userId, int page, int pageSize)
    {
        return await GetNotificationsAsync(userId, "all", page, pageSize);
    }

    public async Task CreateNotificationAsync(int guardianId, int childId, CreateNotificationRequest request)
    {
        var type = Enum.TryParse<NotificationType>(request.Type, true, out var t) ? t : NotificationType.Reminder;

        var notification = new Notification
        {
            GuardianId = guardianId,
            ChildId = childId,
            Title = request.Title,
            Message = request.Message,
            Type = type,
            ScheduledAt = request.ScheduledAt,
            CreatedAt = DateTime.Now
        };

        if (notification.ScheduledAt == null || notification.ScheduledAt <= DateTime.Now)
        {
            notification.SentAt = DateTime.Now;
            _db.Notifications.Add(notification);
            await _db.SaveChangesAsync();

            // Gửi realtime tới con
            await _hubContext.Clients.Group($"user_{childId}").SendAsync("ReceiveNotification", new NotificationDto
            {
                Id = notification.Id,
                Title = notification.Title,
                Message = notification.Message,
                Type = notification.Type.ToString().ToLower(),
                CreatedAt = notification.CreatedAt
            });
        }
        else
        {
            _db.Notifications.Add(notification);
            await _db.SaveChangesAsync();
        }
    }

    public async Task MarkAsReadAsync(int userId, int notificationId)
    {
        var notification = await _db.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.GuardianId == userId); // ✅

        if (notification != null)
        {
            notification.IsRead = true;
            await _db.SaveChangesAsync();
        }
    }

    public async Task MarkAllAsReadAsync(int userId)
    {
        var unread = await _db.Notifications
            .Where(n => n.GuardianId == userId && !n.IsRead) // ✅
            .ToListAsync();

        foreach (var n in unread) n.IsRead = true;
        await _db.SaveChangesAsync();
    }

    public async Task SendScheduledNotificationsAsync()
    {
        var pending = await _db.Notifications
            .Where(n => n.SentAt == null && n.ScheduledAt != null && n.ScheduledAt <= DateTime.Now)
            .ToListAsync();

        if (pending.Count == 0) return;

        foreach (var n in pending)
        {
            n.SentAt = DateTime.Now;
            await _hubContext.Clients.Group($"user_{n.ChildId}").SendAsync("ReceiveNotification", new NotificationDto
            {
                Id = n.Id,
                Title = n.Title,
                Message = n.Message,
                Type = n.Type.ToString().ToLower(),
                CreatedAt = n.CreatedAt
            });
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Sent {Count} scheduled notifications.", pending.Count);
    }
}
