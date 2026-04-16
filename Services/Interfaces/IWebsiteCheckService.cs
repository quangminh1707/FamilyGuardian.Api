using FamilyGuardian.Api.Models.DTOs.WebsiteCheck;

namespace FamilyGuardian.Api.Services.Interfaces;

public interface IWebsiteCheckService
{
    Task<WebsiteCheckResult> CheckAsync(string domain, bool forceRefresh = false, CancellationToken ct = default);
}
