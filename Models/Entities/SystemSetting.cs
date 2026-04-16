using System.ComponentModel.DataAnnotations;

namespace FamilyGuardian.Api.Models.Entities;

public class SystemSetting
{
    [Key]
    public string SettingKey { get; set; } = null!;
    public string? SettingValue { get; set; }
    public string? Description { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
