using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Models.DTOs.Children;
using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Services;

public class ChildService : IChildService
{
    private readonly AppDbContext _db;
    private readonly IAllowedWebsiteService _websiteService;

    public ChildService(AppDbContext db, IAllowedWebsiteService websiteService)
    {
        _db = db;
        _websiteService = websiteService;
    }

    public async Task<List<ChildDto>> GetGuardianChildrenAsync(int guardianId)
    {
        var results = await _db.ChildSpResults
            .FromSqlRaw("CALL sp_GetGuardianChildren({0})", guardianId)
            .ToListAsync();

        return results.Select(r => new ChildDto
        {
            Id = r.Id,
            FullName = r.FullName,
            Email = r.Email,
            AvatarUrl = r.AvatarUrl,
            IsOnline = r.IsOnline,
            LastSeenAt = r.LastSeenAt,
            ActiveWebsitesCount = r.ActiveWebsitesCount,
            TodayTotalSeconds = r.TodayTotalSeconds,
             FilterEnabled = r.FilterEnabled,
            InternetPaused = r.InternetPaused,
        }).ToList();
    }

    public async Task<ChildDetailDto> GetChildDetailAsync(int childId, int guardianId)
    {
        // Kiểm tra quyền
        var hasAccess = await _db.GuardianChildRelationships
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
        
        if (!hasAccess)
            throw new UnauthorizedAccessException("Bạn không có quyền quản lý trẻ này.");

        // Lấy thông tin child
        var child = await _db.Users
            .Include(u => u.OnlineStatus)
            .FirstOrDefaultAsync(u => u.Id == childId);

        if (child == null)
            throw new KeyNotFoundException("Không tìm thấy thông tin trẻ.");

        // Lấy danh sách web được phép
        var websites = await _websiteService.GetChildAllowedWebsitesAsync(childId, guardianId);

        return new ChildDetailDto
        {
            Id = child.Id,
            FullName = child.FullName,
            Email = child.Email,
            AvatarUrl = child.AvatarUrl,
            IsOnline = child.OnlineStatus?.IsOnline ?? false,
            LastSeenAt = child.OnlineStatus?.LastSeenAt,
            IpAddress = child.OnlineStatus?.IpAddress,
            AllowedWebsites = websites,
            FilterEnabled = child.FilterEnabled,  // ✅ THÊM DÒNG NÀY
            InternetPaused = child.InternetPaused,
            TodayTotalSeconds = websites.Sum(w => w.TodaySeconds)
        };
    }

    public async Task UnlinkChildAsync(int childId, int guardianId)
    {
        var rel = await _db.GuardianChildRelationships
            .FirstOrDefaultAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
        
        if (rel != null)
        {
            _db.GuardianChildRelationships.Remove(rel);
            await _db.SaveChangesAsync();
        }
    }
    public async Task ToggleFilterAsync(int childId, int guardianId, bool filterEnabled)
{
    // Kiểm tra quyền
    var hasAccess = await _db.GuardianChildRelationships
        .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);

    if (!hasAccess) throw new UnauthorizedAccessException("Không có quyền quản lý con này");

    var child = await _db.Users.FindAsync(childId)
        ?? throw new KeyNotFoundException();

    child.FilterEnabled = filterEnabled;
    await _db.SaveChangesAsync();
}
}