using FamilyGuardian.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // ── Real tables ──────────────────────────────────────────────────────────
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<GuardianChildRelationship> GuardianChildRelationships => Set<GuardianChildRelationship>();
    public DbSet<AllowedWebsite> AllowedWebsites => Set<AllowedWebsite>();
    public DbSet<WebAccessLog> WebAccessLogs => Set<WebAccessLog>();
    public DbSet<DailyUsageStat> DailyUsageStats => Set<DailyUsageStat>();
    public DbSet<UserOnlineStatus> UserOnlineStatuses => Set<UserOnlineStatus>();
    public DbSet<WebsiteCheckCache> WebsiteCheckCaches => Set<WebsiteCheckCache>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
 
    public DbSet<WebSession> WebSessions => Set<WebSession>();
    public DbSet<WebsiteWarningConfig> WebsiteWarningConfigs => Set<WebsiteWarningConfig>();
    public DbSet<WebsiteTimeWindowWarningConfig> WebsiteTimeWindowWarningConfigs => Set<WebsiteTimeWindowWarningConfig>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<WebsiteScreenshot> WebsiteScreenshots => Set<WebsiteScreenshot>();
    public DbSet<ScheduledScreenshot> ScheduledScreenshots => Set<ScheduledScreenshot>();

    // ── Stored procedure result types (keyless – không map tới bảng thật) ────
    public DbSet<ChildSpResult> ChildSpResults => Set<ChildSpResult>();
    public DbSet<AllowedWebsiteSpResult> AllowedWebsiteSpResults => Set<AllowedWebsiteSpResult>();
    public DbSet<CheckWebAccessSpResult> CheckWebAccessSpResults => Set<CheckWebAccessSpResult>();
    public DbSet<UsageHistorySpResult> UsageHistorySpResults => Set<UsageHistorySpResult>();
    public DbSet<AccessLogSpResult> AccessLogSpResults => Set<AccessLogSpResult>();
    public DbSet<AdminStatsSpResult> AdminStatsSpResults => Set<AdminStatsSpResult>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ── Enum conversions ───────────────────────────────────────────────
        mb.Entity<User>().Property(u => u.Role).HasConversion<string>();
        mb.Entity<WebAccessLog>().Property(l => l.AccessResult).HasConversion<string>();
        mb.Entity<Notification>().Property(n => n.Type).HasConversion<string>();

        // ── Unique Index ────────────────────────────────────────────────────
        mb.Entity<GuardianChildRelationship>()
            .HasIndex(r => new { r.GuardianId, r.ChildId }).IsUnique();

        mb.Entity<AllowedWebsite>()
            .HasIndex(w => new { w.ChildId, w.Domain }).IsUnique();

        mb.Entity<DailyUsageStat>()
            .HasIndex(d => new { d.ChildId, d.AllowedWebsiteId, d.UsageDate }).IsUnique();

        mb.Entity<ProxyIpMapping>()
            .HasIndex(p => p.IpAddress).IsUnique();

        // ── Primary Keys for keyless/special tables ─────────────────────────
        mb.Entity<WebsiteCheckCache>()
    .ToTable("website_check_cache")  
    .HasKey(c => c.Domain);
        mb.Entity<UserOnlineStatus>().ToTable("user_online_status").HasKey(o => o.UserId);
        mb.Entity<SystemSetting>().HasKey(s => s.SettingKey);

        // ── Relationships ───────────────────────────────────────────────────
        
        // User Online Status
        mb.Entity<UserOnlineStatus>()
            .HasOne(o => o.User).WithOne(u => u.OnlineStatus)
            .HasForeignKey<UserOnlineStatus>(o => o.UserId);

        // Guardian ↔ Child Relationships
        mb.Entity<GuardianChildRelationship>()
            .HasOne(r => r.Guardian).WithMany(u => u.AsGuardian)
            .HasForeignKey(r => r.GuardianId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<GuardianChildRelationship>()
            .HasOne(r => r.Child).WithMany(u => u.AsChild)
            .HasForeignKey(r => r.ChildId).OnDelete(DeleteBehavior.Cascade);

        // AllowedWebsite ↔ Child
        mb.Entity<AllowedWebsite>()
            .HasOne(w => w.Child).WithMany(u => u.AllowedWebsites)
            .HasForeignKey(w => w.ChildId).OnDelete(DeleteBehavior.Cascade);
        
        // AllowedWebsite ↔ Guardian (AddedBy)
        mb.Entity<AllowedWebsite>()
            .HasOne(w => w.Guardian).WithMany()
            .HasForeignKey(w => w.AddedBy).OnDelete(DeleteBehavior.Restrict);

        // ProxyIpMapping
        mb.Entity<ProxyIpMapping>()
            .HasOne(p => p.Child).WithMany().HasForeignKey(p => p.ChildId)
            .OnDelete(DeleteBehavior.Cascade);
        mb.Entity<ProxyIpMapping>()
            .HasOne(p => p.Guardian).WithMany().HasForeignKey(p => p.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);

        // WebAccessLog ↔ Child
        mb.Entity<WebAccessLog>()
            .HasOne(l => l.Child).WithMany(u => u.AccessLogs)
            .HasForeignKey(l => l.ChildId).OnDelete(DeleteBehavior.Cascade);

        // DailyUsageStat ↔ Child & Website
        mb.Entity<DailyUsageStat>()
            .HasOne(d => d.Child).WithMany().HasForeignKey(d => d.ChildId)
            .OnDelete(DeleteBehavior.Cascade);
        mb.Entity<DailyUsageStat>()
            .HasOne(d => d.Website).WithMany(w => w.DailyStats)
            .HasForeignKey(d => d.AllowedWebsiteId).OnDelete(DeleteBehavior.Cascade);

        // Notifications
        mb.Entity<Notification>()
            .HasOne(n => n.Guardian).WithMany(u => u.SentNotifications)
            .HasForeignKey(n => n.GuardianId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<Notification>()
            .HasOne(n => n.Child).WithMany(u => u.ReceivedNotifications)
            .HasForeignKey(n => n.ChildId).OnDelete(DeleteBehavior.Cascade);

        // WebSession
        mb.Entity<WebSession>()
            .HasOne(s => s.Child).WithMany()
            .HasForeignKey(s => s.ChildId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<WebSession>()
            .HasOne(s => s.Website).WithMany()
            .HasForeignKey(s => s.AllowedWebsiteId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<WebSession>()
            .HasIndex(s => new { s.ChildId, s.StartedAt });
        mb.Entity<WebSession>()
            .HasIndex(s => new { s.EndedAt, s.LastActivityAt }); // cho CloseIdleSessionsJob

        // WebsiteTimeWindowWarningConfig ↔ AllowedWebsite
        mb.Entity<WebsiteTimeWindowWarningConfig>()
            .HasOne(c => c.AllowedWebsite).WithMany()
            .HasForeignKey(c => c.AllowedWebsiteId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<WebsiteTimeWindowWarningConfig>()
            .HasIndex(c => c.AllowedWebsiteId).IsUnique();

        // AccessRequest
        mb.Entity<AccessRequest>(entity =>
        {
            entity.ToTable("access_requests");
            entity.HasIndex(e => new { e.GuardianId, e.Status });
            entity.HasIndex(e => new { e.ChildId, e.Domain });
            entity.HasOne(e => e.Child).WithMany(u => u.AccessRequestsAsChild)
                .HasForeignKey(e => e.ChildId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Guardian).WithMany(u => u.AccessRequestsAsGuardian)
                .HasForeignKey(e => e.GuardianId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── Keyless SP result types ─────────────────────────────────────────
        mb.Entity<ChildSpResult>().HasNoKey();
        mb.Entity<AllowedWebsiteSpResult>().HasNoKey();
        mb.Entity<CheckWebAccessSpResult>().HasNoKey();
        mb.Entity<UsageHistorySpResult>().HasNoKey();
        mb.Entity<AccessLogSpResult>().HasNoKey();
        mb.Entity<AdminStatsSpResult>().HasNoKey();
    }
}
