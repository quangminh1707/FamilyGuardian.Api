namespace FamilyGuardian.Api.Proxy;

/// <summary>
/// Interface để track web sessions của child
/// </summary>
public interface ISessionTracker
{
    /// <summary>
    /// Ghi nhận request tới domain từ child
    /// Logic:
    /// - Giữ cache in-memory (Dictionary) lastSeen per (childId, websiteId)
    /// - Nếu request trong 5 phút → cộng seconds vào daily_usage_stats
    /// - Nếu mới / sau idle → tạo WebSession record mới
    /// </summary>
    Task RecordRequestAsync(int childId, int websiteId, string domain, string clientIp, CancellationToken ct = default);

    /// <summary>
    /// Đóng các session idle > 5 phút
    /// Logic:
    /// - Query WebSessions có EndedAt = null VÀ LastActivityAt < (now - 5min)
    /// - Set EndedAt = LastActivityAt, tính DurationSeconds
    /// </summary>
    Task CloseIdleSessionsAsync(CancellationToken ct = default);
}
