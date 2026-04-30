using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Controllers;

[ApiController]
[Route("api/timewindow-warning-configs")]
[Authorize(Roles = "Admin,Guardian")]
public class TimeWindowWarningConfigController : ControllerBase
{
    private readonly AppDbContext _context;

    public TimeWindowWarningConfigController(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// GET /api/timewindow-warning-configs?allowedWebsiteId={id}
    /// Lấy config của 1 website (204 nếu chưa có)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetByWebsite([FromQuery] int allowedWebsiteId)
    {
        var config = await _context.WebsiteTimeWindowWarningConfigs
            .Include(c => c.AllowedWebsite)
            .Select(c => new
            {
                c.Id,
                c.AllowedWebsiteId,
                Domain = c.AllowedWebsite!.Domain,
                c.WarnMinutesBefore1,
                c.Message1,
                c.WarnMinutesBefore2,
                c.Message2,
                c.IsActive,
                c.UpdatedAt
            })
            .FirstOrDefaultAsync(c => c.AllowedWebsiteId == allowedWebsiteId);

        if (config == null) return NoContent();
        return Ok(config);
    }

    /// <summary>
    /// GET /api/timewindow-warning-configs/by-child/{childId}
    /// Tất cả config của con, join lấy domain
    /// </summary>
    [HttpGet("by-child/{childId}")]
    public async Task<IActionResult> GetByChild(int childId)
    {
        var configs = await _context.WebsiteTimeWindowWarningConfigs
            .Include(c => c.AllowedWebsite)
            .Where(c => c.AllowedWebsite!.ChildId == childId)
            .Select(c => new
            {
                c.Id,
                c.AllowedWebsiteId,
                Domain = c.AllowedWebsite!.Domain,
                c.WarnMinutesBefore1,
                c.Message1,
                c.WarnMinutesBefore2,
                c.Message2,
                c.IsActive,
                c.UpdatedAt
            })
            .ToListAsync();

        return Ok(configs);
    }

    public class UpsertTimeWindowWarningConfigPayload
    {
        public int AllowedWebsiteId { get; set; }
        public int WarnMinutesBefore1 { get; set; }
        public string Message1 { get; set; } = null!;
        public int? WarnMinutesBefore2 { get; set; }
        public string? Message2 { get; set; }
    }

    /// <summary>
    /// POST /api/timewindow-warning-configs
    /// Upsert (tạo hoặc cập nhật) config
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] UpsertTimeWindowWarningConfigPayload payload)
    {
        if (payload.WarnMinutesBefore1 <= 0)
            return BadRequest(new { message = "warnMinutesBefore1 phải lớn hơn 0" });

        if (payload.WarnMinutesBefore2.HasValue)
        {
            if (payload.WarnMinutesBefore2.Value <= 0)
                return BadRequest(new { message = "warnMinutesBefore2 phải lớn hơn 0" });
            if (payload.WarnMinutesBefore2.Value >= payload.WarnMinutesBefore1)
                return BadRequest(new { message = "warnMinutesBefore2 phải nhỏ hơn warnMinutesBefore1 (mốc 2 gần hết hơn mốc 1)" });
            if (string.IsNullOrWhiteSpace(payload.Message2))
                return BadRequest(new { message = "message2 là bắt buộc khi có warnMinutesBefore2" });
        }

        // Validate website đang dùng time window
        var website = await _context.AllowedWebsites.FindAsync(payload.AllowedWebsiteId);
        if (website == null)
            return NotFound(new { message = "Không tìm thấy website" });
        if (website.AllowedStartTime == null || website.AllowedEndTime == null)
            return BadRequest(new { message = "Website này chưa được thiết lập khung giờ. Vui lòng thiết lập khung giờ trước." });

        var config = await _context.WebsiteTimeWindowWarningConfigs
            .FirstOrDefaultAsync(c => c.AllowedWebsiteId == payload.AllowedWebsiteId);

        if (config == null)
        {
            config = new WebsiteTimeWindowWarningConfig
            {
                AllowedWebsiteId = payload.AllowedWebsiteId,
                WarnMinutesBefore1 = payload.WarnMinutesBefore1,
                Message1 = payload.Message1,
                WarnMinutesBefore2 = payload.WarnMinutesBefore2,
                Message2 = payload.Message2,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            _context.WebsiteTimeWindowWarningConfigs.Add(config);
        }
        else
        {
            config.WarnMinutesBefore1 = payload.WarnMinutesBefore1;
            config.Message1 = payload.Message1;
            config.WarnMinutesBefore2 = payload.WarnMinutesBefore2;
            config.Message2 = payload.Message2;
            config.UpdatedAt = DateTime.Now;
        }

        await _context.SaveChangesAsync();
        return Ok(new { message = "Đã lưu cấu hình cảnh báo khung giờ" });
    }

    /// <summary>
    /// DELETE /api/timewindow-warning-configs/{id}
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var config = await _context.WebsiteTimeWindowWarningConfigs.FindAsync(id);
        if (config == null) return NotFound();

        _context.WebsiteTimeWindowWarningConfigs.Remove(config);
        await _context.SaveChangesAsync();

        return Ok(new { message = "Xóa cấu hình thành công" });
    }
}
