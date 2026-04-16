using System.ComponentModel.DataAnnotations.Schema;

namespace FamilyGuardian.Api.Data;

/// <summary>
/// Result type cho sp_GetGuardianChildren
/// </summary>
public class ChildSpResult
{
    [Column("id")]                    public int Id { get; set; }
    [Column("full_name")]             public string FullName { get; set; } = null!;
    [Column("email")]                 public string Email { get; set; } = null!;
    [Column("avatar_url")]            public string? AvatarUrl { get; set; }
    [Column("is_active")]             public bool IsActive { get; set; }
    [Column("created_at")]            public DateTime CreatedAt { get; set; }
    [Column("is_online")]             public bool IsOnline { get; set; }
    [Column("last_seen_at")]          public DateTime? LastSeenAt { get; set; }
    [Column("ip_address")]            public string? IpAddress { get; set; }
    [Column("active_websites_count")] public int ActiveWebsitesCount { get; set; }
    [Column("today_total_seconds")]   public int TodayTotalSeconds { get; set; }
}

/// <summary>
/// Result type cho sp_GetChildAllowedWebsites
/// </summary>
public class AllowedWebsiteSpResult
{
    [Column("id")]                  public int Id { get; set; }
    [Column("domain")]              public string Domain { get; set; } = null!;
    [Column("display_name")]        public string? DisplayName { get; set; }
    [Column("favicon_url")]         public string? FaviconUrl { get; set; }
    [Column("is_active")]           public bool IsActive { get; set; }
    [Column("time_limit_minutes")]  public int? TimeLimitMinutes { get; set; }
    [Column("allowed_start_time")]  public TimeSpan? AllowedStartTime { get; set; }
    [Column("allowed_end_time")]    public TimeSpan? AllowedEndTime { get; set; }
    [Column("is_verified")]         public bool IsVerified { get; set; }
    [Column("is_safe")]             public bool? IsSafe { get; set; }
    [Column("http_status_code")]    public int? HttpStatusCode { get; set; }
    [Column("last_checked_at")]     public DateTime? LastCheckedAt { get; set; }
    [Column("created_at")]          public DateTime CreatedAt { get; set; }
    [Column("today_seconds")]       public int TodaySeconds { get; set; }
    [Column("today_requests")]      public int TodayRequests { get; set; }
    [Column("limit_exceeded")]      public bool LimitExceeded { get; set; }
}

/// <summary>
/// Result type cho sp_CheckWebAccess
/// </summary>
public class CheckWebAccessSpResult
{
    [Column("access_result")]       public string AccessResult { get; set; } = null!; // 'allowed' or 'blocked'
    [Column("allowed_website_id")]  public int? AllowedWebsiteId { get; set; }
    [Column("reason")]               public string? Reason { get; set; }
}

/// <summary>
/// Result type cho sp_GetUsageHistory
/// </summary>
public class UsageHistorySpResult
{
    [Column("usage_date")]          public DateOnly UsageDate { get; set; }
    [Column("domain")]              public string Domain { get; set; } = null!;
    [Column("display_name")]        public string? DisplayName { get; set; }
    [Column("favicon_url")]         public string? FaviconUrl { get; set; }
    [Column("total_seconds")]       public int TotalSeconds { get; set; }
    [Column("request_count")]       public int RequestCount { get; set; }
    [Column("time_limit_minutes")]  public int? TimeLimitMinutes { get; set; }
    [Column("limit_exceeded")]      public bool LimitExceeded { get; set; }
}

/// <summary>
/// Result type cho sp_GetAccessLogs
/// </summary>
public class AccessLogSpResult
{
    [Column("id")]               public long Id { get; set; }
    [Column("domain")]           public string Domain { get; set; } = null!;
    [Column("full_url")]         public string? FullUrl { get; set; }
    [Column("access_result")]    public string AccessResult { get; set; } = null!;
    [Column("duration_seconds")] public int DurationSeconds { get; set; }
    [Column("session_start")]    public DateTime SessionStart { get; set; }
    [Column("session_end")]      public DateTime? SessionEnd { get; set; }
    [Column("display_name")]     public string? DisplayName { get; set; }
    [Column("favicon_url")]      public string? FaviconUrl { get; set; }
}

/// <summary>
/// Result type cho sp_AdminGetStats
/// </summary>
public class AdminStatsSpResult
{
    [Column("total_guardians")] public int TotalGuardians { get; set; }
    [Column("total_children")]  public int TotalChildren { get; set; }
    [Column("online_users")]    public int OnlineUsers { get; set; }
    [Column("total_rules")]     public int TotalRules { get; set; }
    [Column("today_requests")]  public int TodayRequests { get; set; }
    [Column("today_blocked")]   public int TodayBlocked { get; set; }
}
