using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Hubs;
using FamilyGuardian.Api.Models.Entities;
using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Services;

public class ScreenshotService : IScreenshotService
{
    private readonly AppDbContext _context;
    private readonly IHubContext<NotificationHub> _hub;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ScreenshotService> _logger;

    public ScreenshotService(
        AppDbContext context,
        IHubContext<NotificationHub> hub,
        IWebHostEnvironment env,
        ILogger<ScreenshotService> logger)
    {
        _context = context;
        _hub = hub;
        _env = env;
        _logger = logger;
    }

    public async Task<ScreenshotRequestResult> RequestScreenshotAsync(
        int guardianId, int childId, string domain)
    {
        // Verify guardian có quyền với child
        var hasRelation = await _context.GuardianChildRelationships
            .AsNoTracking()
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);

        if (!hasRelation)
            return new ScreenshotRequestResult { Success = false, Error = "Không có quyền" };

        // Tìm allowed_website_id nếu có
        var website = await _context.AllowedWebsites
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.ChildId == childId && w.Domain == domain && w.IsActive);

        var screenshot = new WebsiteScreenshot
        {
            ChildId = childId,
            AllowedWebsiteId = website?.Id,
            Domain = domain,
            RequestedBy = guardianId,
            Status = "pending",
            CapturedAt = DateTime.Now,
            ImagePath = ""
        };

        _context.WebsiteScreenshots.Add(screenshot);
        await _context.SaveChangesAsync();

        // Gửi SignalR tới extension của con
        await _hub.Clients.Group($"child_{childId}")
            .SendAsync("CaptureScreenshot", new
            {
                screenshotId = screenshot.Id,
                domain = domain,
                allowedWebsiteId = website?.Id
            });

        _logger.LogInformation(
            "Screenshot requested: Id={Id}, ChildId={ChildId}, Domain={Domain}",
            screenshot.Id, childId, domain);

        return new ScreenshotRequestResult { Success = true, ScreenshotId = screenshot.Id };
    }

    public async Task<bool> SaveScreenshotAsync(int screenshotId, IFormFile imageFile)
    {
        var screenshot = await _context.WebsiteScreenshots.FindAsync(screenshotId);
        if (screenshot == null) return false;

        try
        {
            // Tạo thư mục lưu file
            var baseDir = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
            var screenshotsDir = Path.Combine(baseDir, "screenshots", screenshot.ChildId.ToString());
            Directory.CreateDirectory(screenshotsDir);

            var fileName = $"{screenshotId}_{DateTime.Now:yyyyMMddHHmmss}.jpg";
            var filePath = Path.Combine(screenshotsDir, fileName);

            await using var stream = File.Create(filePath);
            await imageFile.CopyToAsync(stream);

            screenshot.ImagePath = $"screenshots/{screenshot.ChildId}/{fileName}";
            screenshot.Status = "captured";
            screenshot.CapturedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            // Thông báo guardian ảnh đã sẵn sàng
            await _hub.Clients.Group($"guardian_{screenshot.RequestedBy}")
                .SendAsync("ScreenshotReady", new
                {
                    screenshotId = screenshot.Id,
                    childId = screenshot.ChildId,
                    domain = screenshot.Domain,
                    imageUrl = $"/{screenshot.ImagePath}",
                    capturedAt = screenshot.CapturedAt,
                    status = "captured"
                });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save screenshot {Id}", screenshotId);
            screenshot.Status = "failed";
            screenshot.ErrorMessage = ex.Message[..Math.Min(ex.Message.Length, 490)];
            await _context.SaveChangesAsync();

            await _hub.Clients.Group($"guardian_{screenshot.RequestedBy}")
                .SendAsync("ScreenshotReady", new
                {
                    screenshotId = screenshot.Id,
                    childId = screenshot.ChildId,
                    domain = screenshot.Domain,
                    status = "failed",
                    errorMessage = screenshot.ErrorMessage
                });

            return false;
        }
    }

    public async Task UpdateScreenshotStatusAsync(
        int screenshotId, string status, string? errorMessage = null)
    {
        var screenshot = await _context.WebsiteScreenshots.FindAsync(screenshotId);
        if (screenshot == null) return;

        screenshot.Status = status;
        screenshot.ErrorMessage = errorMessage;
        await _context.SaveChangesAsync();

        await _hub.Clients.Group($"guardian_{screenshot.RequestedBy}")
            .SendAsync("ScreenshotReady", new
            {
                screenshotId = screenshot.Id,
                childId = screenshot.ChildId,
                domain = screenshot.Domain,
                status = status,
                errorMessage = errorMessage,
                capturedAt = DateTime.Now
            });
    }

    public async Task<List<ScreenshotDto>> GetScreenshotsAsync(
        int guardianId, int childId, string domain, int limit = 10)
    {
        var hasRelation = await _context.GuardianChildRelationships
            .AsNoTracking()
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);

        if (!hasRelation) return [];

        return await _context.WebsiteScreenshots
            .AsNoTracking()
            .Where(s => s.ChildId == childId && s.Domain == domain)
            .OrderByDescending(s => s.CapturedAt)
            .Take(limit)
            .Select(s => new ScreenshotDto
            {
                Id = s.Id,
                Domain = s.Domain,
                Status = s.Status,
                ImageUrl = s.Status == "captured" && s.ImagePath != ""
                    ? $"/{s.ImagePath}"
                    : null,
                CapturedAt = s.CapturedAt,
                ErrorMessage = s.ErrorMessage
            })
            .ToListAsync();
    }
}
