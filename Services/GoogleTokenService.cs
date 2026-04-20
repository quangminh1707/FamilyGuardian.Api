using System.Net.Http.Headers;
using System.Text.Json;

namespace FamilyGuardian.Api.Services;

public interface IGoogleTokenService
{
    /// <summary>
    /// Verify Google Access Token and return google_id + user info
    /// </summary>
    Task<(bool Success, string GoogleId, string Email, string FullName)> VerifyTokenAsync(string accessToken);
}

public class GoogleTokenService : IGoogleTokenService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GoogleTokenService> _logger;

    public GoogleTokenService(HttpClient httpClient, ILogger<GoogleTokenService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<(bool Success, string GoogleId, string Email, string FullName)> VerifyTokenAsync(string accessToken)
    {
        try
        {
            // Call Google userinfo endpoint
            var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v3/userinfo");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to verify Google token. Status: {Status}", response.StatusCode);
                return (false, "", "", "");
            }

            var content = await response.Content.ReadAsStringAsync();
            using (JsonDocument doc = JsonDocument.Parse(content))
            {
                var root = doc.RootElement;
                
                var googleId = root.GetProperty("sub").GetString() ?? "";
                var email = root.GetProperty("email").GetString() ?? "";
                var name = root.TryGetProperty("name", out var nameElement) 
                    ? nameElement.GetString() ?? "" 
                    : "";

                _logger.LogInformation("Google token verified for user: {Email}", email);
                return (true, googleId, email, name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying Google token");
            return (false, "", "", "");
        }
    }
}
