namespace FamilyGuardian.Api.Models.Entities;

/// <summary>
/// Một phiên truy cập liên tục vào 1 domain
/// Session được tạo khi có request mới sau khoảng idle > 5 phút
/// Session bị đóng bởi CloseIdleSessionsJob
/// </summary>
public class WebSession
{
    public long Id { get; set; }
    public int ChildId { get; set; }
    public int AllowedWebsiteId { get; set; }
    public string Domain { get; set; } = null!;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }           // null = session chưa đóng
    public int DurationSeconds { get; set; } = 0;   // tính khi đóng

    public User Child { get; set; } = null!;
    public AllowedWebsite Website { get; set; } = null!;
}
