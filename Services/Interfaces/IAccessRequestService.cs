using FamilyGuardian.Api.Models.DTOs;

namespace FamilyGuardian.Api.Services.Interfaces;

public interface IAccessRequestService
{
    Task<(bool Success, string Message)> SubmitRequestAsync(
        string googleId,
        string domain,
        string? fullUrl,
        string reason,
        int? requestedDurationMinutes,
        string? requestedStartTime,
        string? requestedEndTime);

    Task<List<AccessRequestDto>> GetPendingRequestsAsync(int guardianId);

    Task<List<AccessRequestDto>> GetRequestsAsync(int guardianId, string statusFilter = "pending");

    Task<(bool Success, string Message)> RespondToRequestAsync(int requestId, int guardianId, RespondAccessRequestDto dto);
}
