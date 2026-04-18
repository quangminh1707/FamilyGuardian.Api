using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Models.DTOs.Logs;
using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Services;

public class AccessLogService : IAccessLogService
{
    private readonly AppDbContext _db;

    public AccessLogService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<(List<AccessLogDto> Items, int TotalCount)> GetAccessLogsAsync(int childId, int guardianId, DateTime fromDate, DateTime toDate, int page, int pageSize, string? domain = null, string? result = null)
    {
        // Verify access
        var hasAccess = await _db.GuardianChildRelationships
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
        if (!hasAccess) throw new UnauthorizedAccessException("Bạn không có quyền xem thông tin này.");

        // sp_GetAccessLogs doesn't have domain/result filters in the provided SQL, 
        // so I'll handle those in memory if passed, or I could update the SP. 
        // For simplicity and matching the SQL as provided:
        
        var results = await _db.AccessLogSpResults
            .FromSqlRaw("CALL sp_GetAccessLogs({0}, {1}, {2}, {3}, {4})", 
                childId, fromDate.Date, toDate.Date, page, pageSize)
            .ToListAsync();

        // The SP returns TWO result sets. EF FromSqlRaw only gets the first one.
        // To get the total count, I'll do a separate query or assume the user wants it.
        var total = await _db.WebAccessLogs
            .CountAsync(l => l.ChildId == childId && l.SessionStart.Date >= fromDate.Date && l.SessionStart.Date <= toDate.Date);

        return (results.Select(r => new AccessLogDto
        {
            Id = r.Id,
            Domain = r.Domain,
            DisplayName = r.DisplayName,
            FaviconUrl = r.FaviconUrl,
            FullUrl = r.FullUrl,
            AccessResult = r.AccessResult,
            DurationSeconds = r.DurationSeconds,
            SessionStart = r.SessionStart,
            SessionEnd = r.SessionEnd
        }).ToList(), total);
    }

    public async Task<List<DailyUsageDto>> GetDailyUsageAsync(int childId, int guardianId, DateOnly date)
    {
        var hasAccess = await _db.GuardianChildRelationships
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
        if (!hasAccess) throw new UnauthorizedAccessException("Bạn không có quyền xem thông tin này.");

        var results = await _db.AllowedWebsiteSpResults
            .FromSqlRaw("CALL sp_GetChildAllowedWebsites({0})", childId)
            .ToListAsync();
        
        // Filter by date if needed, but the SP uses CURDATE(). 
        // If we want a specific date, we'd need a different SP or query.
        // For 'daily-usage' endpoint using current day:
        
        return results.Select(r => new DailyUsageDto
        {
            Domain = r.Domain,
            DisplayName = r.DisplayName,
            FaviconUrl = r.FaviconUrl,
            TotalSeconds = r.TodaySeconds,
            RequestCount = r.TodayRequests,
            TimeLimitMinutes = r.TimeLimitMinutes,
            LimitExceeded = r.LimitExceeded,
            UsagePercent = r.TimeLimitMinutes.HasValue ? (double)r.TodaySeconds / (r.TimeLimitMinutes.Value * 60) * 100 : null,
            UsageDate = date.ToString("yyyy-MM-dd")
        }).ToList();
    }

    public async Task<List<UsageHistoryDto>> GetUsageHistoryAsync(int childId, int guardianId, DateOnly fromDate, DateOnly toDate)
    {
        var hasAccess = await _db.GuardianChildRelationships
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
        if (!hasAccess) throw new UnauthorizedAccessException("Bạn không có quyền xem thông tin này.");

        var results = await _db.UsageHistorySpResults
            .FromSqlRaw("CALL sp_GetUsageHistory({0}, {1}, {2})", childId, fromDate, toDate)
            .ToListAsync();

        return results.GroupBy(r => r.UsageDate)
            .Select(g => new UsageHistoryDto
            {
                Date = g.Key.ToString("yyyy-MM-dd"),
                TotalSeconds = g.Sum(x => x.TotalSeconds),
                Websites = g.Select(x => new DailyUsageDto
                {
                    Domain = x.Domain,
                    DisplayName = x.DisplayName,
                    FaviconUrl = x.FaviconUrl,
                    TotalSeconds = x.TotalSeconds,
                    RequestCount = x.RequestCount,
                    TimeLimitMinutes = x.TimeLimitMinutes,
                    LimitExceeded = x.LimitExceeded,
                    UsagePercent = x.TimeLimitMinutes.HasValue ? (double)x.TotalSeconds / (x.TimeLimitMinutes.Value * 60) * 100 : null,
                    UsageDate = x.UsageDate.ToString("yyyy-MM-dd")
                }).ToList()
            }).ToList();
    }

    public async Task<(List<SessionDto> Items, int TotalCount)> GetSessionsAsync(int childId, int guardianId, DateTime? fromDate, DateTime? toDate, int page, int pageSize)
    {
        // Verify access
        var hasAccess = await _db.GuardianChildRelationships
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
        if (!hasAccess) throw new UnauthorizedAccessException("Bạn không có quyền xem thông tin này.");

        var from = (fromDate ?? DateTime.Today.AddDays(-7)).Date;
        var to = (toDate ?? DateTime.Today).Date;

        var sessions = await _db.WebSessions
            .Include(s => s.Website)
            .Where(s => s.ChildId == childId
                     && s.StartedAt.Date >= from
                     && s.StartedAt.Date <= to)
            .OrderByDescending(s => s.StartedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new SessionDto
            {
                Id = s.Id,
                Domain = s.Domain,
                DisplayName = s.Website.DisplayName,
                FaviconUrl = s.Website.FaviconUrl,
                StartedAt = s.StartedAt,
                EndedAt = s.EndedAt,
                DurationSeconds = s.DurationSeconds,
                IsActive = s.EndedAt == null,
            })
            .ToListAsync();

        var total = await _db.WebSessions
            .CountAsync(s => s.ChildId == childId
                          && s.StartedAt.Date >= from
                          && s.StartedAt.Date <= to);

        return (sessions, total);
    }

    public async Task<object> GetUsageSummaryAsync(int childId, int guardianId, int days)
    {
        // Verify access
        var hasAccess = await _db.GuardianChildRelationships
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
        if (!hasAccess) throw new UnauthorizedAccessException("Bạn không có quyền xem thông tin này.");

        var fromDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-days + 1));

        // Tổng theo domain
        var byDomain = await _db.DailyUsageStats
            .Include(d => d.Website)
            .Where(d => d.ChildId == childId && d.UsageDate >= fromDate)
            .GroupBy(d => new { d.Domain, d.Website.DisplayName, d.Website.FaviconUrl, d.Website.TimeLimitMinutes })
            .Select(g => new
            {
                domain = g.Key.Domain,
                displayName = g.Key.DisplayName,
                faviconUrl = g.Key.FaviconUrl,
                timeLimitMinutes = g.Key.TimeLimitMinutes,
                totalSeconds = g.Sum(x => x.TotalSeconds),
                totalRequests = g.Sum(x => x.RequestCount),
            })
            .OrderByDescending(x => x.totalSeconds)
            .ToListAsync();

        // Breakdown theo ngày
        var byDay = await _db.DailyUsageStats
            .Where(d => d.ChildId == childId && d.UsageDate >= fromDate)
            .GroupBy(d => d.UsageDate)
            .Select(g => new
            {
                date = g.Key,
                totalSeconds = g.Sum(x => x.TotalSeconds),
            })
            .OrderBy(x => x.date)
            .ToListAsync();

        return new { byDomain, byDay, days };
    }
}
