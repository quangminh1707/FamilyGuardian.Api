using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Models.DTOs.Auth;
using FamilyGuardian.Api.Models.DTOs.Children;
using FamilyGuardian.Api.Models.Entities;
using FamilyGuardian.Api.Services.Interfaces;
using Google.Apis.Auth;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Services;

public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly IJwtService _jwt;
    private readonly IOnlineStatusService _onlineStatus;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthService> _logger;

    public AuthService(AppDbContext db, IJwtService jwt, IOnlineStatusService onlineStatus,
        IConfiguration config, ILogger<AuthService> logger)
    {
        _db = db;
        _jwt = jwt;
        _onlineStatus = onlineStatus;
        _config = config;
        _logger = logger;
    }

    public async Task<AuthResponse> GoogleLoginAsync(string idToken)
    {
        var payload = await ValidateGoogleToken(idToken);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.GoogleId == payload.Subject);
        if (user == null)
        {
            user = await _db.Users.FirstOrDefaultAsync(u => u.Email == payload.Email);
            if (user != null)
            {
                user.GoogleId = payload.Subject;
                user.AvatarUrl = payload.Picture;
            }
            else
            {
                user = new User
                {
                    GoogleId = payload.Subject,
                    Email = payload.Email,
                    FullName = payload.Name,
                    AvatarUrl = payload.Picture,
                    Role = UserRole.Guardian
                };
                _db.Users.Add(user);
            }
        }
        else
        {
            user.FullName = payload.Name;
            user.AvatarUrl = payload.Picture;
        }

        if (user.Role == UserRole.Child)
            throw new UnauthorizedAccessException("Tài khoản trẻ em không thể đăng nhập trang này.");

        if (!user.IsActive)
            throw new UnauthorizedAccessException("Tài khoản đã bị khóa.");

        await _db.SaveChangesAsync();

        var refreshTokenString = _jwt.GenerateRefreshToken();
        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            Token = refreshTokenString,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });

        await _db.SaveChangesAsync();
        await _onlineStatus.UpdateStatusAsync(user.Id, true);

        return new AuthResponse
        {
            AccessToken = _jwt.GenerateAccessToken(user),
            RefreshToken = refreshTokenString,
            User = new UserDto
            {
                Id = user.Id,
                Email = user.Email,
                FullName = user.FullName,
                AvatarUrl = user.AvatarUrl,
                Role = user.Role.ToString()
            }
        };
    }

    public async Task<ChildDto> LinkChildGoogleAsync(string idToken, int guardianId)
    {
        var payload = await ValidateGoogleToken(idToken);

        var child = await _db.Users.FirstOrDefaultAsync(u => u.GoogleId == payload.Subject);
        if (child == null)
        {
            child = await _db.Users.FirstOrDefaultAsync(u => u.Email == payload.Email);
            if (child != null && child.Role != UserRole.Child)
                throw new InvalidOperationException("Email này đã được sử dụng bởi một tài khoản khác.");

            if (child == null)
            {
                child = new User
                {
                    GoogleId = payload.Subject,
                    Email = payload.Email,
                    FullName = payload.Name,
                    AvatarUrl = payload.Picture,
                    Role = UserRole.Child
                };
                _db.Users.Add(child);
                await _db.SaveChangesAsync();
            }
            else
            {
                child.GoogleId = payload.Subject;
                child.AvatarUrl = payload.Picture;
                await _db.SaveChangesAsync();
            }
        }
        else if (child.Role != UserRole.Child)
        {
            throw new InvalidOperationException("Đây không phải tài khoản trẻ em.");
        }

        var exists = await _db.GuardianChildRelationships
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == child.Id);
        if (exists)
            throw new InvalidOperationException("Tài khoản con này đã được liên kết.");

        _db.GuardianChildRelationships.Add(new GuardianChildRelationship
        {
            GuardianId = guardianId,
            ChildId = child.Id
        });

        await _db.SaveChangesAsync();

        return new ChildDto
        {
            Id = child.Id,
            FullName = child.FullName,
            Email = child.Email,
            AvatarUrl = child.AvatarUrl
        };
    }

    public async Task<AuthResponse> RefreshTokenAsync(string refreshToken)
    {
        var tokenRecord = await _db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.Token == refreshToken && !t.IsRevoked && t.ExpiresAt > DateTime.UtcNow);

        if (tokenRecord == null)
            throw new UnauthorizedAccessException("Token không hợp lệ hoặc đã hết hạn.");

        tokenRecord.IsRevoked = true;
        
        var newRefreshToken = _jwt.GenerateRefreshToken();
        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = tokenRecord.UserId,
            Token = newRefreshToken,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });

        await _db.SaveChangesAsync();

        return new AuthResponse
        {
            AccessToken = _jwt.GenerateAccessToken(tokenRecord.User),
            RefreshToken = newRefreshToken,
            User = new UserDto
            {
                Id = tokenRecord.User.Id,
                Email = tokenRecord.User.Email,
                FullName = tokenRecord.User.FullName,
                AvatarUrl = tokenRecord.User.AvatarUrl,
                Role = tokenRecord.User.Role.ToString()
            }
        };
    }

    public async Task LogoutAsync(int userId)
    {
        var tokens = await _db.RefreshTokens
            .Where(t => t.UserId == userId && !t.IsRevoked)
            .ToListAsync();

        foreach (var t in tokens) t.IsRevoked = true;
        
        await _db.SaveChangesAsync();
        await _onlineStatus.UpdateStatusAsync(userId, false);
    }

    private async Task<GoogleJsonWebSignature.Payload> ValidateGoogleToken(string idToken)
    {
        try
        {
            var settings = new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = new[] { _config["Google:ClientId"] }
            };
            return await GoogleJsonWebSignature.ValidateAsync(idToken, settings);
        }
        catch (InvalidJwtException ex)
        {
            _logger.LogError(ex, "Google token validation failed");
            throw new UnauthorizedAccessException("Token Google không hợp lệ.");
        }
    }
}
