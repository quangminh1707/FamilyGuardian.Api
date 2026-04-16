namespace FamilyGuardian.Api.Models.DTOs.Logs;

public class AccessLogDto
{
    public long Id { get; set; }
    public string Domain { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string? FaviconUrl { get; set; }
    public string? FullUrl { get; set; }
    public string AccessResult { get; set; } = null!; // "allowed" or "blocked"
    public int DurationSeconds { get; set; }
    public DateTime SessionStart { get; set; }
    public DateTime? SessionEnd { get; set; }
}

public class DailyUsageDto
{
    public string Domain { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string? FaviconUrl { get; set; }
    public int TotalSeconds { get; set; }
    public int RequestCount { get; set; }
    public int? TimeLimitMinutes { get; set; }
    public bool LimitExceeded { get; set; }
    public double? UsagePercent { get; set; }
    public string UsageDate { get; set; } = null!;
}

public class UsageHistoryDto
{
    public string Date { get; set; } = null!;
    public int TotalSeconds { get; set; }
    public List<DailyUsageDto> Websites { get; set; } = [];
}
