using FamilyGuardian.Api.Models;

namespace FamilyGuardian.Api.Services.Interfaces;

public interface IExtensionService
{
    Task<ExtensionCheckResponse> CheckAccessAsync(string googleId, string domain);
    Task<ExtensionConfigResponse?> GetConfigAsync(string googleId);
    Task<HeartbeatResult> UpdateHeartbeatAsync(string googleId, string domain, int? allowedWebsiteId);
    Task<bool> ToggleFilterAsync(int childId, bool enabled, int requestingGuardianId);
    Task UpdateExtensionPingAsync(string googleId);
    Task MarkWarningSentAsync(string googleId, int allowedWebsiteId, int warningNumber);
    Task MarkWarningShownAsync(string googleId, int allowedWebsiteId, int warningIndex);
}