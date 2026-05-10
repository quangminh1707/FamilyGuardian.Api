using FamilyGuardian.Api.Models.DTOs;
using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace FamilyGuardian.Api.Controllers;

[ApiController]
[Route("api/access-requests")]
[Authorize(Roles = "Guardian")]
public class AccessRequestsController : ControllerBase
{
    private readonly IAccessRequestService _service;

    public AccessRequestsController(IAccessRequestService service)
    {
        _service = service;
    }

    // GET /api/access-requests
    [HttpGet]
    public async Task<IActionResult> GetRequests()
    {
        var guardianId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var requests = await _service.GetPendingRequestsAsync(guardianId);
        return Ok(requests);
    }

    // PATCH /api/access-requests/{id}/respond
    [HttpPatch("{id}/respond")]
    public async Task<IActionResult> Respond(int id, [FromBody] RespondAccessRequestDto dto)
    {
        var guardianId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var (success, message) = await _service.RespondToRequestAsync(id, guardianId, dto);
        if (!success) return BadRequest(new { message });
        return Ok(new { message });
    }
}
