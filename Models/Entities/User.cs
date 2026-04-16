namespace FamilyGuardian.Api.Models.Entities;

public class User
{
    public int Id { get; set; }
    public string? GoogleId { get; set; }        // từ Google OAuth payload.Subject
    public string Email { get; set; } = null!;
    public string FullName { get; set; } = null!;
    public string? AvatarUrl { get; set; }       // từ Google payload.Picture
    public UserRole Role { get; set; } = UserRole.Guardian;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public UserOnlineStatus? OnlineStatus { get; set; }
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
    public ICollection<GuardianChildRelationship> AsGuardian { get; set; } = [];
    public ICollection<GuardianChildRelationship> AsChild { get; set; } = [];
    public ICollection<AllowedWebsite> AllowedWebsites { get; set; } = [];
    public ICollection<WebAccessLog> AccessLogs { get; set; } = [];
    public ICollection<Notification> SentNotifications { get; set; } = [];
    public ICollection<Notification> ReceivedNotifications { get; set; } = [];
}

public enum UserRole { Admin, Guardian, Child }
