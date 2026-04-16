namespace FamilyGuardian.Api.Models.DTOs.Admin;

public class SystemStatsDto
{
    public int TotalGuardians { get; set; }
    public int TotalChildren { get; set; }
    public int OnlineUsers { get; set; }
    public int TotalRules { get; set; }
    public int TodayRequests { get; set; }
    public int TodayBlocked { get; set; }
}

public class UpdateSettingRequest
{
    public string Key { get; set; } = null!;
    public string Value { get; set; } = null!;
}

public class AdminUserDto
{
    public int Id { get; set; }
    public string Email { get; set; } = null!;
    public string FullName { get; set; } = null!;
    public string? AvatarUrl { get; set; }
    public string Role { get; set; } = null!;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}
