using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FamilyGuardian.Api.Models.Entities;

[Table("website_timewindow_warning_configs")]
public class WebsiteTimeWindowWarningConfig
{
    [Key][Column("id")] public int Id { get; set; }
    [Column("allowed_website_id")] public int AllowedWebsiteId { get; set; }
    [Column("warn_minutes_before1")] public int WarnMinutesBefore1 { get; set; } = 10;
    [Column("message1")] public string Message1 { get; set; } = string.Empty;
    [Column("warn_minutes_before2")] public int? WarnMinutesBefore2 { get; set; }
    [Column("message2")] public string? Message2 { get; set; }
    [Column("is_active")] public bool IsActive { get; set; } = true;
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.Now;
    [Column("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public AllowedWebsite? AllowedWebsite { get; set; }
}
