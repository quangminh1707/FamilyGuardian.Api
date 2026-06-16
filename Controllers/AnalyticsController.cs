using FamilyGuardian.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace FamilyGuardian.Api.Controllers;

[ApiController]
[Route("api/analytics")]
[Authorize]
public class AnalyticsController : ControllerBase
{
    private readonly AppDbContext _db;

    public AnalyticsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("weekly")]
    public async Task<IActionResult> GetWeeklyUsage([FromQuery] int childId)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var hasAccess = await _db.GuardianChildRelationships
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
        if (!hasAccess)
            return Forbid();

        var since = DateTime.Now.Date.AddDays(-6);
        var sinceDate = DateOnly.FromDateTime(since);

        var stats = await _db.DailyUsageStats
            .AsNoTracking()
            .Where(s => s.ChildId == childId && s.UsageDate >= sinceDate)
            .GroupBy(s => s.UsageDate)
            .Select(g => new
            {
                Date = g.Key,
                TotalSeconds = g.Sum(s => s.TotalSeconds - s.BonusSeconds > 0
                    ? s.TotalSeconds - s.BonusSeconds
                    : 0)
            })
            .ToListAsync();

        var result = Enumerable.Range(0, 7)
            .Select(i => since.AddDays(i))
            .Select(date =>
            {
                var usageDate = DateOnly.FromDateTime(date);
                var totalSeconds = stats.FirstOrDefault(s => s.Date == usageDate)?.TotalSeconds ?? 0;
                return new
                {
                    Date = date.ToString("yyyy-MM-dd"),
                    TotalSeconds = totalSeconds
                };
            })
            .ToList();

        return Ok(result);
    }

    [HttpGet("top-domains")]
    public async Task<IActionResult> GetTopDomains([FromQuery] int childId)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var hasAccess = await _db.GuardianChildRelationships
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
        if (!hasAccess)
            return Forbid();

        var since = DateTime.Now.Date.AddDays(-29);
        var sinceDate = DateOnly.FromDateTime(since);

        var topDomains = await _db.DailyUsageStats
            .AsNoTracking()
            .Where(s => s.ChildId == childId && s.UsageDate >= sinceDate)
            .GroupBy(s => s.Domain)
            .Select(g => new
            {
                Domain = g.Key,
                TotalSeconds = g.Sum(s => s.TotalSeconds - s.BonusSeconds > 0
                    ? s.TotalSeconds - s.BonusSeconds
                    : 0)
            })
            .OrderByDescending(x => x.TotalSeconds)
            .Take(5)
            .ToListAsync();

        return Ok(topDomains);
    }
}
