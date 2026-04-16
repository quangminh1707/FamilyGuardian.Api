using FamilyGuardian.Api.Models.DTOs.Children;
using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace FamilyGuardian.Api.Controllers;

[Authorize(Roles = "Guardian,Admin")]
[ApiController]
[Route("api/children")]
public class ChildrenController : ControllerBase
{
    private readonly IChildService _childService;

    public ChildrenController(IChildService childService)
    {
        _childService = childService;
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

    [HttpPost("{childId}/ip-mappings")]
    public async Task<IActionResult> AddIpMapping(int childId, [FromBody] AddIpMappingRequest request)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        try
        {
            await _childService.AddIpMappingAsync(childId, guardianId, request);
            return Ok();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
    }

    [HttpGet("{childId}/ip-mappings")]
    public async Task<IActionResult> GetIpMappings(int childId)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        try
        {
            var mappings = await _childService.GetIpMappingsAsync(childId, guardianId);
            return Ok(mappings);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
    }

    [HttpDelete("{childId}/ip-mappings/{mappingId}")]
    public async Task<IActionResult> RemoveIpMapping(int childId, int mappingId)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _childService.RemoveIpMappingAsync(childId, guardianId, mappingId);
        return NoContent();
    }
}
