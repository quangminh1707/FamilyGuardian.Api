namespace FamilyGuardian.Api.Models.Entities;

public class ProxyIpMapping
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public string IpAddress { get; set; } = null!;
    public string? DeviceName { get; set; }
    public string? GoogleId { get; set; }              // Google account ID của child (từ User.GoogleId)
    public string? GoogleEmail { get; set; }          // Email Google của child (từ User.Email)
    public bool IsActive { get; set; } = true;
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User Child { get; set; } = null!;
    public User Guardian { get; set; } = null!;
}
