using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FamilyGuardian.Api.Models.Entities;

[Table("website_screenshots")]
public class WebsiteScreenshot
{
    [Column("id")]
    public int Id { get; set; }

    [Column("child_id")]
    public int ChildId { get; set; }

    [Column("allowed_website_id")]
    public int? AllowedWebsiteId { get; set; }

    [Column("domain")]
    [MaxLength(255)]
    public string Domain { get; set; } = string.Empty;

    [Column("image_path")]
    [MaxLength(500)]
    public string ImagePath { get; set; } = string.Empty;

    [Column("captured_at")]
    public DateTime CapturedAt { get; set; } = DateTime.Now;

    [Column("requested_by")]
    public int RequestedBy { get; set; }

    [Column("status")]
    [MaxLength(20)]
    public string Status { get; set; } = "pending";
    // Values: pending | captured | failed | tab_not_found

    [Column("error_message")]
    [MaxLength(500)]
    public string? ErrorMessage { get; set; }

    [ForeignKey("ChildId")]
    public User? Child { get; set; }

    [ForeignKey("AllowedWebsiteId")]
    public AllowedWebsite? AllowedWebsite { get; set; }
}
