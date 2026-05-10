using FamilyGuardian.Api.Models;
using FamilyGuardian.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FamilyGuardian.Api.Services.Interfaces;
using FamilyGuardian.Api.Models.DTOs;
using System.Security.Claims;

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
    private readonly ILogger<ExtensionController> _logger;

    public ExtensionController(
        IExtensionService extensionService,
        IGoogleTokenService googleTokenService,
        IAccessRequestService accessRequestService,
        ILogger<ExtensionController> logger)
    {
        _extensionService = extensionService;
        _googleTokenService = googleTokenService;
        _accessRequestService = accessRequestService;
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
            googleId, dto.Domain, dto.FullUrl);

        if (!reqSuccess) return BadRequest(new { message });
        return Ok(new { message });
    }
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