using FamilyGuardian.Api.Models.DTOs.Children;

namespace FamilyGuardian.Api.Services.Interfaces;

public interface IChildService
{
    Task<List<ChildDto>> GetGuardianChildrenAsync(int guardianId);
    Task<ChildDetailDto> GetChildDetailAsync(int childId, int guardianId);
    Task UnlinkChildAsync(int childId, int guardianId);
    Task ToggleFilterAsync(int childId, int guardianId, bool filterEnabled);

}
