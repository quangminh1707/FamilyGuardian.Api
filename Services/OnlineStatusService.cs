using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Services;

public class OnlineStatusService : IOnlineStatusService
{
    private readonly AppDbContext _db;

    public OnlineStatusService(AppDbContext db)
    {
        _db = db;
    }

  public async Task UpdateStatusAsync(int userId, bool isOnline, string? ip = null)
{
    await _db.Database.ExecuteSqlInterpolatedAsync(
        $"CALL sp_UpdateOnlineStatus({userId}, {isOnline}, {ip})"
    );
}

    public async Task<bool> IsUserOnlineAsync(int userId)
    {
        var status = await _db.UserOnlineStatuses.FindAsync(userId);
        return status?.IsOnline ?? false;
    }
}
