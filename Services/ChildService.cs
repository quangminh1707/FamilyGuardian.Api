using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Models.DTOs.Children;
using FamilyGuardian.Api.Models.Entities;
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
            TodayTotalSeconds = r.TodayTotalSeconds
        }).ToList();
    }

    public async Task<ChildDetailDto> GetChildDetailAsync(int childId, int guardianId)
    {
        // Kiểm tra quyền
        var hasAccess = await _db.GuardianChildRelationships
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
        
        if (!hasAccess)
            throw new UnauthorizedAccessException("Bạn không có quyền quản lý trẻ này.");

        var child = await _db.Users
            .Include(u => u.OnlineStatus)
            .FirstOrDefaultAsync(u => u.Id == childId);

        if (child == null)
            throw new KeyNotFoundException("Không tìm thấy thông tin trẻ.");

        // Lấy danh sách web được phép
        var websites = await _websiteService.GetChildAllowedWebsitesAsync(childId, guardianId);
        
        // Lấy IP mappings
        var ipMappings = await _db.ProxyIpMappings
            .Where(m => m.ChildId == childId)
            .Select(m => new ProxyIpMappingDto
            {
                Id = m.Id,
                IpAddress = m.IpAddress,
                DeviceName = m.DeviceName
            }).ToListAsync();

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
            ProxyIpMappings = ipMappings,
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

    public async Task AddIpMappingAsync(int childId, int guardianId, AddIpMappingRequest request)
    {
        var hasAccess = await _db.GuardianChildRelationships
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
        
        if (!hasAccess)
            throw new UnauthorizedAccessException("Bạn không có quyền quản lý trẻ này.");

        // Upsert logic (SQL UNIQUE KEY uq_ip handles single IP globally, 
        // but typically one IP belongs to one child)
        var mapping = await _db.ProxyIpMappings.FirstOrDefaultAsync(m => m.IpAddress == request.IpAddress);
        if (mapping != null)
        {
            mapping.ChildId = childId;
            mapping.DeviceName = request.DeviceName;
            mapping.CreatedBy = guardianId;
        }
        else
        {
            _db.ProxyIpMappings.Add(new ProxyIpMapping
            {
                ChildId = childId,
                IpAddress = request.IpAddress,
                DeviceName = request.DeviceName,
                CreatedBy = guardianId
            });
        }

        await _db.SaveChangesAsync();
    }

    public async Task<List<ProxyIpMappingDto>> GetIpMappingsAsync(int childId, int guardianId)
    {
        var hasAccess = await _db.GuardianChildRelationships
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
        
        if (!hasAccess)
            throw new UnauthorizedAccessException("Bạn không có quyền quản lý trẻ này.");

        return await _db.ProxyIpMappings
            .Where(m => m.ChildId == childId)
            .Select(m => new ProxyIpMappingDto
            {
                Id = m.Id,
                IpAddress = m.IpAddress,
                DeviceName = m.DeviceName
            }).ToListAsync();
    }

    public async Task RemoveIpMappingAsync(int childId, int guardianId, int mappingId)
    {
        var mapping = await _db.ProxyIpMappings.FindAsync(mappingId);
        if (mapping != null && mapping.ChildId == childId)
        {
            _db.ProxyIpMappings.Remove(mapping);
            await _db.SaveChangesAsync();
        }
    }
}
