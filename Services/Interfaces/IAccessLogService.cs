using FamilyGuardian.Api.Models.DTOs.Logs;

namespace FamilyGuardian.Api.Services.Interfaces;

public interface IAccessLogService
{
    Task<(List<AccessLogDto> Items, int TotalCount)> GetAccessLogsAsync(int childId, int guardianId, DateTime fromDate, DateTime toDate, int page, int pageSize, string? domain = null, string? result = null);
    Task<List<DailyUsageDto>> GetDailyUsageAsync(int childId, int guardianId, DateOnly date);
    Task<List<UsageHistoryDto>> GetUsageHistoryAsync(int childId, int guardianId, DateOnly fromDate, DateOnly toDate);
    Task<(List<SessionDto> Items, int TotalCount)> GetSessionsAsync(int childId, int guardianId, DateTime? fromDate, DateTime? toDate, int page, int pageSize);
    Task<object> GetUsageSummaryAsync(int childId, int guardianId, int days);
}
