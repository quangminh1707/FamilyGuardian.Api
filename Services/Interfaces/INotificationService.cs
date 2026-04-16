using FamilyGuardian.Api.Models.DTOs.Notifications;

namespace FamilyGuardian.Api.Services.Interfaces;

public interface INotificationService
{
    Task<List<NotificationDto>> GetUnreadNotificationsAsync(int userId);
    Task<List<NotificationDto>> GetNotificationHistoryAsync(int userId, int page, int pageSize);
    Task CreateNotificationAsync(int guardianId, int childId, CreateNotificationRequest request);
    Task MarkAsReadAsync(int userId, int notificationId);
    Task MarkAllAsReadAsync(int userId);
    Task SendScheduledNotificationsAsync();
}
