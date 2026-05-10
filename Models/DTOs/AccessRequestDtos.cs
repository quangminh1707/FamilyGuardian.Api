using System.ComponentModel.DataAnnotations;

namespace FamilyGuardian.Api.Models.DTOs;

public class AccessRequestDto
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public string ChildName { get; set; } = string.Empty;
    public string? ChildAvatarUrl { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string? FullUrl { get; set; }
    public string Reason { get; set; } = "not_in_whitelist";
    public int? RequestedDurationMinutes { get; set; }
    public string? RequestedStartTime { get; set; }
    public string? RequestedEndTime { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public DateTime? TempExpiresAt { get; set; }
}

public class RespondAccessRequestDto
{
    // "approve_temp" | "approve_permanent" | "reject" | "extend_time" | "approve_internet"
    [Required]
    public string Action { get; set; } = string.Empty;

    // Dùng cho approve_temp / extend_time / approve_permanent
    public int? DurationMinutes { get; set; }
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
}

public class RequestAccessDto
{
    public string Domain { get; set; } = string.Empty;
    public string? FullUrl { get; set; }
    public string Reason { get; set; } = "not_in_whitelist";
    public int? RequestedDurationMinutes { get; set; }
    public string? RequestedStartTime { get; set; }
    public string? RequestedEndTime { get; set; }
}
