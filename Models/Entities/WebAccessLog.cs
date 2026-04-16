namespace FamilyGuardian.Api.Models.Entities;

public class WebAccessLog
{
    public long Id { get; set; }
    public int ChildId { get; set; }
    public string Domain { get; set; } = null!;
    public string? FullUrl { get; set; }
    public AccessResult AccessResult { get; set; }
    public int? AllowedWebsiteId { get; set; }
    public DateTime SessionStart { get; set; } = DateTime.UtcNow;
    public DateTime? SessionEnd { get; set; }
    public int DurationSeconds { get; set; } = 0;

    public User Child { get; set; } = null!;
    public AllowedWebsite? Website { get; set; }
}

public enum AccessResult { Allowed, Blocked }
