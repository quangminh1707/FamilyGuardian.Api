namespace FamilyGuardian.Api.Models.DTOs.Children;

public class ChildDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string? AvatarUrl { get; set; }
    public bool IsOnline { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public bool FilterEnabled { get; set; }  
    public int ActiveWebsitesCount { get; set; }
    public int TodayTotalSeconds { get; set; }
}

public class ChildDetailDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string? AvatarUrl { get; set; }
    public bool IsOnline { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public string? IpAddress { get; set; }
    public bool FilterEnabled { get; set; }  
    public List<AllowedWebsiteDto> AllowedWebsites { get; set; } = [];
    public List<ProxyIpMappingDto> ProxyIpMappings { get; set; } = [];
    public int TodayTotalSeconds { get; set; }
}

public class AllowedWebsiteDto
{
    public int Id { get; set; }
    public string Domain { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string? FaviconUrl { get; set; }
    public bool IsActive { get; set; }
    public int? TimeLimitMinutes { get; set; }
    public string? AllowedStartTime { get; set; }
    public string? AllowedEndTime { get; set; }
    public bool IsVerified { get; set; }
    public bool IsSafe { get; set; }
    public int? HttpStatusCode { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    public int TodaySeconds { get; set; }
    public int TodayRequests { get; set; }
    public bool LimitExceeded { get; set; }
}

public class ProxyIpMappingDto
{
    public int Id { get; set; }
    public string IpAddress { get; set; } = null!;
    public string? DeviceName { get; set; }
}

public class AddIpMappingRequest
{
    public string IpAddress { get; set; } = null!;
    public string? DeviceName { get; set; }
}
