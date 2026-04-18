using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Helpers;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Proxy;

/// <summary>
/// Check quyền truy cập của child tới domain
/// </summary>
public class ProxyAccessChecker : IProxyAccessChecker
{
    private readonly AppDbContext _db;

    public ProxyAccessChecker(AppDbContext db)
    {
        _db = db;
    }

    public async Task<AccessCheckResult> CheckAccessAsync(string clientIp, string domain, CancellationToken ct = default)
    {
        // 1. Tìm IP mapping → lấy ChildId + GoogleId
        var ipMapping = await _db.ProxyIpMappings
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.IpAddress == clientIp && m.IsActive, cancellationToken: ct);

        if (ipMapping == null)
        {
            return new AccessCheckResult
            {
                Decision = AccessDecision.Blocked,
                Reason = $"IP {clientIp} chưa được đăng ký"
            };
        }

        var childId = ipMapping.ChildId;

        // 2. Kiểm tra child có GoogleId không (Google account-based access)
        // Nếu không có GoogleId → chặn tất cả
        if (string.IsNullOrEmpty(ipMapping.GoogleId))
        {
            return new AccessCheckResult
            {
                Decision = AccessDecision.Blocked,
                ChildId = childId,
                Reason = $"Tài khoản Google chưa được cải hạ"
            };
        }

        // 3. Normalize domain
        var normalizedDomain = DomainNormalizer.Normalize(domain);

        // 4. Tìm allowed_websites khớp domain (exact + subdomain)
        var allowedWebsite = await _db.AllowedWebsites
            .AsNoTracking()
            .FirstOrDefaultAsync(w =>
                w.ChildId == childId &&
                w.IsActive &&
                (w.Domain == normalizedDomain || normalizedDomain.EndsWith("." + w.Domain)),
            cancellationToken: ct);

        if (allowedWebsite == null)
        {
            return new AccessCheckResult
            {
                Decision = AccessDecision.Blocked,
                ChildId = childId,
                Reason = $"Domain {normalizedDomain} không có trong danh sách cho phép"
            };
        }

        // 5. Kiểm tra khung giờ
        if (allowedWebsite.AllowedStartTime.HasValue && allowedWebsite.AllowedEndTime.HasValue)
        {
            var currentTime = TimeOnly.FromDateTime(DateTime.Now);
            if (currentTime < allowedWebsite.AllowedStartTime || currentTime > allowedWebsite.AllowedEndTime)
            {
                return new AccessCheckResult
                {
                    Decision = AccessDecision.Blocked,
                    ChildId = childId,
                    AllowedWebsiteId = allowedWebsite.Id,
                    Reason = $"Ngoài khung giờ ({allowedWebsite.AllowedStartTime:HH:mm} - {allowedWebsite.AllowedEndTime:HH:mm})"
                };
            }
        }

        // 6. Kiểm tra time limit
        if (allowedWebsite.TimeLimitMinutes.HasValue && allowedWebsite.TimeLimitMinutes > 0)
        {
            var usageToday = await _db.DailyUsageStats
                .AsNoTracking()
                .Where(d =>
                    d.ChildId == childId &&
                    d.AllowedWebsiteId == allowedWebsite.Id &&
                    d.UsageDate == DateOnly.FromDateTime(DateTime.Today))
                .Select(d => d.TotalSeconds)
                .FirstOrDefaultAsync(cancellationToken: ct);

            var limitSeconds = allowedWebsite.TimeLimitMinutes.Value * 60;
            if (usageToday >= limitSeconds)
            {
                return new AccessCheckResult
                {
                    Decision = AccessDecision.Blocked,
                    ChildId = childId,
                    AllowedWebsiteId = allowedWebsite.Id,
                    Reason = $"Đã vượt quá thời gian cho phép ({allowedWebsite.TimeLimitMinutes} phút)"
                };
            }
        }

        // ✅ Cho phép truy cập
        return new AccessCheckResult
        {
            Decision = AccessDecision.Allowed,
            ChildId = childId,
            AllowedWebsiteId = allowedWebsite.Id,
            Reason = "Được phép truy cập"
        };
    }
}
