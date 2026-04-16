using System.ComponentModel.DataAnnotations;

namespace FamilyGuardian.Api.Models.Entities;

public class WebsiteCheckCache
{
    [Key]
    public string Domain { get; set; } = null!;
    public bool? IsReachable { get; set; }
    public int? HttpStatusCode { get; set; }
    public int? ResponseTimeMs { get; set; }
    public bool? IsSafe { get; set; }
    public string? ThreatType { get; set; }
    public string? FaviconUrl { get; set; }
    public string? DisplayName { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
}
