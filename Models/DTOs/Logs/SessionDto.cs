namespace FamilyGuardian.Api.Models.DTOs.Logs;

/// <summary>
/// DTO cho lịch sử phiên truy cập web (web_sessions)
/// </summary>
public class SessionDto
{
    public long Id { get; set; }
    public string Domain { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string? FaviconUrl { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int DurationSeconds { get; set; }
    public bool IsActive { get; set; }
}
