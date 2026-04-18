using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Proxy;

/// <summary>
/// Track web sessions của child
/// Lưu cache in-memory để detect idle sessions
/// </summary>
public class SessionTracker : ISessionTracker
{
    private readonly AppDbContext _db;
    private readonly ILogger<SessionTracker> _logger;

    // Cache in-memory: key = (childId, websiteId), value = last seen DateTime
    private readonly Dictionary<(int, int), DateTime> _lastSeen = new();
    private readonly object _lock = new();

    private const int IdleMinutes = 5;
    private const int IdleSeconds = IdleMinutes * 60;

    public SessionTracker(AppDbContext db, ILogger<SessionTracker> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task RecordRequestAsync(int childId, int websiteId, string domain, string clientIp, CancellationToken ct = default)
    {
        var key = (childId, websiteId);
        var now = DateTime.UtcNow;

        Task? backgroundTask = null;

        lock (_lock)
        {
            if (_lastSeen.TryGetValue(key, out var lastTime))
            {
                // Request trong 5 phút → cộng vào daily_usage_stats
                var timeSpan = (int)(now - lastTime).TotalSeconds;
                if (timeSpan <= IdleSeconds)
                {
                    _lastSeen[key] = now;
                    // Cộng vào DB trong background
                    backgroundTask = AddUsageAsync(childId, websiteId, domain, Math.Min(timeSpan, 60), ct); // Max 60s per request
                    return backgroundTask;
                }
                // Else: idle > 5 phút, tạo session mới
            }

            // Tạo session mới
            _lastSeen[key] = now;
            backgroundTask = CreateNewSessionAsync(childId, websiteId, domain, now, ct);
        }

        // Fire-and-forget background task (không cần await caller)
        // Task sẽ tiếp tục chạy ngay cả sau khi method return
        #pragma warning disable CS4014
        backgroundTask?.ContinueWith(task =>
        {
            if (task.IsFaulted)
                _logger.LogError(task.Exception, "Error in background session task");
        }, TaskScheduler.Default);
        #pragma warning restore CS4014

        return Task.CompletedTask;
    }

    public async Task CloseIdleSessionsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var idleThreshold = now.AddSeconds(-IdleSeconds);

        try
        {
            var idleSessions = await _db.WebSessions
                .Where(s => s.EndedAt == null && s.LastActivityAt < idleThreshold)
                .ToListAsync(cancellationToken: ct);

            foreach (var session in idleSessions)
            {
                session.EndedAt = session.LastActivityAt;
                session.DurationSeconds = (int)(session.LastActivityAt - session.StartedAt).TotalSeconds;
            }

            if (idleSessions.Count > 0)
            {
                await _db.SaveChangesAsync(cancellationToken: ct);
                _logger.LogInformation($"Closed {idleSessions.Count} idle sessions");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error closing idle sessions");
        }
    }

    private async Task AddUsageAsync(int childId, int websiteId, string domain, int addSeconds, CancellationToken ct)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);

            var usage = await _db.DailyUsageStats
                .FirstOrDefaultAsync(d =>
                    d.ChildId == childId &&
                    d.AllowedWebsiteId == websiteId &&
                    d.UsageDate == today,
                cancellationToken: ct);

            if (usage != null)
            {
                usage.TotalSeconds += addSeconds;
                usage.RequestCount++;
            }
            else
            {
                usage = new DailyUsageStat
                {
                    ChildId = childId,
                    AllowedWebsiteId = websiteId,
                    Domain = domain,
                    UsageDate = today,
                    TotalSeconds = addSeconds,
                    RequestCount = 1
                };
                _db.DailyUsageStats.Add(usage);
            }

            await _db.SaveChangesAsync(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error adding usage for child {childId} website {websiteId}");
        }
    }

    private async Task CreateNewSessionAsync(int childId, int websiteId, string domain, DateTime startedAt, CancellationToken ct)
    {
        try
        {
            var session = new WebSession
            {
                ChildId = childId,
                AllowedWebsiteId = websiteId,
                Domain = domain,
                StartedAt = startedAt,
                LastActivityAt = startedAt
            };

            _db.WebSessions.Add(session);
            await _db.SaveChangesAsync(cancellationToken: ct);
            
            _logger.LogInformation($"Created new session: child {childId} → {domain}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error creating session for child {childId} domain {domain}");
        }
    }
}
