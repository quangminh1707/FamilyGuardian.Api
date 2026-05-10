using FamilyGuardian.Api.Models.DTOs.Children;
using FamilyGuardian.Api.Services.Interfaces;
using FamilyGuardian.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Controllers;

[Authorize(Roles = "Guardian,Admin")]
[ApiController]
[Route("api/children")]
public class ChildrenController : ControllerBase
{
    private readonly IChildService _childService;
    private readonly AppDbContext _context;

    public ChildrenController(IChildService childService, AppDbContext context)
    {
        _childService = childService;
        _context = context;
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

    [HttpPatch("{childId}/filter")]
    public async Task<IActionResult> ToggleFilter(int childId, [FromBody] FilterToggleRequest request)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        try
        {
            await _childService.ToggleFilterAsync(childId, guardianId, request.FilterEnabled);
            return Ok(new { success = true, filterEnabled = request.FilterEnabled });
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

    // Feature 3: Kill Switch — Tạm dừng Internet
    [HttpPatch("{childId}/pause-internet")]
    public async Task<IActionResult> TogglePauseInternet(int childId)
    {
        var guardianId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var relationship = await _context.GuardianChildRelationships
            .FirstOrDefaultAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
        if (relationship == null) return Forbid();

        var child = await _context.Users.FindAsync(childId);
        if (child == null) return NotFound();

        child.InternetPaused = !child.InternetPaused;
        await _context.SaveChangesAsync();

        return Ok(new
        {
            internetPaused = child.InternetPaused,
            message = child.InternetPaused
                ? $"Đã tạm dừng internet cho {child.FullName}"
                : $"Đã bật lại internet cho {child.FullName}"
        });
    }

  
}
