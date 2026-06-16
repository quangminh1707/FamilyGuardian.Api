using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FamilyGuardian.Api.Models.Entities;

public class Notification
{
    public int Id { get; set; }
    public int GuardianId { get; set; }
    public int ChildId { get; set; }
    public string Title { get; set; } = null!;
    public string Message { get; set; } = null!;
    public NotificationType Type { get; set; } = NotificationType.Reminder;

    [Column("notification_type")]
    [MaxLength(50)]
    public string? AlertType { get; set; }

    public bool IsRead { get; set; } = false;
    public DateTime? ScheduledAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public User Guardian { get; set; } = null!;
    public User Child { get; set; } = null!;
}

public enum NotificationType { Reminder, Warning, Info }
