namespace FamilyGuardian.Api.Models.DTOs.AllowedWebsites;

public class AddWebsiteRequest
{
    public string Domain { get; set; } = null!;
    public int? TimeLimitMinutes { get; set; }
    public string? AllowedStartTime { get; set; } // "HH:mm"
    public string? AllowedEndTime { get; set; }   // "HH:mm"
}

public class UpdateWebsiteRequest
{
    public int? TimeLimitMinutes { get; set; }
    public string? AllowedStartTime { get; set; }
    public string? AllowedEndTime { get; set; }
    public bool IsActive { get; set; }
}
