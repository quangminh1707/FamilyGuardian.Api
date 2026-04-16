using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace FamilyGuardian.Api.Controllers;

[ApiController]
[Route("api/online-status")]
public class OnlineStatusController : ControllerBase
{
    private readonly IOnlineStatusService _onlineStatus;

    public OnlineStatusController(IOnlineStatusService onlineStatus)
    {
        _onlineStatus = onlineStatus;
    }

    [HttpGet("{userId}")]
    [Authorize]
    public async Task<IActionResult> GetStatus(int userId)
    {
        var isOnline = await _onlineStatus.IsUserOnlineAsync(userId);
        return Ok(new { isOnline });
    }

    [HttpPost("heartbeat")]
    [Authorize]
    public async Task<IActionResult> Heartbeat()
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        
        await _onlineStatus.UpdateStatusAsync(userId, true, ip);
        return Ok();
    }
}
