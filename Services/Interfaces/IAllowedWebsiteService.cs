using FamilyGuardian.Api.Models.DTOs.Children;
using FamilyGuardian.Api.Models.DTOs.AllowedWebsites;

namespace FamilyGuardian.Api.Services.Interfaces;

public interface IAllowedWebsiteService
{
    Task<List<AllowedWebsiteDto>> GetChildAllowedWebsitesAsync(int childId, int guardianId);
    Task<AllowedWebsiteDto> AddWebsiteAsync(int childId, int guardianId, AddWebsiteRequest request);
    Task UpdateWebsiteAsync(int childId, int guardianId, int websiteId, UpdateWebsiteRequest request);
    Task DeleteWebsiteAsync(int childId, int guardianId, int websiteId);
    Task<bool> ToggleWebsiteAsync(int childId, int guardianId, int websiteId);
    Task RecheckWebsiteAsync(int childId, int guardianId, int websiteId);
}
