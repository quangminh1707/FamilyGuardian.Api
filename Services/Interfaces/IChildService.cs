using FamilyGuardian.Api.Models.DTOs.Children;

namespace FamilyGuardian.Api.Services.Interfaces;

public interface IChildService
{
    Task<List<ChildDto>> GetGuardianChildrenAsync(int guardianId);
    Task<ChildDetailDto> GetChildDetailAsync(int childId, int guardianId);
    Task UnlinkChildAsync(int childId, int guardianId);
    Task AddIpMappingAsync(int childId, int guardianId, AddIpMappingRequest request);
    Task<List<ProxyIpMappingDto>> GetIpMappingsAsync(int childId, int guardianId);
    Task RemoveIpMappingAsync(int childId, int guardianId, int mappingId);
}
