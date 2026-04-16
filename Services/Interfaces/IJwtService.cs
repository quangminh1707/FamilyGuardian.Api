using FamilyGuardian.Api.Models.Entities;

namespace FamilyGuardian.Api.Services.Interfaces;

public interface IJwtService
{
    string GenerateAccessToken(User user);
    string GenerateRefreshToken();
    int? ValidateToken(string token);
}
