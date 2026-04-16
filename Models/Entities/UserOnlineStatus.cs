using System.ComponentModel.DataAnnotations;

namespace FamilyGuardian.Api.Models.Entities;

public class UserOnlineStatus
{
    [Key]
    public int UserId { get; set; }
    public bool IsOnline { get; set; } = false;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public string? IpAddress { get; set; }
    public string? DeviceInfo { get; set; }

    public User User { get; set; } = null!;
}
