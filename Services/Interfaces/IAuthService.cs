using FamilyGuardian.Api.Models.DTOs.Auth;
using FamilyGuardian.Api.Models.DTOs.Children;

namespace FamilyGuardian.Api.Services.Interfaces;

public interface IAuthService
{
    Task<AuthResponse> GoogleLoginAsync(string idToken);
    Task<ChildDto> LinkChildGoogleAsync(string idToken, int guardianId);
    Task<AuthResponse> RefreshTokenAsync(string refreshToken);
    Task LogoutAsync(int userId);
}
