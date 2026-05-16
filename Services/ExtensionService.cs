using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Helpers;
using FamilyGuardian.Api.Models;
using FamilyGuardian.Api.Models.Entities;
using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;

namespace FamilyGuardian.Api.Services;

public class ExtensionCheckRequest
{
    public string? Domain { get; set; }
}

public class ExtensionCheckResponse
{
    public bool Allowed { get; set; }
    public string? Reason { get; set; }
    public string? Domain { get; set; }
    public int? AllowedWebsiteId { get; set; }
}

public class ExtensionConfigResponse
{
    public bool FilterEnabled { get; set; }
    public int ChildId { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
}

public class BlockInfoResult
{
    // "internet_paused" | "time_limit_exceeded" | "not_in_whitelist"
    public string Reason { get; set; } = "not_in_whitelist";
    public bool DomainExistsInWhitelist { get; set; }
    public int? CurrentLimitMinutes { get; set; }
    public int? UsedSecondsToday { get; set; }
    public int? RemainingSeconds { get; set; }
    public string? AllowedStartTime { get; set; }
    public string? AllowedEndTime { get; set; }
}

public class HeartbeatResult
{
    public bool LimitExceeded { get; set; } = false;

    /// <summary>Không null = warning kích hoạt ngay heartbeat này</summary>
    public HeartbeatWarning? Warning { get; set; }

    /// <summary>Giây đến warning mốc 1 → extension đặt alarm chính xác</summary>
    public int? SecondsUntilWarning1 { get; set; }
    public string? WarningMessage1 { get; set; }

    /// <summary>Giây đến warning mốc 2</summary>
    public int? SecondsUntilWarning2 { get; set; }
    public string? WarningMessage2 { get; set; }

    /// <summary>Giây còn lại đến khi bị chặn</summary>
    public int? SecondsUntilBlock { get; set; }

    // ── Time Info (mới) ──────────────────────────────────────────────────────
    /// <summary>"10:00 → 12:00" — extension hiển thị overlay</summary>
    public string? TimeWindowDisplay { get; set; }
    /// <summary>Phút còn lại đến cuối khung giờ</summary>
    public int? MinutesUntilWindowEnd { get; set; }
    /// <summary>Phút còn lại hôm nay (giới hạn phút)</summary>
    public int? MinutesRemainingToday { get; set; }
}

public class HeartbeatWarning
{
    public string Message { get; set; } = string.Empty;
    public int RemainingSeconds { get; set; }
}

public class ExtensionService : IExtensionService
{
    private readonly AppDbContext _context;
    private readonly ILogger<ExtensionService> _logger;
    private readonly IHubContext<FamilyGuardian.Api.Hubs.NotificationHub> _hubContext;

    public ExtensionService(
        AppDbContext context,
        ILogger<ExtensionService> logger,
        IHubContext<FamilyGuardian.Api.Hubs.NotificationHub> hubContext)
    {
        _context = context;
        _logger = logger;
        _hubContext = hubContext;
    }

    public async Task<ExtensionCheckResponse> CheckAccessAsync(string googleId, string domain)
    {
        try
        {
            domain = DomainNormalizer.Normalize(domain);

            // Feature 3: Kill Switch — check internet_paused TRƯỚC khi gọi SP
            var user = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.GoogleId == googleId && u.Role == UserRole.Child);
            if (user != null && user.InternetPaused)
            {
                return new ExtensionCheckResponse
                {
                    Allowed = false,
                    Reason = "Internet đang bị tạm dừng bởi phụ huynh",
                    Domain = domain
                };
            }

            var result = await _context.CheckWebAccessSpResults.FromSqlInterpolated(
                $"CALL sp_ExtensionCheckAccess({googleId}, {domain})"
            ).ToListAsync();

            if (result.Count == 0)
            {
                _logger.LogWarning("Stored procedure returned no results for GoogleId={GoogleId}, Domain={Domain}", googleId, domain);
                return new ExtensionCheckResponse
                {
                    Allowed = false,
                    Reason = "Không thể xác định trạng thái - chặn để an toàn",
                    Domain = domain
                };
            }

            var row = result.First();
            bool allowed = row.AccessResult == "allowed";
            string? reason = row.Reason;
            int? websiteId = row.AllowedWebsiteId;

            var isTimeLimitBlock =
                !string.IsNullOrWhiteSpace(reason)
                && (
                    reason.Contains("time_limit_exceeded", StringComparison.OrdinalIgnoreCase)
                    || reason.Contains("hết", StringComparison.OrdinalIgnoreCase)
                    || reason.Contains("limit", StringComparison.OrdinalIgnoreCase)
                );

            if (!allowed
                && websiteId.HasValue
                && isTimeLimitBlock)
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                var stat = await _context.DailyUsageStats
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s =>
                        s.ChildId == user!.Id
                        && s.AllowedWebsiteId == websiteId.Value
                        && s.UsageDate == today);

                if (stat != null && stat.BonusSeconds > 0)
                {
                    var website = await _context.AllowedWebsites
                        .AsNoTracking()
                        .FirstOrDefaultAsync(w => w.Id == websiteId.Value);

                    if (website?.TimeLimitMinutes != null)
                    {
                        var limitSeconds = website.TimeLimitMinutes.Value * 60;
                        var effectiveUsed = Math.Max(0, stat.TotalSeconds - stat.BonusSeconds);
                        if (effectiveUsed < limitSeconds)
                        {
                            allowed = true;
                            reason = null;
                        }
                    }
                }
            }

            await LogAccessAsync(googleId, domain, allowed, websiteId);

            _logger.LogInformation(
                "Extension access check: GoogleId={GoogleId}, Domain={Domain}, Allowed={Allowed}",
                googleId, domain, allowed);

            return new ExtensionCheckResponse
            {
                Allowed = allowed,
                Reason = allowed ? null : reason,
                Domain = domain,
                AllowedWebsiteId = websiteId
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking access for domain {Domain}", domain);
            return new ExtensionCheckResponse
            {
                Allowed = false,
                Reason = "Lỗi server - chặn truy cập (an toàn)",
                Domain = domain
            };
        }
    }

    public async Task<ExtensionConfigResponse?> GetConfigAsync(string googleId)
    {
        try
        {
            var user = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.GoogleId == googleId && u.Role == UserRole.Child && u.IsActive);

            if (user == null)
            {
                _logger.LogWarning("User not found for GoogleId: {GoogleId}", googleId);
                return null;
            }

            return new ExtensionConfigResponse
            {
                FilterEnabled = user.FilterEnabled,
                ChildId = user.Id,
                FullName = user.FullName,
                Email = user.Email
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting extension config for GoogleId {GoogleId}", googleId);
            return null;
        }
    }

    // ── Đổi return type: bool → HeartbeatResult ──────────────────────────────
    public async Task<BlockInfoResult> GetBlockInfoAsync(string googleId, string domain)
    {
        domain = DomainNormalizer.Normalize(domain);

        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.GoogleId == googleId);
        if (user == null) return new BlockInfoResult { Reason = "not_in_whitelist" };

        if (user.InternetPaused)
        {
            return new BlockInfoResult { Reason = "internet_paused" };
        }

        var website = await _context.AllowedWebsites
            .FirstOrDefaultAsync(w => w.ChildId == user.Id
                                   && w.Domain == domain
                                   && w.IsActive);

        if (website == null)
        {
            return new BlockInfoResult
            {
                Reason = "not_in_whitelist",
                DomainExistsInWhitelist = false
            };
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var stat = await _context.DailyUsageStats
            .FirstOrDefaultAsync(s => s.ChildId == user.Id
                                   && s.AllowedWebsiteId == website.Id
                                   && s.UsageDate == today);

        if (website.TimeLimitMinutes.HasValue && stat != null)
        {
            var effectiveUsed = Math.Max(0, stat.TotalSeconds - Math.Max(0, stat.BonusSeconds));
            var limitSeconds = website.TimeLimitMinutes.Value * 60;
            if (effectiveUsed >= limitSeconds)
            {
                return new BlockInfoResult
                {
                    Reason = "time_limit_exceeded",
                    DomainExistsInWhitelist = true,
                    CurrentLimitMinutes = website.TimeLimitMinutes,
                    UsedSecondsToday = effectiveUsed,
                    RemainingSeconds = 0
                };
            }
        }

        if (website.AllowedStartTime.HasValue && website.AllowedEndTime.HasValue)
        {
            var now = DateTime.Now.TimeOfDay;
            if (now < website.AllowedStartTime.Value.ToTimeSpan() || now > website.AllowedEndTime.Value.ToTimeSpan())
            {
                return new BlockInfoResult
                {
                    Reason = "time_limit_exceeded",
                    DomainExistsInWhitelist = true,
                    AllowedStartTime = website.AllowedStartTime.Value.ToString(@"HH\:mm"),
                    AllowedEndTime = website.AllowedEndTime.Value.ToString(@"HH\:mm")
                };
            }
        }

        return new BlockInfoResult { Reason = "not_in_whitelist", DomainExistsInWhitelist = true };
    }

    public async Task<HeartbeatResult> UpdateHeartbeatAsync(string googleId, string domain, int? allowedWebsiteId)
    {
        var result = new HeartbeatResult();

        try
        {
            domain = DomainNormalizer.Normalize(domain);

            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.GoogleId == googleId && u.Role == UserRole.Child);

            if (user == null) return result;

            // Feature 3: Kill Switch — check internet_paused TRƯỚC mọi logic
            if (user.InternetPaused)
            {
                return new HeartbeatResult { LimitExceeded = true };
            }

            // ── Gộp ping vào heartbeat: update extension_last_seen ────────────
            // Extension gọi heartbeat mỗi 10s, không cần alarm ping riêng nữa.
            await UpdateExtensionLastSeenAsync(user.Id);

            if (!allowedWebsiteId.HasValue) return result;

            var today = DateOnly.FromDateTime(DateTime.Now);
            var dailyStat = await _context.DailyUsageStats
                .FirstOrDefaultAsync(d =>
                    d.ChildId == user.Id
                    && d.AllowedWebsiteId == allowedWebsiteId.Value
                    && d.UsageDate == today);

            if (dailyStat == null)
            {
                dailyStat = new DailyUsageStat
                {
                    ChildId = user.Id,
                    AllowedWebsiteId = allowedWebsiteId.Value,
                    Domain = domain,
                    UsageDate = today,
                    TotalSeconds = 30,   // heartbeat mỗi 30s
                    RequestCount = 1,
                    LastUpdated = DateTime.Now
                };
                _context.DailyUsageStats.Add(dailyStat);
            }
            else
            {
                dailyStat.TotalSeconds += 30;  // heartbeat mỗi 10s
                dailyStat.RequestCount += 1;
                dailyStat.LastUpdated = DateTime.Now;
            }

            await _context.SaveChangesAsync();

            var website = await _context.AllowedWebsites
                .FirstOrDefaultAsync(w => w.Id == allowedWebsiteId.Value);

            if (website?.TimeLimitMinutes != null)
            {
                int limitSeconds = website.TimeLimitMinutes.Value * 60;
                int effectiveUsedSeconds = Math.Max(0, dailyStat.TotalSeconds - Math.Max(0, dailyStat.BonusSeconds));
                double usedPercent = (double)effectiveUsedSeconds / limitSeconds * 100;
                bool exceeded = effectiveUsedSeconds >= limitSeconds;

                var config = await _context.WebsiteWarningConfigs
                    .FirstOrDefaultAsync(c => c.AllowedWebsiteId == website.Id && c.IsActive);

                if (config != null)
                {
                    int remainingSeconds = Math.Max(0, limitSeconds - effectiveUsedSeconds);

                    // ── Mốc 1 ─────────────────────────────────────────────────
                    if (!dailyStat.Warning1Sent && usedPercent >= config.Threshold1Percent)
                    {
                        dailyStat.Warning1Sent = true;
                        await _context.SaveChangesAsync();

                        await SendWarningNotificationAsync(user, website, config.Threshold1Message, remainingSeconds);

                        result.Warning = new HeartbeatWarning
                        {
                            Message = config.Threshold1Message,
                            RemainingSeconds = remainingSeconds
                        };
                    }
                    // ── Mốc 2 (nếu có) ────────────────────────────────────────
                    else if (config.Threshold2Percent.HasValue
                             && !dailyStat.Warning2Sent
                             && usedPercent >= config.Threshold2Percent.Value)
                    {
                        dailyStat.Warning2Sent = true;
                        await _context.SaveChangesAsync();

                        var msg = config.Threshold2Message ?? config.Threshold1Message;
                        await SendWarningNotificationAsync(user, website, msg, remainingSeconds);

                        result.Warning = new HeartbeatWarning
                        {
                            Message = msg,
                            RemainingSeconds = remainingSeconds
                        };
                    }
                }

                result.LimitExceeded = exceeded;
                result.MinutesRemainingToday = (int)Math.Max(0, Math.Ceiling((double)(limitSeconds - effectiveUsedSeconds) / 60));

                _logger.LogDebug(
                    "Heartbeat: child={ChildId}, domain={Domain}, used={UsedPercent:F1}%, exceeded={Exceeded}",
                    user.Id, domain, usedPercent, exceeded);
            }

            // ── Time Window Warning Block (MỚI) ──────────────────────────────
            if (website?.AllowedStartTime != null && website.AllowedEndTime != null)
            {
                var nowTime = TimeOnly.FromDateTime(DateTime.Now);
                var endTime = website.AllowedEndTime.Value;

                // Tính phút còn lại đến cuối khung giờ
                double minutesUntilEndRaw = (endTime.ToTimeSpan() - nowTime.ToTimeSpan()).TotalMinutes;
                // Nếu đã qua 0h (end < now do endTime qua ngày), điều chỉnh
                if (minutesUntilEndRaw < -600) minutesUntilEndRaw += 1440;

                int minutesUntilEnd = (int)Math.Ceiling(minutesUntilEndRaw);

                // Gán TimeWindowDisplay cho overlay
                result.TimeWindowDisplay = $"{website.AllowedStartTime.Value:HH\\:mm} → {endTime:HH\\:mm}";

                if (minutesUntilEnd >= 0)
                {
                    result.MinutesUntilWindowEnd = minutesUntilEnd;

                    var twConfig = await _context.WebsiteTimeWindowWarningConfigs
                        .FirstOrDefaultAsync(c => c.AllowedWebsiteId == website.Id && c.IsActive);

                    if (twConfig != null)
                    {
                        // Mốc 1
                        if (!dailyStat.TwWarning1Sent && minutesUntilEnd <= twConfig.WarnMinutesBefore1)
                        {
                            dailyStat.TwWarning1Sent = true;
                            await _context.SaveChangesAsync();

                            int remainingSeconds = minutesUntilEnd * 60;
                            await SendWarningNotificationAsync(user, website, twConfig.Message1, remainingSeconds);

                            result.Warning = new HeartbeatWarning
                            {
                                Message = twConfig.Message1,
                                RemainingSeconds = remainingSeconds
                            };
                        }
                        // Mốc 2 (nếu có)
                        else if (twConfig.WarnMinutesBefore2.HasValue
                                 && !dailyStat.TwWarning2Sent
                                 && minutesUntilEnd <= twConfig.WarnMinutesBefore2.Value)
                        {
                            dailyStat.TwWarning2Sent = true;
                            await _context.SaveChangesAsync();

                            int remainingSeconds = minutesUntilEnd * 60;
                            var msg2 = twConfig.Message2 ?? twConfig.Message1;
                            await SendWarningNotificationAsync(user, website, msg2, remainingSeconds);

                            result.Warning = new HeartbeatWarning
                            {
                                Message = msg2,
                                RemainingSeconds = remainingSeconds
                            };
                        }
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating heartbeat");
            return result;
        }
    }

    private async Task SendWarningNotificationAsync(User child, AllowedWebsite website, string customMessage, int remainingSeconds)
    {
        var guardians = await _context.GuardianChildRelationships
            .Where(r => r.ChildId == child.Id)
            .Select(r => r.GuardianId)
            .ToListAsync();

        if (!guardians.Any()) return;

        string remainingText = remainingSeconds >= 60
            ? $"{remainingSeconds / 60} phút"
            : $"{remainingSeconds} giây";

        foreach (var guardianId in guardians)
        {
            var notification = new Notification
            {
                GuardianId = guardianId,
                ChildId = child.Id,
                Title = $"⏰ Cảnh báo thời gian — {website.Domain}",
                Message = $"[{child.FullName}] {customMessage} (Còn lại: {remainingText})",
                Type = NotificationType.Warning,
                CreatedAt = DateTime.Now
            };

            _context.Notifications.Add(notification);
            await _context.SaveChangesAsync();

            await _hubContext.Clients.Group($"guardian_{guardianId}").SendAsync("TimeWarning", new
            {
                childId = child.Id,
                childName = child.FullName,
                domain = website.Domain,
                message = customMessage,
                remainingSeconds = remainingSeconds,
                notificationId = notification.Id
            });
        }
    }

    public async Task<bool> ToggleFilterAsync(int childId, bool enabled, int requestingGuardianId)
    {
        try
        {
            var relationship = await _context.GuardianChildRelationships
                .FirstOrDefaultAsync(r => r.GuardianId == requestingGuardianId && r.ChildId == childId);

            if (relationship == null)
            {
                _logger.LogWarning("Guardian {GuardianId} not allowed to manage child {ChildId}",
                    requestingGuardianId, childId);
                return false;
            }

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == childId);
            if (user == null) return false;

            user.FilterEnabled = enabled;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Filter toggled for child {ChildId}: {Enabled}", childId, enabled);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling filter for child {ChildId}", childId);
            return false;
        }
    }

    private async Task LogAccessAsync(string googleId, string domain, bool allowed, int? websiteId)
    {
        try
        {
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.GoogleId == googleId && u.Role == UserRole.Child);

            if (user == null) return;

            var log = new WebAccessLog
            {
                ChildId = user.Id,
                Domain = domain,
                AccessResult = allowed ? AccessResult.Allowed : AccessResult.Blocked,
                AllowedWebsiteId = websiteId,
                SessionStart = DateTime.Now
            };

            _context.WebAccessLogs.Add(log);
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging access for domain {Domain}", domain);
        }
    }

    // ── Helper nội bộ: update last_seen (gọi từ heartbeat) ──────────────────
    private async Task UpdateExtensionLastSeenAsync(int userId)
    {
        try
        {
            var status = await _context.UserOnlineStatuses
                .FirstOrDefaultAsync(o => o.UserId == userId);

            if (status == null)
            {
                _context.UserOnlineStatuses.Add(new UserOnlineStatus
                {
                    UserId = userId,
                    IsOnline = true,
                    LastSeenAt = DateTime.Now,
                    ExtensionLastSeen = DateTime.Now,
                    ExtensionActive = true
                });
            }
            else
            {
                status.ExtensionLastSeen = DateTime.Now;
                status.ExtensionActive = true;
                status.LastSeenAt = DateTime.Now;
            }
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating extension last seen for userId {UserId}", userId);
        }
    }


    /// <summary>
    /// Đánh dấu warning đã gửi trong DB khi precise alarm fires client-side.
    /// Ngăn heartbeat tiếp theo gửi duplicate notification.
    /// </summary>
    // Alias dùng cho endpoint /warning-shown (cùng logic với MarkWarningSentAsync)
    public async Task MarkWarningShownAsync(string googleId, int allowedWebsiteId, int warningIndex)
        => await MarkWarningSentAsync(googleId, allowedWebsiteId, warningIndex);

    public async Task MarkWarningSentAsync(string googleId, int allowedWebsiteId, int warningNumber)
    {
        try
        {
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.GoogleId == googleId && u.Role == UserRole.Child);
            if (user == null) return;

            var today = DateOnly.FromDateTime(DateTime.Now);
            var stat = await _context.DailyUsageStats
                .FirstOrDefaultAsync(d =>
                    d.ChildId == user.Id &&
                    d.AllowedWebsiteId == allowedWebsiteId &&
                    d.UsageDate == today);

            if (stat == null) return;

            if (warningNumber == 1) stat.Warning1Sent = true;
            else if (warningNumber == 2) stat.Warning2Sent = true;

            await _context.SaveChangesAsync();
            _logger.LogInformation("Warning{N} marked sent: child={ChildId}, websiteId={WebsiteId}",
                warningNumber, user.Id, allowedWebsiteId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking warning sent");
        }
    }

    public async Task MarkTimeWindowWarningSentAsync(string googleId, int allowedWebsiteId, int warningNumber)
    {
        try
        {
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.GoogleId == googleId && u.Role == UserRole.Child);
            if (user == null) return;

            var today = DateOnly.FromDateTime(DateTime.Now);
            var stat = await _context.DailyUsageStats
                .FirstOrDefaultAsync(d =>
                    d.ChildId == user.Id &&
                    d.AllowedWebsiteId == allowedWebsiteId &&
                    d.UsageDate == today);

            if (stat == null) return;

            if (warningNumber == 1) stat.TwWarning1Sent = true;
            else if (warningNumber == 2) stat.TwWarning2Sent = true;

            await _context.SaveChangesAsync();
            _logger.LogInformation("TwWarning{N} marked sent: child={ChildId}, websiteId={WebsiteId}",
                warningNumber, user.Id, allowedWebsiteId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking time window warning sent");
        }
    }

    public async Task UpdateExtensionPingAsync(string googleId)
    {
        try
        {
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.GoogleId == googleId && u.Role == UserRole.Child);
            if (user == null) return;

            var status = await _context.UserOnlineStatuses
                .FirstOrDefaultAsync(o => o.UserId == user.Id);

            if (status == null)
            {
                status = new UserOnlineStatus
                {
                    UserId = user.Id,
                    IsOnline = true,
                    LastSeenAt = DateTime.Now,
                    ExtensionLastSeen = DateTime.Now,
                    ExtensionActive = true
                };
                _context.UserOnlineStatuses.Add(status);
            }
            else
            {
                status.ExtensionLastSeen = DateTime.Now;
                status.ExtensionActive = true;
                status.LastSeenAt = DateTime.Now;
            }

            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating extension ping");
        }
    }
}
