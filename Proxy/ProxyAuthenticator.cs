using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Proxy;

/// <summary>
/// Xác thực child từ proxy header hoặc IP mapping.
/// </summary>
public class ProxyAuthenticator
{
    private readonly AppDbContext _db;
    private readonly IJwtService _jwtService;

    public ProxyAuthenticator(AppDbContext db, IJwtService jwtService)
    {
        _db = db;
        _jwtService = jwtService;
    }

    /// <summary>
    /// Trả về childId từ Bearer token trong Proxy-Authorization header.
    /// </summary>
    public async Task<int> AuthenticateAsync(string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader)) return 0;

        var token = authorizationHeader.Replace("Bearer ", "").Trim();
        var userId = _jwtService.ValidateToken(token);
        if (userId == null) return 0;

        var user = await _db.Users.FindAsync(userId.Value);
        if (user == null || user.Role != Models.Entities.UserRole.Child || !user.IsActive) return 0;

        return user.Id;
    }
}
