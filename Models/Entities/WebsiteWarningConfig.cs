using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FamilyGuardian.Api.Models.Entities;

[Table("website_warning_configs")]
public class WebsiteWarningConfig
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("allowed_website_id")]
    public int AllowedWebsiteId { get; set; }

    [Column("threshold1_percent")]
    public int Threshold1Percent { get; set; } = 80;

    [Column("threshold1_message")]
    public string Threshold1Message { get; set; } = string.Empty;

    [Column("threshold2_percent")]
    public int? Threshold2Percent { get; set; }

    [Column("threshold2_message")]
    public string? Threshold2Message { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public AllowedWebsite? AllowedWebsite { get; set; }
}
