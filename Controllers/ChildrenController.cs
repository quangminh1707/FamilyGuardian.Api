using FamilyGuardian.Api.Models.DTOs.Children;
using FamilyGuardian.Api.Services.Interfaces;
using FamilyGuardian.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Controllers;

[Authorize(Roles = "Guardian,Admin")]
[ApiController]
[Route("api/children")]
public class ChildrenController : ControllerBase
{
    private readonly IChildService _childService;
    private readonly AppDbContext _context;
    private readonly IScreenshotService _screenshotService;

    public ChildrenController(IChildService childService, AppDbContext context, IScreenshotService screenshotService)
    {
        _childService = childService;
        _context = context;
        _screenshotService = screenshotService;
    }

    [HttpGet]
    public async Task<IActionResult> GetMyChildren()
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var children = await _childService.GetGuardianChildrenAsync(guardianId);
        return Ok(children);
    }

    [HttpGet("{childId}")]
    public async Task<IActionResult> GetChildDetail(int childId)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        try
        {
            var detail = await _childService.GetChildDetailAsync(childId, guardianId);
            return Ok(detail);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpDelete("{childId}")]
    public async Task<IActionResult> UnlinkChild(int childId)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _childService.UnlinkChildAsync(childId, guardianId);
        return NoContent();
    }

    [HttpPatch("{childId}/filter")]
    public async Task<IActionResult> ToggleFilter(int childId, [FromBody] FilterToggleRequest request)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        try
        {
            await _childService.ToggleFilterAsync(childId, guardianId, request.FilterEnabled);
            return Ok(new { success = true, filterEnabled = request.FilterEnabled });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // Feature 3: Kill Switch — Tạm dừng Internet
    [HttpPatch("{childId}/pause-internet")]
    public async Task<IActionResult> TogglePauseInternet(int childId)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var relationship = await _context.GuardianChildRelationships
            .FirstOrDefaultAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
        if (relationship == null) return Forbid();

        var child = await _context.Users.FindAsync(childId);
        if (child == null) return NotFound();

        child.InternetPaused = !child.InternetPaused;
        await _context.SaveChangesAsync();

        return Ok(new
        {
            internetPaused = child.InternetPaused,
            message = child.InternetPaused
                ? $"Đã tạm dừng internet cho {child.FullName}"
                : $"Đã bật lại internet cho {child.FullName}"
        });
    }

    // ── Endpoint 1: Guardian yêu cầu chụp ──
    [HttpPost("{childId}/request-screenshot")]
    [Authorize(Roles = "Guardian,Admin")]
    public async Task<IActionResult> RequestScreenshot(int childId, [FromQuery] string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return BadRequest("domain required");

        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var result = await _screenshotService.RequestScreenshotAsync(guardianId, childId, domain);

        if (!result.Success)
            return BadRequest(new { error = result.Error });

        return Ok(new { screenshotId = result.ScreenshotId, message = "Đã gửi yêu cầu chụp ảnh" });
    }

    // ── Endpoint 2: Lấy danh sách ảnh ──
    [HttpGet("{childId}/screenshots")]
    [Authorize(Roles = "Guardian,Admin")]
    public async Task<IActionResult> GetScreenshots(int childId, [FromQuery] string domain, [FromQuery] int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return BadRequest("domain required");

        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var screenshots = await _screenshotService.GetScreenshotsAsync(guardianId, childId, domain, limit);
        return Ok(screenshots);
    }

    [HttpDelete("{childId}/screenshots/{screenshotId}")]
    [Authorize(Roles = "Guardian,Admin")]
    public async Task<IActionResult> DeleteScreenshot(int childId, int screenshotId)
    {
        var guardianId = GetCurrentUserId();
        var ok = await _screenshotService.DeleteScreenshotAsync(guardianId, childId, screenshotId);
        return ok ? Ok() : NotFound();
    }

    [HttpPost("{childId}/schedule-screenshot")]
    [Authorize(Roles = "Guardian,Admin")]
    public async Task<IActionResult> ScheduleScreenshot(int childId, [FromBody] ScheduleScreenshotDto dto)
    {
        if (dto.ScheduledAt <= DateTime.UtcNow)
            return BadRequest("Thời gian hẹn phải trong tương lai");

        var guardianId = GetCurrentUserId();
        var id = await _screenshotService.ScheduleScreenshotAsync(
            guardianId, childId, dto.Domain, dto.ScheduledAt);

        return id > 0
            ? Ok(new { scheduleId = id, message = "Đã hẹn giờ chụp ảnh" })
            : BadRequest("Không có quyền");
    }

    [HttpGet("{childId}/scheduled-screenshots")]
    [Authorize(Roles = "Guardian,Admin")]
    public async Task<IActionResult> GetScheduled(int childId, [FromQuery] string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return BadRequest("domain required");

        var guardianId = GetCurrentUserId();
        var list = await _screenshotService.GetScheduledAsync(guardianId, childId, domain);
        return Ok(list);
    }

    [HttpDelete("{childId}/scheduled-screenshots/{scheduleId}")]
    [Authorize(Roles = "Guardian,Admin")]
    public async Task<IActionResult> CancelScheduled(int childId, int scheduleId)
    {
        var guardianId = GetCurrentUserId();
        var ok = await _screenshotService.CancelScheduledAsync(guardianId, scheduleId);
        return ok ? Ok() : NotFound();
    }

    private int GetCurrentUserId()
    {
        return int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    }
}

public class ScheduleScreenshotDto
{
    public string Domain { get; set; } = string.Empty;
    public DateTime ScheduledAt { get; set; }
}
