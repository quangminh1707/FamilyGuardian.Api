namespace FamilyGuardian.Api.Services.Interfaces;

public interface IOnlineStatusService
{
    Task UpdateStatusAsync(int userId, bool isOnline, string? ip = null);
    Task<bool> IsUserOnlineAsync(int userId);
}
