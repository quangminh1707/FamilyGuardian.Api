using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Helpers;
using FamilyGuardian.Api.Models.DTOs.Children;
using FamilyGuardian.Api.Models.DTOs.AllowedWebsites;
using FamilyGuardian.Api.Models.Entities;
using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Services;

public class AllowedWebsiteService : IAllowedWebsiteService
{
    private readonly AppDbContext _db;
    private readonly IWebsiteCheckService _checkService;

    public AllowedWebsiteService(AppDbContext db, IWebsiteCheckService checkService)
    {
        _db = db;
        _checkService = checkService;
    }

    public async Task<List<AllowedWebsiteDto>> GetChildAllowedWebsitesAsync(int childId, int guardianId)
    {
        // Verify access
        var hasAccess = await _db.GuardianChildRelationships
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
        if (!hasAccess) throw new UnauthorizedAccessException("Bạn không có quyền xem thông tin này.");

        var results = await _db.AllowedWebsiteSpResults
            .FromSqlRaw("CALL sp_GetChildAllowedWebsites({0})", childId)
            .ToListAsync();

        return results.Select(r => new AllowedWebsiteDto
        {
            Id = r.Id,
            Domain = r.Domain,
            DisplayName = r.DisplayName,
            FaviconUrl = r.FaviconUrl,
            IsActive = r.IsActive,
            TimeLimitMinutes = r.TimeLimitMinutes,
            AllowedStartTime = r.AllowedStartTime?.ToString(@"hh\:mm"),
            AllowedEndTime = r.AllowedEndTime?.ToString(@"hh\:mm"),
            IsVerified = r.IsVerified,
            IsSafe = r.IsSafe ?? true,
            HttpStatusCode = r.HttpStatusCode,
            LastCheckedAt = r.LastCheckedAt,
            TodaySeconds = r.TodaySeconds,
            TodayRequests = r.TodayRequests,
            LimitExceeded = r.LimitExceeded
        }).ToList();
    }

    public async Task<AllowedWebsiteDto> AddWebsiteAsync(int childId, int guardianId, AddWebsiteRequest request)
    {
        var hasAccess = await _db.GuardianChildRelationships
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
        if (!hasAccess) throw new UnauthorizedAccessException("Bạn không có quyền quản lý trẻ này.");

        var domain = DomainNormalizer.Normalize(request.Domain);
        if (string.IsNullOrEmpty(domain)) throw new ArgumentException("Domain không hợp lệ.");

        if (await _db.AllowedWebsites.AnyAsync(w => w.ChildId == childId && w.Domain == domain))
            throw new InvalidOperationException("Website này đã có trong danh sách cho phép.");

        // Check website info
        var checkResult = await _checkService.CheckAsync(domain);

        var website = new AllowedWebsite
        {
            ChildId = childId,
            Domain = domain,
            DisplayName = checkResult.DisplayName ?? domain,
            FaviconUrl = checkResult.FaviconUrl,
            TimeLimitMinutes = request.TimeLimitMinutes,
            AllowedStartTime = !string.IsNullOrEmpty(request.AllowedStartTime) ? TimeOnly.Parse(request.AllowedStartTime) : null,
            AllowedEndTime = !string.IsNullOrEmpty(request.AllowedEndTime) ? TimeOnly.Parse(request.AllowedEndTime) : null,
            IsVerified = checkResult.IsReachable,
            IsSafe = checkResult.IsSafe,
            HttpStatusCode = checkResult.HttpStatusCode,
            LastCheckedAt = DateTime.UtcNow,
            AddedBy = guardianId
        };

        _db.AllowedWebsites.Add(website);
        await _db.SaveChangesAsync();

        return new AllowedWebsiteDto
        {
            Id = website.Id,
            Domain = website.Domain,
            DisplayName = website.DisplayName,
            FaviconUrl = website.FaviconUrl,
            IsActive = website.IsActive,
            TimeLimitMinutes = website.TimeLimitMinutes,
            AllowedStartTime = website.AllowedStartTime?.ToString("HH:mm"),
            AllowedEndTime = website.AllowedEndTime?.ToString("HH:mm"),
            IsVerified = website.IsVerified,
            IsSafe = website.IsSafe ?? true,
            HttpStatusCode = website.HttpStatusCode,
            LastCheckedAt = website.LastCheckedAt
        };
    }

    public async Task UpdateWebsiteAsync(int childId, int guardianId, int websiteId, UpdateWebsiteRequest request)
    {
        var website = await _db.AllowedWebsites
            .FirstOrDefaultAsync(w => w.Id == websiteId && w.ChildId == childId)
            ?? throw new KeyNotFoundException("Không tìm thấy website.");

        // Logic chuyển đổi: chỉ 1 trong 2 tính năng được kích hoạt tại cùng thời điểm
        if (request.TimeLimitMinutes.HasValue)
        {
            // Dùng giới hạn phút → xóa khung giờ cũ
            website.TimeLimitMinutes = request.TimeLimitMinutes;
            website.AllowedStartTime = null;
            website.AllowedEndTime = null;
        }
        else if (!string.IsNullOrEmpty(request.AllowedStartTime))
        {
            // Dùng khung giờ → xóa giới hạn phút cũ
            website.AllowedStartTime = TimeOnly.Parse(request.AllowedStartTime);
            website.AllowedEndTime = !string.IsNullOrEmpty(request.AllowedEndTime)
                ? TimeOnly.Parse(request.AllowedEndTime)
                : null;
            website.TimeLimitMinutes = null;
        }
        else
        {
            // Cả 2 đều null → không giới hạn
            website.TimeLimitMinutes = null;
            website.AllowedStartTime = null;
            website.AllowedEndTime = null;
        }

        website.IsActive = request.IsActive;
        website.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task DeleteWebsiteAsync(int childId, int guardianId, int websiteId)
    {
        var website = await _db.AllowedWebsites
            .FirstOrDefaultAsync(w => w.Id == websiteId && w.ChildId == childId)
            ?? throw new KeyNotFoundException("Không tìm thấy website.");

        _db.AllowedWebsites.Remove(website);
        await _db.SaveChangesAsync();
    }

    public async Task<bool> ToggleWebsiteAsync(int childId, int guardianId, int websiteId)
    {
        var website = await _db.AllowedWebsites
            .FirstOrDefaultAsync(w => w.Id == websiteId && w.ChildId == childId)
            ?? throw new KeyNotFoundException("Không tìm thấy website.");

        website.IsActive = !website.IsActive;
        website.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return website.IsActive;
    }

    public async Task RecheckWebsiteAsync(int childId, int guardianId, int websiteId)
    {
        var website = await _db.AllowedWebsites
            .FirstOrDefaultAsync(w => w.Id == websiteId && w.ChildId == childId)
            ?? throw new KeyNotFoundException("Không tìm thấy website.");

        var res = await _checkService.CheckAsync(website.Domain, forceRefresh: true);
        website.IsVerified = res.IsReachable;
        website.IsSafe = res.IsSafe;
        website.HttpStatusCode = res.HttpStatusCode;
        website.LastCheckedAt = DateTime.UtcNow;
        website.DisplayName = res.DisplayName ?? website.DisplayName;
        website.FaviconUrl = res.FaviconUrl ?? website.FaviconUrl;

        await _db.SaveChangesAsync();
    }
}
