using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace FamilyGuardian.Api.Controllers;

[ApiController]
[Route("api/warning-configs")]
[Authorize(Roles = "Admin,Guardian")]
public class WarningConfigController : ControllerBase
{
    private readonly AppDbContext _context;

    public WarningConfigController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> GetByWebsite([FromQuery] int allowedWebsiteId)
    {
        var config = await _context.WebsiteWarningConfigs
            .Include(c => c.AllowedWebsite)
            .Select(c => new
            {
                c.Id,
                c.AllowedWebsiteId,
                Domain = c.AllowedWebsite!.Domain,
                c.Threshold1Percent,
                c.Threshold1Message,
                c.Threshold2Percent,
                c.Threshold2Message,
                c.IsActive,
                c.UpdatedAt
            })
            .FirstOrDefaultAsync(c => c.AllowedWebsiteId == allowedWebsiteId);

        if (config == null) return NoContent();

        return Ok(config);
    }

    [HttpGet("by-child/{childId}")]
    public async Task<IActionResult> GetByChild(int childId)
    {
        var configs = await _context.WebsiteWarningConfigs
            .Include(c => c.AllowedWebsite)
            .Where(c => c.AllowedWebsite!.ChildId == childId)
            .Select(c => new
            {
                c.Id,
                c.AllowedWebsiteId,
                Domain = c.AllowedWebsite!.Domain,
                c.Threshold1Percent,
                c.Threshold1Message,
                c.Threshold2Percent,
                c.Threshold2Message,
                c.IsActive,
                c.UpdatedAt
            })
            .ToListAsync();

        return Ok(configs);
    }

    public class UpsertWarningConfigPayload
    {
        public List<int> AllowedWebsiteIds { get; set; } = new();
        public int Threshold1Percent { get; set; }
        public string Threshold1Message { get; set; } = null!;
        public int? Threshold2Percent { get; set; }
        public string? Threshold2Message { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] UpsertWarningConfigPayload payload)
    {
        if (payload.AllowedWebsiteIds == null || !payload.AllowedWebsiteIds.Any())
            return BadRequest(new { message = "allowedWebsiteIds không được rỗng" });

        if (payload.Threshold1Percent < 1 || payload.Threshold1Percent > 99)
            return BadRequest(new { message = "threshold1Percent phải từ 1-99" });

        if (payload.Threshold2Percent.HasValue)
            {
                if (payload.Threshold2Percent <= payload.Threshold1Percent || payload.Threshold2Percent > 99)
                    return BadRequest(new { message = "threshold2Percent phải lớn hơn threshold1Percent và <= 99" });

                if (string.IsNullOrWhiteSpace(payload.Threshold2Message))
                    return BadRequest(new { message = "threshold2Message là bắt buộc khi có threshold2Percent" });
            }

        foreach (var websiteId in payload.AllowedWebsiteIds)
        {
            var config = await _context.WebsiteWarningConfigs
                .FirstOrDefaultAsync(c => c.AllowedWebsiteId == websiteId);

            if (config == null)
            {
                config = new WebsiteWarningConfig
                {
                    AllowedWebsiteId = websiteId,
                    Threshold1Percent = payload.Threshold1Percent,
                    Threshold1Message = payload.Threshold1Message,
                    Threshold2Percent = payload.Threshold2Percent,
                    Threshold2Message = payload.Threshold2Message,
                    UpdatedAt = DateTime.Now,
                    CreatedAt = DateTime.Now
                };
                _context.WebsiteWarningConfigs.Add(config);
            }
            else
            {
                config.Threshold1Percent = payload.Threshold1Percent;
                config.Threshold1Message = payload.Threshold1Message;
                config.Threshold2Percent = payload.Threshold2Percent;
                config.Threshold2Message = payload.Threshold2Message;
                config.UpdatedAt = DateTime.Now;
            }
            
            // Also reset warning flags in daily stat to allow notifications again when config changes? 
            // Optional, let's keep it simple as guide doesn't explicitly require resetting.
        }

        await _context.SaveChangesAsync();
        return Ok(new { message = "Đã lưu cấu hình cảnh báo" });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var config = await _context.WebsiteWarningConfigs.FindAsync(id);
        if (config == null) return NotFound();

        _context.WebsiteWarningConfigs.Remove(config);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Xóa cấu hình thành công" });
    }
}
