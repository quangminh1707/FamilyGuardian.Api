using FamilyGuardian.Api.Models.DTOs;

namespace FamilyGuardian.Api.Services.Interfaces;

public interface IAccessRequestService
{
    // Extension gọi — child gửi request
    Task<(bool Success, string Message)> SubmitRequestAsync(string googleId, string domain, string? fullUrl);

    // Guardian gọi — xem danh sách
    Task<List<AccessRequestDto>> GetPendingRequestsAsync(int guardianId);

    // Guardian gọi — phản hồi
    Task<(bool Success, string Message)> RespondToRequestAsync(int requestId, int guardianId, RespondAccessRequestDto dto);
}
