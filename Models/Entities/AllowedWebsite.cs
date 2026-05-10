namespace FamilyGuardian.Api.Models.Entities;

public class AllowedWebsite
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public string Domain { get; set; } = null!;          // youtube.com (normalized)
    public string? DisplayName { get; set; }              // YouTube
    public string? FaviconUrl { get; set; }               // https://www.google.com/s2/favicons?domain=youtube.com
    public bool IsActive { get; set; } = true;
    public int? TimeLimitMinutes { get; set; }            // null = không giới hạn
    public TimeOnly? AllowedStartTime { get; set; }       // 07:00
    public TimeOnly? AllowedEndTime { get; set; }         // 21:00
    // Kết quả kiểm tra
    public bool IsVerified { get; set; } = false;
    public bool? IsSafe { get; set; }
    public int? HttpStatusCode { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    // Meta
    public int AddedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? TempExpiresAt { get; set; }  // Feature 2: Temp access expiration

    public User Child { get; set; } = null!;
    public User Guardian { get; set; } = null!;
    public ICollection<DailyUsageStat> DailyStats { get; set; } = [];
    public ICollection<WebAccessLog> AccessLogs { get; set; } = [];
}
