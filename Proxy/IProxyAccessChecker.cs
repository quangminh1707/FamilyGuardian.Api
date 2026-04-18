namespace FamilyGuardian.Api.Proxy;

/// <summary>
/// Kết quả kiểm tra quyền truy cập của child tới domain
/// </summary>
public class AccessCheckResult
{
    public AccessDecision Decision { get; set; }
    public int? ChildId { get; set; }
    public int? AllowedWebsiteId { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public enum AccessDecision
{
    Allowed,
    Blocked
}

/// <summary>
/// Interface để check xem child có được phép truy cập domain không
/// </summary>
public interface IProxyAccessChecker
{
    /// <summary>
    /// Kiểm tra xem request từ IP có được phép truy cập domain hay không.
    /// Logic:
    /// 1. Tìm proxy_ip_mappings theo clientIp → lấy childId
    /// 2. Tìm allowed_websites của childId khớp domain (exact + subdomain)
    /// 3. Kiểm tra khung giờ
    /// 4. Kiểm tra time limit từ daily_usage_stats
    /// 5. Trả kết quả
    /// </summary>
    Task<AccessCheckResult> CheckAccessAsync(string clientIp, string domain, CancellationToken ct = default);
}
