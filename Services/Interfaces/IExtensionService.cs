using FamilyGuardian.Api.Models;

namespace FamilyGuardian.Api.Services.Interfaces;

public interface IExtensionService
{
  
     Task<ExtensionCheckResponse> CheckAccessAsync(string googleId, string domain);

    Task<ExtensionConfigResponse?> GetConfigAsync(string googleId);

     Task<bool> UpdateHeartbeatAsync(string googleId, string domain, int? allowedWebsiteId);
    Task<bool> ToggleFilterAsync(int childId, bool enabled, int requestingGuardianId);
    Task UpdateExtensionPingAsync(string googleId);
    
}