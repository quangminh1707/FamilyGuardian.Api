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
    public string Status { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public DateTime? TempExpiresAt { get; set; }
}

public class RespondAccessRequestDto
{
    // "approve_temp" | "approve_permanent" | "reject"
    [Required]
    public string Action { get; set; } = string.Empty;

    // Chỉ dùng khi Action = "approve_temp"
    public int DurationMinutes { get; set; } = 30;
}

public class RequestAccessDto
{
    public string Domain { get; set; } = string.Empty;
    public string? FullUrl { get; set; }
}
