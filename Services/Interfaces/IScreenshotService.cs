namespace FamilyGuardian.Api.Services.Interfaces;

public interface IScreenshotService
{
    Task<ScreenshotRequestResult> RequestScreenshotAsync(int guardianId, int childId, string domain);
    Task<bool> SaveScreenshotAsync(int screenshotId, IFormFile imageFile);
    Task UpdateScreenshotStatusAsync(int screenshotId, string status, string? errorMessage = null);
    Task<List<ScreenshotDto>> GetScreenshotsAsync(int guardianId, int childId, string domain, int limit = 10);
}

public class ScreenshotRequestResult
{
    public bool Success { get; set; }
    public int? ScreenshotId { get; set; }
    public string? Error { get; set; }
}

public class ScreenshotDto
{
    public int Id { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public DateTime CapturedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
