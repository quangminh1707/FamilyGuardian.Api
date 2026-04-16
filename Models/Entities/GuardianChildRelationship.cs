namespace FamilyGuardian.Api.Models.Entities;

public class GuardianChildRelationship
{
    public int Id { get; set; }
    public int GuardianId { get; set; }
    public int ChildId { get; set; }
    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;

    public User Guardian { get; set; } = null!;
    public User Child { get; set; } = null!;
}
