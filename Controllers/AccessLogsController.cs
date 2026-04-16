using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace FamilyGuardian.Api.Controllers;

[Authorize(Roles = "Guardian,Admin")]
[ApiController]
[Route("api/children/{childId}/logs")]
public class AccessLogsController : ControllerBase
{
    private readonly IAccessLogService _logService;

    public AccessLogsController(IAccessLogService logService)
    {
        _logService = logService;
    }

    [HttpGet]
    public async Task<IActionResult> GetLogs(int childId, 
        [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate, 
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? domain = null, [FromQuery] string? result = null)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var start = fromDate ?? DateTime.UtcNow.AddDays(-7);
        var end = toDate ?? DateTime.UtcNow;

        try
        {
            var (items, total) = await _logService.GetAccessLogsAsync(childId, guardianId, start, end, page, pageSize, domain, result);
            return Ok(new { items, totalCount = total, page, pageSize });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
    }

    [HttpGet("daily-usage")]
    public async Task<IActionResult> GetDailyUsage(int childId, [FromQuery] DateOnly? date)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var usageDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);

        try
        {
            var stats = await _logService.GetDailyUsageAsync(childId, guardianId, usageDate);
            return Ok(stats);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetUsageHistory(int childId, [FromQuery] DateOnly? fromDate, [FromQuery] DateOnly? toDate)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var start = fromDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7));
        var end = toDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        try
        {
            var history = await _logService.GetUsageHistoryAsync(childId, guardianId, start, end);
            return Ok(history);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
    }
}
