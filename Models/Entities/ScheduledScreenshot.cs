using System.ComponentModel.DataAnnotations.Schema;

namespace FamilyGuardian.Api.Models.Entities;

[Table("scheduled_screenshots")]
public class ScheduledScreenshot
{
    [Column("id")]
    public int Id { get; set; }

    [Column("child_id")]
    public int ChildId { get; set; }

    [Column("allowed_website_id")]
    public int? AllowedWebsiteId { get; set; }

    [Column("domain")]
    public string Domain { get; set; } = string.Empty;

    [Column("scheduled_at")]
    public DateTime ScheduledAt { get; set; }

    [Column("requested_by")]
    public int RequestedBy { get; set; }

    [Column("status")]
    public string Status { get; set; } = "pending";

    [Column("screenshot_id")]
    public int? ScreenshotId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("ChildId")]
    public User? Child { get; set; }
}
