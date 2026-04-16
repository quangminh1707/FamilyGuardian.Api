using FamilyGuardian.Api.Models.DTOs.AllowedWebsites;
using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace FamilyGuardian.Api.Controllers;

[Authorize(Roles = "Guardian,Admin")]
[ApiController]
[Route("api/children/{childId}/websites")]
public class AllowedWebsitesController : ControllerBase
{
    private readonly IAllowedWebsiteService _websiteService;

    public AllowedWebsitesController(IAllowedWebsiteService websiteService)
    {
        _websiteService = websiteService;
    }

    [HttpGet]
    public async Task<IActionResult> GetWebsites(int childId)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        try
        {
            var websites = await _websiteService.GetChildAllowedWebsitesAsync(childId, guardianId);
            return Ok(websites);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
    }

    [HttpPost]
    public async Task<IActionResult> AddWebsite(int childId, [FromBody] AddWebsiteRequest request)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        try
        {
            var result = await _websiteService.AddWebsiteAsync(childId, guardianId, request);
            return CreatedAtAction(null, result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{websiteId}")]
    public async Task<IActionResult> UpdateWebsite(int childId, int websiteId, [FromBody] UpdateWebsiteRequest request)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        try
        {
            await _websiteService.UpdateWebsiteAsync(childId, guardianId, websiteId, request);
            return NoContent();
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

    [HttpDelete("{websiteId}")]
    public async Task<IActionResult> DeleteWebsite(int childId, int websiteId)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        try
        {
            await _websiteService.DeleteWebsiteAsync(childId, guardianId, websiteId);
            return NoContent();
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

    [HttpPatch("{websiteId}/toggle")]
    public async Task<IActionResult> ToggleWebsite(int childId, int websiteId)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        try
        {
            var isActive = await _websiteService.ToggleWebsiteAsync(childId, guardianId, websiteId);
            return Ok(new { isActive });
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

    [HttpPost("{websiteId}/recheck")]
    public async Task<IActionResult> RecheckWebsite(int childId, int websiteId)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        try
        {
            await _websiteService.RecheckWebsiteAsync(childId, guardianId, websiteId);
            return Ok();
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
}
