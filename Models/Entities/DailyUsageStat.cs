namespace FamilyGuardian.Api.Models.Entities;

public class DailyUsageStat
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public int AllowedWebsiteId { get; set; }
    public string Domain { get; set; } = null!;
    public DateOnly UsageDate { get; set; }
    public int TotalSeconds { get; set; } = 0;
    public int RequestCount { get; set; } = 0;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    public User Child { get; set; } = null!;
    public AllowedWebsite Website { get; set; } = null!;
}
