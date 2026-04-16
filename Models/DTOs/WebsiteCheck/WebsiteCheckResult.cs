namespace FamilyGuardian.Api.Models.DTOs.WebsiteCheck;

public class WebsiteCheckResult
{
    public string Domain { get; set; } = null!;
    public bool IsReachable { get; set; }
    public int? HttpStatusCode { get; set; }
    public int? ResponseTimeMs { get; set; }
    public bool IsSafe { get; set; }
    public string? ThreatType { get; set; }
    public string? FaviconUrl { get; set; }
    public string? DisplayName { get; set; }
    public DateTime CheckedAt { get; set; }
}
