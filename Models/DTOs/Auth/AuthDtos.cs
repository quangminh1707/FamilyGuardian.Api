namespace FamilyGuardian.Api.Models.DTOs.Auth;

public class GoogleLoginRequest
{
    public string IdToken { get; set; } = null!;
}

public class LinkChildRequest
{
    public string IdToken { get; set; } = null!;
}

public class AuthResponse
{
    public string AccessToken { get; set; } = null!;
    public string RefreshToken { get; set; } = null!;
    public UserDto User { get; set; } = null!;
}

public class UserDto
{
    public int Id { get; set; }
    public string Email { get; set; } = null!;
    public string FullName { get; set; } = null!;
    public string? AvatarUrl { get; set; }
    public string Role { get; set; } = null!;
}
