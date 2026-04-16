using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FamilyGuardian.Api.Controllers;

[Authorize(Roles = "Guardian,Admin")]
[ApiController]
[Route("api/website-check")]
public class WebsiteCheckController : ControllerBase
{
    private readonly IWebsiteCheckService _checkService;

    public WebsiteCheckController(IWebsiteCheckService checkService)
    {
        _checkService = checkService;
    }

    [HttpGet]
    public async Task<IActionResult> Check([FromQuery] string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return BadRequest("Domain is required.");
        
        var result = await _checkService.CheckAsync(domain);
        return Ok(result);
    }
}
