namespace FamilyGuardian.Api.Models.Entities;

public class AccessRequest
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public int GuardianId { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string? FullUrl { get; set; }
    // "pending" | "approved_temp" | "approved_permanent" | "rejected"
    public string Status { get; set; } = "pending";
    public DateTime? TempExpiresAt { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.Now;
    public DateTime? RespondedAt { get; set; }

    // Navigation
    public User Child { get; set; } = null!;
    public User Guardian { get; set; } = null!;
}
