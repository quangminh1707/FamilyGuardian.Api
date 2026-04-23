using FamilyGuardian.Api.Models;
using FamilyGuardian.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FamilyGuardian.Api.Services.Interfaces;
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
    private readonly ILogger<ExtensionController> _logger;

    public ExtensionController(
        IExtensionService extensionService,
        IGoogleTokenService googleTokenService,
        ILogger<ExtensionController> logger)
    {
        _extensionService = extensionService;
        _googleTokenService = googleTokenService;
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
        {
            return BadRequest(new { error = "Domain is required" });
        }

        // Get token from header
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
        {
            return Unauthorized(new { error = "Missing or invalid authorization header" });
        }

        var token = authHeader.Substring("Bearer ".Length);

        // Verify token with Google
        var (success, googleId, email, fullName) = await _googleTokenService.VerifyTokenAsync(token);
        if (!success)
        {
            _logger.LogWarning("Failed to verify Google token");
            return Unauthorized(new { error = "Invalid Google token" });
        }

        // Check access
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
        // Get token from header
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
        {
            return Unauthorized(new { error = "Missing or invalid authorization header" });
        }

        var token = authHeader.Substring("Bearer ".Length);

        // Verify token with Google
        var (success, googleId, email, fullName) = await _googleTokenService.VerifyTokenAsync(token);
        if (!success)
        {
            _logger.LogWarning("Failed to verify Google token");
            return Unauthorized(new { error = "Invalid Google token" });
        }

        // Get config
        var config = await _extensionService.GetConfigAsync(googleId);
        if (config == null)
        {
            return NotFound(new { error = "Child account not found or not active" });
        }

        return Ok(config);
    }

    /// <summary>
    /// POST /api/extension/heartbeat
    /// Extension sends heartbeat every 30 seconds to track time
    /// </summary>
    [HttpPost("heartbeat")]
    [AllowAnonymous]
    public async Task<ActionResult> SendHeartbeat([FromBody] HeartbeatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Domain))
        {
            return BadRequest(new { error = "Domain is required" });
        }

        // Get token from header
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
        {
            return Unauthorized(new { error = "Missing or invalid authorization header" });
        }

        var token = authHeader.Substring("Bearer ".Length);

        // Verify token with Google
        var (success, googleId, email, fullName) = await _googleTokenService.VerifyTokenAsync(token);
        if (!success)
        {
            _logger.LogWarning("Failed to verify Google token for heartbeat");
            return Unauthorized(new { error = "Invalid Google token" });
        }

        // Update heartbeat
       bool limitExceeded = await _extensionService.UpdateHeartbeatAsync(
    googleId, request.Domain, request.AllowedWebsiteId);

return Ok(new { success = true, limitExceeded });
        
    }



    

   /// <summary>
/// POST /api/extension/ping
/// Extension pings every 10 seconds to signal it's alive
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
 