using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Models.DTOs.Admin;
using FamilyGuardian.Api.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        // Use sp_AdminGetStats if defined, else fallback to LINQ. 
        // Based on database.sql, sp_AdminGetStats is NOT defined, but AppDbContext has the result set.
        // It might have been missed in database.sql. I'll use LINQ for safety or assume it exists since it was in AppDbContext.
        
        try {
             var result = await _db.Database.SqlQueryRaw<AdminStatsSpResult>("CALL sp_AdminGetStats()").ToListAsync();
             var first = result.FirstOrDefault();
             if (first != null) return Ok(first);
        } catch { /* SP not found */ }

        var totalGuardians = await _db.Users.CountAsync(u => u.Role == UserRole.Guardian);
        var totalChildren = await _db.Users.CountAsync(u => u.Role == UserRole.Child);
        var onlineUsers = await _db.UserOnlineStatuses.CountAsync(s => s.IsOnline);
        var totalRules = await _db.AllowedWebsites.CountAsync();
        var todayRequests = await _db.WebAccessLogs.CountAsync(l => l.SessionStart.Date == DateTime.UtcNow.Date);
        var todayBlocked = await _db.WebAccessLogs.CountAsync(l => l.SessionStart.Date == DateTime.UtcNow.Date && l.AccessResult == AccessResult.Blocked);

        return Ok(new SystemStatsDto
        {
            TotalGuardians = totalGuardians,
            TotalChildren = totalChildren,
            OnlineUsers = onlineUsers,
            TotalRules = totalRules,
            TodayRequests = todayRequests,
            TodayBlocked = todayBlocked
        });
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers(
        [FromQuery] string? role,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var query = _db.Users.AsQueryable();

        if (!string.IsNullOrEmpty(role) && Enum.TryParse<UserRole>(role, true, out var roleEnum))
            query = query.Where(u => u.Role == roleEnum);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(u => u.FullName.Contains(search) || u.Email.Contains(search));

        var total = await query.CountAsync();
        var users = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new AdminUserDto
            {
                Id = u.Id,
                Email = u.Email,
                FullName = u.FullName,
                AvatarUrl = u.AvatarUrl,
                Role = u.Role.ToString(),
                IsActive = u.IsActive,
                CreatedAt = u.CreatedAt
            })
            .ToListAsync();

        return Ok(new { items = users, totalCount = total, page, pageSize });
    }

    [HttpPatch("users/{id}/toggle-active")]
    public async Task<IActionResult> ToggleUserActive(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        user.IsActive = !user.IsActive;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { id = user.Id, isActive = user.IsActive });
    }

    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] int? childId,
        [FromQuery] string? domain,
        [FromQuery] string? result,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var from = fromDate ?? DateTime.UtcNow.AddDays(-7);
        var to = toDate ?? DateTime.UtcNow;

        var query = _db.WebAccessLogs.Where(l => l.SessionStart >= from && l.SessionStart <= to);

        if (childId.HasValue) query = query.Where(l => l.ChildId == childId.Value);
        if (!string.IsNullOrEmpty(domain)) query = query.Where(l => l.Domain.Contains(domain));
        if (!string.IsNullOrEmpty(result) && Enum.TryParse<AccessResult>(result, true, out var resEnum))
            query = query.Where(l => l.AccessResult == resEnum);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(l => l.SessionStart)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new { items, totalCount = total, page, pageSize });
    }

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        var settings = await _db.SystemSettings.ToListAsync();
        return Ok(settings);
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSetting([FromBody] UpdateSettingRequest request)
    {
        var setting = await _db.SystemSettings.FindAsync(request.Key);
        if (setting == null) return NotFound(new { message = "Setting not found." });

        setting.SettingValue = request.Value;
        setting.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(setting);
    }

    [HttpGet("proxy-root-cert")]
    [AllowAnonymous]
    public async Task<IActionResult> DownloadRootCert()
    {
        try
        {
            // Cert được export bởi FamilyProxyServer khi khởi động
            var certPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "certs", "FamilyGuardian-RootCA.pfx");

            if (!System.IO.File.Exists(certPath))
                return NotFound(new { message = "Root certificate không tìm thấy. Vui lòng khởi động proxy trước." });

            var bytes = await System.IO.File.ReadAllBytesAsync(certPath);
            return File(bytes, "application/x-pkcs12", "FamilyGuardian-RootCA.pfx");
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = $"Lỗi tải certificate: {ex.Message}" });
        }
    }
}
