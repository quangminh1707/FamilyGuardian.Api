using FamilyGuardian.Api.Models;
using FamilyGuardian.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FamilyGuardian.Api.Services.Interfaces;
using FamilyGuardian.Api.Models.DTOs;
using System.Security.Claims;
using FamilyGuardian.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Controllers;

/// <summary>
/// Chrome Extension API endpoints
/// </summary>
[ApiController]
[Route("api/extension")]
[Authorize]
public class ExtensionController : ControllerBase
{
    private readonly IExtensionService _extensionService;
    private readonly IGoogleTokenService _googleTokenService;
    private readonly IAccessRequestService _accessRequestService;
    private readonly IScreenshotService _screenshotService;
    private readonly ILogger<ExtensionController> _logger;
    private readonly AppDbContext _context;

    public ExtensionController(
        IExtensionService extensionService,
        IGoogleTokenService googleTokenService,
        IAccessRequestService accessRequestService,
        IScreenshotService screenshotService,
        AppDbContext context,
        ILogger<ExtensionController> logger)
    {
        _extensionService = extensionService;
        _googleTokenService = googleTokenService;
        _accessRequestService = accessRequestService;
        _screenshotService = screenshotService;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/extension/check?domain=youtube.com
    /// Extension calls this to check if domain is allowed
    /// </summary>
    [HttpGet("check")]
    [AllowAnonymous]
    public async Task<ActionResult<ExtensionCheckResponse>> CheckAccess([FromQuery] string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return BadRequest(new { error = "Domain is required" });

        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return Unauthorized(new { error = "Missing or invalid authorization header" });

        var token = authHeader.Substring("Bearer ".Length);
        var (success, googleId, email, fullName) = await _googleTokenService.VerifyTokenAsync(token);
        if (!success)
        {
            _logger.LogWarning("Failed to verify Google token");
            return Unauthorized(new { error = "Invalid Google token" });
        }

        var result = await _extensionService.CheckAccessAsync(googleId, domain);
        return Ok(result);
    }

    /// <summary>
    /// GET /api/extension/config
    /// Extension calls this on startup to get user config
    /// </summary>
    [HttpGet("config")]
    [AllowAnonymous]
    public async Task<ActionResult<ExtensionConfigResponse>> GetConfig()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return Unauthorized(new { error = "Missing or invalid authorization header" });

        var token = authHeader.Substring("Bearer ".Length);
        var (success, googleId, email, fullName) = await _googleTokenService.VerifyTokenAsync(token);
        if (!success)
        {
            _logger.LogWarning("Failed to verify Google token");
            return Unauthorized(new { error = "Invalid Google token" });
        }

        var config = await _extensionService.GetConfigAsync(googleId);
        if (config == null)
            return NotFound(new { error = "Child account not found or not active" });

        return Ok(config);
    }

    /// <summary>
    /// POST /api/extension/heartbeat
    /// Extension sends heartbeat every 30 seconds to track time.
    /// Response includes limitExceeded and optional warning for child notification.
    /// </summary>
    [HttpPost("heartbeat")]
    [AllowAnonymous]
    public async Task<ActionResult> SendHeartbeat([FromBody] HeartbeatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Domain))
            return BadRequest(new { error = "Domain is required" });

        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return Unauthorized(new { error = "Missing or invalid authorization header" });

        var token = authHeader.Substring("Bearer ".Length);
        var (success, googleId, email, fullName) = await _googleTokenService.VerifyTokenAsync(token);
        if (!success)
        {
            _logger.LogWarning("Failed to verify Google token for heartbeat");
            return Unauthorized(new { error = "Invalid Google token" });
        }

        // ── Nhận HeartbeatResult thay vì bool ────────────────────────────────
        var result = await _extensionService.UpdateHeartbeatAsync(
            googleId, request.Domain, request.AllowedWebsiteId);

        return Ok(new
        {
            success       = true,
            limitExceeded = result.LimitExceeded,

            // warning != null → warning kích hoạt ngay heartbeat này (hiện notification ngay)
            warning = result.Warning == null ? null : new
            {
                message          = result.Warning.Message,
                remainingSeconds = result.Warning.RemainingSeconds
            },

            // schedule → extension đặt alarm chính xác, không phụ thuộc heartbeat 30s
            schedule = new
            {
                secondsUntilWarning1 = result.SecondsUntilWarning1,
                warningMessage1      = result.WarningMessage1,
                secondsUntilWarning2 = result.SecondsUntilWarning2,
                warningMessage2      = result.WarningMessage2,
                secondsUntilBlock    = result.SecondsUntilBlock
            },

            // timeInfo → extension hiển thị overlay thông tin thời gian
            timeInfo = (result.TimeWindowDisplay != null || result.MinutesRemainingToday != null) ? new
            {
                mode               = result.TimeWindowDisplay != null ? "timeWindow" : "minuteLimit",
                timeWindowDisplay  = result.TimeWindowDisplay,
                minutesUntilWindowEnd = result.MinutesUntilWindowEnd,
                minutesRemainingToday = result.MinutesRemainingToday
            } : (object?)null
        });
    }

    /// <summary>
    /// POST /api/extension/ping
    /// Extension pings every 30 seconds to signal it's alive
    /// </summary>
    [HttpPost("ping")]
    [AllowAnonymous]
    public async Task<ActionResult> Ping()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return Unauthorized();

        var token = authHeader.Substring("Bearer ".Length);
        var (success, googleId, _, _) = await _googleTokenService.VerifyTokenAsync(token);
        if (!success) return Unauthorized();

        await _extensionService.UpdateExtensionPingAsync(googleId);
        return Ok(new { success = true });
    }

     [HttpPost("warning-ack")]
    [AllowAnonymous]
    public async Task<ActionResult> WarningAck(
        [FromQuery] int allowedWebsiteId,
        [FromQuery] int warningNumber)
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return Unauthorized();
 
        var token = authHeader.Substring("Bearer ".Length);
        var (success, googleId, _, _) = await _googleTokenService.VerifyTokenAsync(token);
        if (!success) return Unauthorized();
 
        await _extensionService.MarkWarningSentAsync(googleId, allowedWebsiteId, warningNumber);
        return Ok(new { success = true });
    }

    [HttpPost("warning-shown")]
[AllowAnonymous]
public async Task<ActionResult> MarkWarningShown([FromBody] WarningShownRequest request)
{
    var authHeader = Request.Headers.Authorization.ToString();
    if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
        return Unauthorized();
 
    var token = authHeader.Substring("Bearer ".Length);
    var (success, googleId, _, _) = await _googleTokenService.VerifyTokenAsync(token);
    if (!success) return Unauthorized();
 
    await _extensionService.MarkWarningShownAsync(googleId, request.AllowedWebsiteId, request.WarningIndex);
    return Ok(new { success = true });
}

    /// <summary>
    /// POST /api/extension/tw-warning-ack
    /// Đánh dấu cảnh báo khung giờ đã gửi
    /// </summary>
    [HttpPost("tw-warning-ack")]
    [AllowAnonymous]
    public async Task<ActionResult> TwWarningAck(
        [FromQuery] int allowedWebsiteId,
        [FromQuery] int warningNumber)
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return Unauthorized();

        var token = authHeader.Substring("Bearer ".Length);
        var (success, googleId, _, _) = await _googleTokenService.VerifyTokenAsync(token);
        if (!success) return Unauthorized();

        await _extensionService.MarkTimeWindowWarningSentAsync(googleId, allowedWebsiteId, warningNumber);
        return Ok(new { success = true });
    }

    /// <summary>
    /// POST /api/extension/request-access
    /// Child sends a request to guardian(s) for website access
    /// </summary>
    [HttpPost("request-access")]
    [AllowAnonymous]
    public async Task<IActionResult> RequestAccess([FromBody] RequestAccessDto dto)
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return Unauthorized(new { error = "Missing or invalid authorization header" });

        var token = authHeader.Substring("Bearer ".Length);
        var (success, googleId, _, _) = await _googleTokenService.VerifyTokenAsync(token);
        if (!success)
            return Unauthorized(new { error = "Invalid Google token" });

        if (string.IsNullOrEmpty(dto.Domain))
            return BadRequest(new { message = "Domain không được để trống" });

        var (reqSuccess, message) = await _accessRequestService.SubmitRequestAsync(
            googleId,
            dto.Domain,
            dto.FullUrl,
            dto.Reason,
            dto.RequestedDurationMinutes,
            dto.RequestedStartTime,
            dto.RequestedEndTime);

        if (!reqSuccess) return BadRequest(new { message });
        return Ok(new { message });
    }

    /// <summary>
    /// GET /api/extension/pending-screenshots
    /// Extension polls every 5s to receive pending screenshot commands.
    /// </summary>
    [HttpGet("pending-screenshots")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPendingScreenshots()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return Unauthorized(new { error = "Missing or invalid authorization header" });

        var token = authHeader.Substring("Bearer ".Length);
        var (success, googleId, _, _) = await _googleTokenService.VerifyTokenAsync(token);
        if (!success)
            return Unauthorized(new { error = "Invalid Google token" });

        var child = await _context.Users.FirstOrDefaultAsync(u => u.GoogleId == googleId);
        if (child == null)
            return Unauthorized(new { error = "Child account not found" });

        var pending = await _context.WebsiteScreenshots
            .AsNoTracking()
            .Where(s => s.ChildId == child.Id
                     && s.Status == "pending"
                     && s.CapturedAt >= DateTime.Now.AddMinutes(-2))
            .OrderBy(s => s.CapturedAt)
            .Select(s => new
            {
                screenshotId = s.Id,
                domain = s.Domain
            })
            .ToListAsync();

        return Ok(pending);
    }

    /// <summary>
    /// GET /api/extension/block-info?domain=youtube.com
    /// Extension calls this to understand why a page is blocked.
    /// </summary>
    [HttpGet("block-info")]
    [AllowAnonymous]
    public async Task<IActionResult> GetBlockInfo([FromQuery] string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return BadRequest(new { error = "Domain is required" });

        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return Unauthorized(new { error = "Missing or invalid authorization header" });

        var token = authHeader.Substring("Bearer ".Length);
        var (success, googleId, _, _) = await _googleTokenService.VerifyTokenAsync(token);
        if (!success)
        {
            _logger.LogWarning("Failed to verify Google token for block-info");
            return Unauthorized(new { error = "Invalid Google token" });
        }

        var result = await _extensionService.GetBlockInfoAsync(googleId, domain);
        return Ok(result);
    }

    // ── Endpoint 3: Extension upload ảnh ──
    [HttpPost("upload-screenshot")]
    [AllowAnonymous]
    public async Task<IActionResult> UploadScreenshot(
        [FromQuery] int screenshotId,
        IFormFile image)
    {
        if (image == null || image.Length == 0)
            return BadRequest("No image");

        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return Unauthorized();
        var token = authHeader.Substring("Bearer ".Length);
        var (success, googleId, _, _) = await _googleTokenService.VerifyTokenAsync(token);
        if (!success) return Unauthorized();

        var child = await _context.Users.FirstOrDefaultAsync(u => u.GoogleId == googleId);
        if (child == null) return Unauthorized();

        var screenshot = await _context.WebsiteScreenshots.FindAsync(screenshotId);
        if (screenshot == null || screenshot.ChildId != child.Id)
            return Forbid();

        var saved = await _screenshotService.SaveScreenshotAsync(screenshotId, image);
        return saved ? Ok("saved") : StatusCode(500, "save failed");
    }

    // ── Endpoint 4: Extension báo tab_not_found hoặc failed ──
    [HttpPost("screenshot-result")]
    [AllowAnonymous]
    public async Task<IActionResult> ScreenshotResult([FromBody] ScreenshotResultDto dto)
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return Unauthorized();
        var token = authHeader.Substring("Bearer ".Length);
        var (success, googleId, _, _) = await _googleTokenService.VerifyTokenAsync(token);
        if (!success) return Unauthorized();

        var child = await _context.Users.FirstOrDefaultAsync(u => u.GoogleId == googleId);
        if (child == null) return Unauthorized();

        var screenshot = await _context.WebsiteScreenshots.FindAsync(dto.ScreenshotId);
        if (screenshot == null || screenshot.ChildId != child.Id)
            return Forbid();

        await _screenshotService.UpdateScreenshotStatusAsync(
            dto.ScreenshotId, dto.Status, dto.ErrorMessage);

        return Ok();
    }
}

public class ScreenshotResultDto
{
    public int ScreenshotId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}

public class HeartbeatRequest
{
    public string? Domain { get; set; }
    public int? AllowedWebsiteId { get; set; }
}

public class FilterToggleRequest
{
    public bool FilterEnabled { get; set; }
}

public class WarningShownRequest
{
    public int AllowedWebsiteId { get; set; }
    public int WarningIndex { get; set; } // 1 hoặc 2
}
