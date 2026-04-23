using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Helpers;
using FamilyGuardian.Api.Models;
using FamilyGuardian.Api.Models.Entities;
using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

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

public class ExtensionService : IExtensionService
{
    private readonly AppDbContext _context;
    private readonly ILogger<ExtensionService> _logger;

    public ExtensionService(AppDbContext context, ILogger<ExtensionService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<ExtensionCheckResponse> CheckAccessAsync(string googleId, string domain)
    {
        try
        {
            // Normalize domain
            domain = DomainNormalizer.Normalize(domain);

            // Call stored procedure
            var result = await _context.CheckWebAccessSpResults.FromSqlInterpolated(
                $"CALL sp_ExtensionCheckAccess({googleId}, {domain})"
            ).ToListAsync();

            if (result.Count == 0)
            {
                _logger.LogWarning("Stored procedure returned no results for GoogleId={GoogleId}, Domain={Domain}", googleId, domain);
                // Fail-closed: block access if no response from procedure
                return new ExtensionCheckResponse 
                { 
                    Allowed = false, 
                    Reason = "Không thể xác định trạng thái - chặn để an toàn",
                    Domain = domain
                };
            }

            var row = result.First();
            bool allowed = row.AccessResult == "allowed";
            string reason = row.Reason ?? "";
            int? websiteId = row.AllowedWebsiteId;

            // Log the access attempt
            await LogAccessAsync(googleId, domain, allowed, websiteId);

            _logger.LogInformation(
                "Extension access check: GoogleId={GoogleId}, Domain={Domain}, Allowed={Allowed}",
                googleId, domain, allowed
            );

            return new ExtensionCheckResponse
            {
                Allowed = allowed,
                Reason = reason,
                Domain = domain,
                AllowedWebsiteId = websiteId
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking access for domain {Domain}", domain);
            // On error, BLOCK access (fail-closed) for security
            // Better to block legitimate sites than allow malicious ones
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

  public async Task<bool> UpdateHeartbeatAsync(string googleId, string domain, int? allowedWebsiteId)
{
    try
    {
        domain = DomainNormalizer.Normalize(domain);

        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.GoogleId == googleId && u.Role == UserRole.Child);

        if (user == null) return false;
        if (!allowedWebsiteId.HasValue) return false;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
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
                TotalSeconds = 30,
                RequestCount = 1,
                LastUpdated = DateTime.UtcNow
            };
            _context.DailyUsageStats.Add(dailyStat);
        }
        else
        {
            dailyStat.TotalSeconds += 30;
            dailyStat.RequestCount += 1;
            dailyStat.LastUpdated = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        // ✅ Kiểm tra có vượt giới hạn không
        var website = await _context.AllowedWebsites
            .FirstOrDefaultAsync(w => w.Id == allowedWebsiteId.Value);

        if (website?.TimeLimitMinutes != null)
        {
            bool exceeded = dailyStat.TotalSeconds >= (website.TimeLimitMinutes * 60);
            _logger.LogDebug("Heartbeat: child={ChildId}, domain={Domain}, seconds={Seconds}, limit={Limit}, exceeded={Exceeded}",
                user.Id, domain, dailyStat.TotalSeconds, website.TimeLimitMinutes * 60, exceeded);
            return exceeded;
        }

        return false;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error updating heartbeat");
        return false;
    }
}

    public async Task<bool> ToggleFilterAsync(int childId, bool enabled, int requestingGuardianId)
    {
        try
        {
            // Check if guardian can manage this child
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
                SessionStart = DateTime.UtcNow
            };

            _context.WebAccessLogs.Add(log);
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging access for domain {Domain}", domain);
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
                LastSeenAt = DateTime.UtcNow,
                ExtensionLastSeen = DateTime.UtcNow,
                ExtensionActive = true
            };
            _context.UserOnlineStatuses.Add(status);
        }
        else
        {
            status.ExtensionLastSeen = DateTime.UtcNow;
            status.ExtensionActive = true;
            status.LastSeenAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error updating extension ping");
    }
}

    
}
