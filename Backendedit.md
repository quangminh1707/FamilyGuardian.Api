# BACKEND ADDITIONS
## Chỉ thêm phần này vào backend hiện có — KHÔNG làm lại phần đã có

---

## A. NUGET PACKAGE CẦN THÊM

```bash
dotnet add package Titanium.Web.Proxy
```

```xml
<!-- Thêm vào .csproj -->
<PackageReference Include="Titanium.Web.Proxy" Version="3.*" />
```

---

## B. ENTITY MỚI: WebSession.cs

Thêm vào `Models/Entities/WebSession.cs`:

```csharp
namespace FamilyGuardian.Api.Models.Entities
{
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
}
```

---

## C. THÊM VÀO AppDbContext.cs

```csharp
// Thêm DbSet
public DbSet<WebSession> WebSessions => Set<WebSession>();

// Thêm vào OnModelCreating:
modelBuilder.Entity<WebSession>()
    .HasOne(s => s.Child).WithMany()
    .HasForeignKey(s => s.ChildId).OnDelete(DeleteBehavior.Cascade);

modelBuilder.Entity<WebSession>()
    .HasOne(s => s.Website).WithMany()
    .HasForeignKey(s => s.AllowedWebsiteId).OnDelete(DeleteBehavior.Cascade);

modelBuilder.Entity<WebSession>()
    .HasIndex(s => new { s.ChildId, s.StartedAt });

modelBuilder.Entity<WebSession>()
    .HasIndex(s => new { s.EndedAt, s.LastActivityAt }); // cho CloseIdleSessionsJob
```

---

## D. FILE MỚI CẦN TẠO TRONG FOLDER Proxy/
File `ProxyAccessChecker_SessionTracker.cs` 
//// ============================================================
// FILE: Proxy/ProxyAccessChecker.cs
// Kiểm tra child có được phép truy cập domain không
// ============================================================

using Microsoft.EntityFrameworkCore;
using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Models.Entities;

namespace FamilyGuardian.Api.Proxy
{
    public enum AccessDecision { Allowed, Blocked, NoMapping }

    public class AccessCheckResult
    {
        public AccessDecision Decision { get; set; }
        public int? ChildId { get; set; }
        public int? AllowedWebsiteId { get; set; }
        public string Reason { get; set; } = "";
    }

    public interface IProxyAccessChecker
    {
        Task<AccessCheckResult> CheckAsync(string clientIp, string domain);
    }

    public class ProxyAccessChecker : IProxyAccessChecker
    {
        private readonly AppDbContext _db;
        private readonly ILogger<ProxyAccessChecker> _logger;

        public ProxyAccessChecker(AppDbContext db, ILogger<ProxyAccessChecker> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<AccessCheckResult> CheckAsync(string clientIp, string domain)
        {
            // Normalize domain: bỏ port nếu có
            domain = domain.Split(':')[0].ToLower().TrimStart('.');

            // ── Bước 1: Xác định child từ IP ─────────────────────
            var mapping = await _db.ProxyIpMappings
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.IpAddress == clientIp && m.IsActive);

            if (mapping == null)
            {
                // IP chưa được mapping → CHẶN (hoặc cho qua tùy setting)
                // Theo yêu cầu: mặc định chặn tất cả
                return new AccessCheckResult
                {
                    Decision = AccessDecision.Blocked,
                    Reason   = $"Thiết bị {clientIp} chưa được đăng ký trong hệ thống"
                };
            }

            var childId = mapping.ChildId;

            // ── Bước 2: Tìm domain trong allowed_websites ─────────
            // Khớp exact (youtube.com) hoặc subdomain (www.youtube.com, m.youtube.com)
            var website = await _db.AllowedWebsites
                .AsNoTracking()
                .Where(w => w.ChildId == childId
                         && w.IsActive
                         && (
                             w.Domain == domain ||                          // exact
                             domain.EndsWith("." + w.Domain) ||             // subdomain
                             w.Domain.StartsWith("*.") &&
                                 domain.EndsWith(w.Domain[2..])             // wildcard *.youtube.com
                         ))
                .OrderByDescending(w => w.Domain.Length) // domain cụ thể nhất được ưu tiên
                .FirstOrDefaultAsync();

            if (website == null)
            {
                return new AccessCheckResult
                {
                    Decision = AccessDecision.Blocked,
                    ChildId  = childId,
                    Reason   = $"\"{domain}\" không có trong danh sách website được phép"
                };
            }

            // ── Bước 3: Kiểm tra khung giờ ───────────────────────
            if (website.AllowedStartTime.HasValue && website.AllowedEndTime.HasValue)
            {
                var now = TimeOnly.FromDateTime(DateTime.Now);
                if (now < website.AllowedStartTime.Value || now > website.AllowedEndTime.Value)
                {
                    return new AccessCheckResult
                    {
                        Decision        = AccessDecision.Blocked,
                        ChildId         = childId,
                        AllowedWebsiteId = website.Id,
                        Reason          = $"Ngoài khung giờ cho phép " +
                                          $"({website.AllowedStartTime:hh\\:mm} – {website.AllowedEndTime:hh\\:mm})"
                    };
                }
            }

            // ── Bước 4: Kiểm tra giới hạn thời gian ngày ─────────
            if (website.TimeLimitMinutes.HasValue)
            {
                var today = DateOnly.FromDateTime(DateTime.Today);
                var stat  = await _db.DailyUsageStats
                    .AsNoTracking()
                    .FirstOrDefaultAsync(d => d.ChildId == childId
                                           && d.AllowedWebsiteId == website.Id
                                           && d.UsageDate == today);

                if (stat != null && stat.TotalSeconds >= website.TimeLimitMinutes.Value * 60)
                {
                    var limitStr = website.TimeLimitMinutes.Value >= 60
                        ? $"{website.TimeLimitMinutes.Value / 60} giờ {website.TimeLimitMinutes.Value % 60} phút"
                        : $"{website.TimeLimitMinutes.Value} phút";

                    return new AccessCheckResult
                    {
                        Decision         = AccessDecision.Blocked,
                        ChildId          = childId,
                        AllowedWebsiteId = website.Id,
                        Reason           = $"Đã hết thời gian cho phép ({limitStr}/ngày)"
                    };
                }
            }

            // ── Tất cả pass → Cho phép ───────────────────────────
            return new AccessCheckResult
            {
                Decision         = AccessDecision.Allowed,
                ChildId          = childId,
                AllowedWebsiteId = website.Id,
                Reason           = ""
            };
        }
    }
}


// ============================================================
// FILE: Proxy/SessionTracker.cs
// Tracking thời gian sử dụng web theo domain + child
// Logic: mỗi request → upsert session. Nếu request kế tiếp
//        cách request trước > IDLE_THRESHOLD giây → đóng session cũ
// ============================================================

namespace FamilyGuardian.Api.Proxy
{
    public interface ISessionTracker
    {
        Task RecordRequestAsync(int childId, int allowedWebsiteId, string domain, string ip);
        Task CloseIdleSessionsAsync(CancellationToken ct = default);
    }

    public class SessionTracker : ISessionTracker
    {
        // Nếu không có request mới sau IDLE_SECONDS giây → đóng session
        private const int IDLE_SECONDS = 300; // 5 phút

        private readonly AppDbContext _db;
        private readonly ILogger<SessionTracker> _logger;

        // Cache in-memory: key = (childId, websiteId) → last request time
        // Dùng để tránh query DB mỗi request
        private static readonly Dictionary<(int childId, int websiteId), DateTime> _lastSeen = new();
        private static readonly SemaphoreSlim _lock = new(1, 1);

        public SessionTracker(AppDbContext db, ILogger<SessionTracker> logger)
        {
            _db     = db;
            _logger = logger;
        }

        public async Task RecordRequestAsync(int childId, int allowedWebsiteId, string domain, string ip)
        {
            var now  = DateTime.UtcNow;
            var key  = (childId, allowedWebsiteId);
            var today = DateOnly.FromDateTime(DateTime.Today);

            await _lock.WaitAsync();
            try
            {
                bool hadRecentRequest = _lastSeen.TryGetValue(key, out var lastTime)
                                     && (now - lastTime).TotalSeconds < IDLE_SECONDS;
                _lastSeen[key] = now;
                _lock.Release();

                if (hadRecentRequest)
                {
                    // Tiếp tục session hiện tại → chỉ cộng thêm giây
                    var secondsToAdd = (int)(now - lastTime).TotalSeconds;
                    await UpsertDailyUsageAsync(childId, allowedWebsiteId, domain, today, secondsToAdd);
                }
                else
                {
                    // Session mới (request đầu tiên hoặc sau idle)
                    // Tạo web_session record mới
                    var session = new WebSession
                    {
                        ChildId          = childId,
                        AllowedWebsiteId = allowedWebsiteId,
                        Domain           = domain,
                        StartedAt        = now,
                        LastActivityAt   = now,
                    };
                    _db.WebSessions.Add(session);
                    await _db.SaveChangesAsync();

                    // Cộng 1 giây cho lần đầu tiên
                    await UpsertDailyUsageAsync(childId, allowedWebsiteId, domain, today, 1);
                }
            }
            catch
            {
                if (_lock.CurrentCount == 0) _lock.Release();
                throw;
            }
        }

        private async Task UpsertDailyUsageAsync(int childId, int websiteId,
            string domain, DateOnly date, int addSeconds)
        {
            if (addSeconds <= 0) return;

            var stat = await _db.DailyUsageStats
                .FirstOrDefaultAsync(d => d.ChildId == childId
                                       && d.AllowedWebsiteId == websiteId
                                       && d.UsageDate == date);
            if (stat == null)
            {
                _db.DailyUsageStats.Add(new DailyUsageStat
                {
                    ChildId          = childId,
                    AllowedWebsiteId = websiteId,
                    Domain           = domain,
                    UsageDate        = date,
                    TotalSeconds     = addSeconds,
                    RequestCount     = 1,
                });
            }
            else
            {
                stat.TotalSeconds += addSeconds;
                stat.RequestCount += 1;
            }

            await _db.SaveChangesAsync();
        }

        /// <summary>
        /// Đóng các session đã idle quá lâu → cập nhật EndedAt + Duration
        /// Được gọi bởi CloseIdleSessionsJob mỗi phút
        /// </summary>
        public async Task CloseIdleSessionsAsync(CancellationToken ct = default)
        {
            var cutoff   = DateTime.UtcNow.AddSeconds(-IDLE_SECONDS);
            var sessions = await _db.WebSessions
                .Where(s => s.EndedAt == null && s.LastActivityAt < cutoff)
                .ToListAsync(ct);

            foreach (var s in sessions)
            {
                s.EndedAt        = s.LastActivityAt; // kết thúc tại lần activity cuối
                s.DurationSeconds = (int)(s.EndedAt.Value - s.StartedAt).TotalSeconds;
            }

            if (sessions.Count > 0)
            {
                await _db.SaveChangesAsync(ct);
                // _logger.LogDebug("Closed {Count} idle sessions", sessions.Count);
            }
        }
    }
}


// ============================================================
// FILE: Jobs/CloseIdleSessionsJob.cs
// Quartz job: chạy mỗi 1 phút → đóng session idle
// ============================================================

using Quartz;

namespace FamilyGuardian.Api.Jobs
{
    [DisallowConcurrentExecution]
    public class CloseIdleSessionsJob : IJob
    {
        private readonly ISessionTracker _tracker;

        public CloseIdleSessionsJob(ISessionTracker tracker)
        {
            _tracker = tracker;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            await _tracker.CloseIdleSessionsAsync(context.CancellationToken);
        }
    }
}//



### Proxy/ProxyAccessChecker.cs
Xem file `ProxyAccessChecker_SessionTracker.cs` đã cung cấp.

```
Interface: IProxyAccessChecker
Class:     ProxyAccessChecker
- Input:  clientIp (string), domain (string)
- Output: AccessCheckResult { Decision, ChildId, AllowedWebsiteId, Reason }
- Logic:
  1. Tìm proxy_ip_mappings theo clientIp → lấy childId
  2. Tìm allowed_websites của childId khớp domain (exact + subdomain)
  3. Kiểm tra khung giờ
  4. Kiểm tra time limit từ daily_usage_stats
  5. Trả kết quả
```

### Proxy/SessionTracker.cs
Xem file `ProxyAccessChecker_SessionTracker.cs` đã cung cấp.

```
Interface: ISessionTracker
Class:     SessionTracker
- RecordRequestAsync(childId, websiteId, domain, ip):
    • Giữ cache in-memory (Dictionary) lastSeen per (childId, websiteId)
    • Nếu request trong 5 phút → cộng seconds vào daily_usage_stats
    • Nếu mới / sau idle → tạo WebSession record mới
- CloseIdleSessionsAsync():
    • Query WebSessions có EndedAt = null VÀ LastActivityAt < (now - 5min)
    • Set EndedAt = LastActivityAt, tính DurationSeconds
```
//

### Proxy/ProxyServer.cs (THAY THẾ file cũ)
Xem file `ProxyServer.cs` đã cung cấp.

```
Class: FamilyProxyServer : BackgroundService
- Dùng Titanium.Web.Proxy thay cho TcpListener thủ công
- Tạo root CA, trust trên máy chạy server
- Event: OnBeforeTunnelConnectAsync → check domain trước khi decrypt SSL
- Event: OnBeforeRequestAsync → check domain cho HTTP + intercept HTTPS sau decrypt
- Mọi request blocked → trả HTML block page (không redirect)
- Mọi request allowed → gọi SessionTracker.RecordRequestAsync
```
//
// ============================================================
// FILE: Proxy/ProxyServer.cs
// Dùng Titanium.Web.Proxy để chặn cả HTTP và HTTPS
// NuGet: dotnet add package Titanium.Web.Proxy
// ============================================================

using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Models.Entities;

namespace FamilyGuardian.Api.Proxy
{
    /// <summary>
    /// ProxyServer dùng Titanium.Web.Proxy
    /// - Intercept HTTP và HTTPS (MITM SSL)
    /// - Chặn domain không có trong allowed_websites của child
    /// - Ghi nhận session truy cập để tính thời gian
    /// </summary>
    public class FamilyProxyServer : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<FamilyProxyServer> _logger;
        private ProxyServer? _proxy;

        public FamilyProxyServer(
            IServiceScopeFactory scopeFactory,
            IConfiguration config,
            ILogger<FamilyProxyServer> logger)
        {
            _scopeFactory = scopeFactory;
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var port = _config.GetValue<int>("Proxy:Port", 8888);

            _proxy = new ProxyServer(
                userTrustRootCertificate: true,     // auto trust root cert trên Windows
                machineTrustRootCertificate: true   // cài vào machine store
            );

            // Tạo root CA để sign certificate giả cho HTTPS
            _proxy.CertificateManager.RootCertificateName = "Family Guardian CA";
            _proxy.CertificateManager.RootCertificateIssuerName = "Family Guardian";
            await _proxy.CertificateManager.CreateRootCertificateAsync();
            await _proxy.CertificateManager.TrustRootCertificateAsync(); // trust trên máy chạy server

            // ── Event handlers ──────────────────────────────────
            _proxy.BeforeRequest += OnBeforeRequestAsync;
            _proxy.ServerCertificateValidationCallback += OnCertValidation;

            // ── Endpoint: HTTP + HTTPS (decryptSsl = true = MITM) ──
            var endpoint = new ExplicitProxyEndPoint(IPAddress.Any, port, decryptSsl: true);

            // TRƯỚC khi tạo HTTPS tunnel → check domain ở đây (CONNECT phase)
            // Nếu block ngay lúc này = không cần decrypt SSL (nhanh hơn)
            endpoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnectAsync;

            _proxy.AddEndPoint(endpoint);
            _proxy.Start();

            _logger.LogInformation("✅ Titanium Proxy started on port {Port} (HTTP + HTTPS MITM)", port);

            // Chờ cho đến khi app shutdown
            await Task.Delay(Timeout.Infinite, stoppingToken);

            // Cleanup
            _proxy.BeforeRequest -= OnBeforeRequestAsync;
            endpoint.BeforeTunnelConnectRequest -= OnBeforeTunnelConnectAsync;
            _proxy.Stop();
            _logger.LogInformation("Proxy stopped.");
        }

        // ── Xử lý HTTPS CONNECT (trước khi decrypt SSL) ────────────
        // Nếu domain bị block → dừng ngay, không cần decrypt
        private async Task OnBeforeTunnelConnectAsync(object sender, TunnelConnectSessionEventArgs e)
        {
            var hostname = e.HttpClient.Request.RequestUri.Host;
            using var scope = _scopeFactory.CreateScope();
            var checker = scope.ServiceProvider.GetRequiredService<IProxyAccessChecker>();

            var clientIp = e.ClientRemoteEndPoint.Address.ToString();
            var result = await checker.CheckAsync(clientIp, hostname);

            if (result.Decision == AccessDecision.Blocked)
            {
                // Dừng CONNECT tunnel → browser nhận lỗi kết nối
                e.DenyConnect = true;
                _logger.LogDebug("CONNECT blocked: {Host} for IP {IP} — {Reason}",
                    hostname, clientIp, result.Reason);

                // Ghi log bị chặn
                _ = LogAccessAsync(result.ChildId, hostname, null,
                    AccessResult.Blocked, null, scope.ServiceProvider);
            }
            else
            {
                // Cho phép decrypt SSL để theo dõi từng request bên trong
                e.DecryptSsl = (result.Decision == AccessDecision.Allowed);
            }
        }

        // ── Xử lý mọi HTTP/HTTPS request (sau khi decrypt) ─────────
        private async Task OnBeforeRequestAsync(object sender, SessionEventArgs e)
        {
            var request  = e.HttpClient.Request;
            var hostname = request.RequestUri.Host.ToLower();

            // Bỏ qua request nội bộ của proxy (cert download...)
            if (hostname == "localhost" || hostname == "127.0.0.1") return;

            using var scope = _scopeFactory.CreateScope();
            var checker = scope.ServiceProvider.GetRequiredService<IProxyAccessChecker>();
            var tracker = scope.ServiceProvider.GetRequiredService<ISessionTracker>();

            var clientIp = e.ClientRemoteEndPoint.Address.ToString();
            var result   = await checker.CheckAsync(clientIp, hostname);

            if (result.Decision == AccessDecision.Blocked)
            {
                // Trả về trang block HTML
                var html = BuildBlockPage(hostname, result.Reason);
                e.Ok(html, new Dictionary<string, HttpHeader>
                {
                    { "Content-Type", new HttpHeader("Content-Type", "text/html; charset=utf-8") }
                });

                // Ghi log bị chặn
                _ = LogAccessAsync(result.ChildId, hostname, request.Url,
                    AccessResult.Blocked, null, scope.ServiceProvider);

                _logger.LogDebug("HTTP blocked: {Url} — {Reason}", request.Url, result.Reason);
            }
            else if (result.Decision == AccessDecision.Allowed && result.ChildId.HasValue)
            {
                // Ghi nhận session (tracking thời gian)
                await tracker.RecordRequestAsync(
                    result.ChildId.Value,
                    result.AllowedWebsiteId!.Value,
                    hostname,
                    clientIp);

                // Ghi log được phép
                _ = LogAccessAsync(result.ChildId, hostname, request.Url,
                    AccessResult.Allowed, result.AllowedWebsiteId, scope.ServiceProvider);
            }
        }

        // Bỏ qua lỗi certificate của server (tránh proxy crash khi server có cert tự ký)
        private Task OnCertValidation(object sender, CertificateValidationEventArgs e)
        {
            e.IsValid = true;
            return Task.CompletedTask;
        }

        private async Task LogAccessAsync(int? childId, string domain, string? url,
            AccessResult result, int? websiteId, IServiceProvider services)
        {
            if (childId == null) return;
            try
            {
                var db = services.GetRequiredService<AppDbContext>();
                db.WebAccessLogs.Add(new WebAccessLog
                {
                    ChildId          = childId.Value,
                    Domain           = domain,
                    FullUrl          = url?.Length > 2000 ? url[..2000] : url,
                    AccessResult     = result,
                    AllowedWebsiteId = websiteId,
                    SessionStart     = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log access for {Domain}", domain);
            }
        }

        private static string BuildBlockPage(string domain, string reason) => $"""
            <!DOCTYPE html>
            <html lang="vi">
            <head>
              <meta charset="UTF-8">
              <meta name="viewport" content="width=device-width, initial-scale=1.0">
              <title>Trang web bị chặn</title>
              <style>
                *{{margin:0;padding:0;box-sizing:border-box}}
                body{{
                  font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;
                  min-height:100vh;display:flex;align-items:center;justify-content:center;
                  background:linear-gradient(135deg,#667eea,#764ba2);
                }}
                .card{{
                  background:white;padding:48px 40px;border-radius:24px;
                  box-shadow:0 25px 60px rgba(0,0,0,0.3);text-align:center;
                  max-width:460px;width:90%;
                }}
                .shield{{font-size:72px;margin-bottom:16px;display:block}}
                h1{{font-size:22px;font-weight:700;color:#1a202c;margin-bottom:8px}}
                .domain{{
                  display:inline-block;background:#f0f4ff;color:#5a67d8;
                  padding:6px 18px;border-radius:20px;font-weight:600;font-size:16px;margin:12px 0;
                }}
                .reason{{
                  background:#fff5f5;border-left:4px solid #fc8181;
                  color:#c53030;padding:10px 14px;border-radius:8px;
                  font-size:13px;margin:12px 0;text-align:left;
                }}
                p{{color:#718096;font-size:14px;line-height:1.6;margin-top:12px}}
              </style>
            </head>
            <body>
              <div class="card">
                <span class="shield">🛡️</span>
                <h1>Trang web bị chặn</h1>
                <div class="domain">{domain}</div>
                <div class="reason">📋 {reason}</div>
                <p>Trang này không nằm trong danh sách cho phép.<br>
                Liên hệ bố/mẹ nếu bạn cần truy cập trang này.</p>
              </div>
            </body>
            </html>
            """;
    }
}//
---

## E. JOB MỚI: Jobs/CloseIdleSessionsJob.cs

```csharp
[DisallowConcurrentExecution]
public class CloseIdleSessionsJob : IJob
{
    private readonly ISessionTracker _tracker;
    public CloseIdleSessionsJob(ISessionTracker tracker) => _tracker = tracker;

    public async Task Execute(IJobExecutionContext context)
        => await _tracker.CloseIdleSessionsAsync(context.CancellationToken);
}
```

---

## F. ĐĂNG KÝ TRONG Program.cs

Thêm vào phần Services:

```csharp
// Proxy services
builder.Services.AddScoped<IProxyAccessChecker, ProxyAccessChecker>();
builder.Services.AddScoped<ISessionTracker, SessionTracker>();

// THAY: builder.Services.AddHostedService<ProxyServer>();
// BẰNG:
builder.Services.AddHostedService<FamilyProxyServer>();
```

Thêm Quartz job mới (cạnh SendScheduledNotificationsJob):

```csharp
builder.Services.AddQuartz(q =>
{
    // Job cũ
    var notifKey = new JobKey("SendScheduledNotificationsJob");
    q.AddJob<SendScheduledNotificationsJob>(opts => opts.WithIdentity(notifKey));
    q.AddTrigger(opts => opts.ForJob(notifKey)
        .WithSimpleSchedule(s => s.WithIntervalInMinutes(1).RepeatForever()));

    // Job mới: đóng session idle
    var sessionKey = new JobKey("CloseIdleSessionsJob");
    q.AddJob<CloseIdleSessionsJob>(opts => opts.WithIdentity(sessionKey));
    q.AddTrigger(opts => opts.ForJob(sessionKey)
        .WithSimpleSchedule(s => s.WithIntervalInMinutes(1).RepeatForever()));
});
```

---

## G. API ENDPOINTS MỚI CẦN THÊM VÀO AccessLogsController

Thêm 2 endpoint mới vào `AccessLogsController.cs`:

### GET `/api/children/{childId}/logs/sessions`
Lịch sử các phiên truy cập (web_sessions).

```csharp
[HttpGet("{childId}/logs/sessions")]
public async Task<IActionResult> GetSessions(
    int childId,
    [FromQuery] DateTime? fromDate,
    [FromQuery] DateTime? toDate,
    [FromQuery] int page = 1,
    [FromQuery] int pageSize = 20)
{
    // Verify guardian có quyền
    var guardianId = GetGuardianId();
    if (!await _childService.HasAccessAsync(guardianId, childId))
        return Forbid();

    var from = (fromDate ?? DateTime.Today.AddDays(-7)).Date;
    var to   = (toDate ?? DateTime.Today).Date;

    var sessions = await _db.WebSessions
        .Include(s => s.Website)
        .Where(s => s.ChildId == childId
                 && s.StartedAt.Date >= from
                 && s.StartedAt.Date <= to)
        .OrderByDescending(s => s.StartedAt)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .Select(s => new SessionDto
        {
            Id              = s.Id,
            Domain          = s.Domain,
            DisplayName     = s.Website.DisplayName,
            FaviconUrl      = s.Website.FaviconUrl,
            StartedAt       = s.StartedAt,
            EndedAt         = s.EndedAt,
            DurationSeconds = s.DurationSeconds,
            IsActive        = s.EndedAt == null,
        })
        .ToListAsync();

    var total = await _db.WebSessions
        .CountAsync(s => s.ChildId == childId
                      && s.StartedAt.Date >= from
                      && s.StartedAt.Date <= to);

    return Ok(new { items = sessions, totalCount = total, page, pageSize });
}
```

### GET `/api/children/{childId}/logs/summary?days=7`
Tổng kết nhiều ngày (cho chart).

```csharp
[HttpGet("{childId}/logs/summary")]
public async Task<IActionResult> GetSummary(int childId, [FromQuery] int days = 7)
{
    var guardianId = GetGuardianId();
    if (!await _childService.HasAccessAsync(guardianId, childId))
        return Forbid();

    var from = DateOnly.FromDateTime(DateTime.Today.AddDays(-days + 1));

    // Tổng theo domain
    var byDomain = await _db.DailyUsageStats
        .Include(d => d.Website)
        .Where(d => d.ChildId == childId && d.UsageDate >= from)
        .GroupBy(d => new { d.Domain, d.Website.DisplayName, d.Website.FaviconUrl, d.Website.TimeLimitMinutes })
        .Select(g => new
        {
            domain          = g.Key.Domain,
            displayName     = g.Key.DisplayName,
            faviconUrl      = g.Key.FaviconUrl,
            timeLimitMinutes = g.Key.TimeLimitMinutes,
            totalSeconds    = g.Sum(x => x.TotalSeconds),
            totalRequests   = g.Sum(x => x.RequestCount),
        })
        .OrderByDescending(x => x.totalSeconds)
        .ToListAsync();

    // Breakdown theo ngày
    var byDay = await _db.DailyUsageStats
        .Where(d => d.ChildId == childId && d.UsageDate >= from)
        .GroupBy(d => d.UsageDate)
        .Select(g => new
        {
            date         = g.Key,
            totalSeconds = g.Sum(x => x.TotalSeconds),
        })
        .OrderBy(x => x.date)
        .ToListAsync();

    return Ok(new { byDomain, byDay, days });
}
```

---

## H. DTO MỚI CẦN THÊM

```csharp
// Models/DTOs/Logs/SessionDto.cs
public class SessionDto
{
    public long Id { get; set; }
    public string Domain { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string? FaviconUrl { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int DurationSeconds { get; set; }
    public bool IsActive { get; set; }
}
```

---

## I. HƯỚNG DẪN CÀI ROOT CA TRÊN MÁY CLIENT (Con)

### Tại sao cần cài Root CA?
Titanium Web Proxy dùng MITM SSL → tạo certificate giả cho mỗi domain.
Browser sẽ báo "Not Secure" nếu không trust Root CA.

### Cách xuất Root CA từ server:

```csharp
// Thêm endpoint trong AdminController (hoặc chạy 1 lần):
[HttpGet("proxy-root-cert")]
[AllowAnonymous]
public async Task<IActionResult> DownloadRootCert()
{
    // Titanium lưu cert trong %APPDATA%\Titanium\rootCert.pfx
    var certPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Titanium", "rootCert.pfx");

    if (!System.IO.File.Exists(certPath))
        return NotFound("Root cert chưa được tạo. Khởi động proxy trước.");

    var bytes = await System.IO.File.ReadAllBytesAsync(certPath);
    return File(bytes, "application/x-pkcs12", "FamilyGuardian-RootCA.pfx");
}
```

### Cài trên máy Windows con:
1. Download `FamilyGuardian-RootCA.pfx` từ server.
2. Double-click → Install Certificate.
3. Chọn **"Local Machine"** → Next.
4. Chọn **"Place all certificates in the following store"** → Browse → **"Trusted Root Certification Authorities"**.
5. Finish → OK.
6. Cấu hình Proxy: Settings → Network → Proxy → `{server_ip}:8888`.

### Cài trên máy macOS con:
```bash
# Terminal:
sudo security add-trusted-cert -d -r trustRoot \
  -k /Library/Keychains/System.keychain FamilyGuardian-RootCA.pfx
```

---

## J. GIẢI THÍCH TẠI SAO PROXY CŨ KHÔNG HOẠT ĐỘNG

| Vấn đề | Chi tiết |
|---|---|
| **TcpListener thủ công** | Không có event-driven API → khó intercept đúng flow HTTP/HTTPS |
| **HTTPS CONNECT tunnel** | Proxy cũ forward thẳng TCP bytes → không thấy domain bên trong SSL |
| **Không có MITM** | Không tạo được cert giả → không decrypt HTTPS → không check URL |
| **Titanium Web Proxy** | Có sẵn MITM, cert manager, event hooks → chỉ cần xử lý business logic |
| **QUIC/HTTP3 bypass** | Cả 2 cách đều không chặn được UDP/QUIC → cần tắt QUIC bằng group policy hoặc firewall |

---

## K. CHECKLIST THÊM VÀO

- [ ] Thêm NuGet: `Titanium.Web.Proxy`
- [ ] Tạo `Models/Entities/WebSession.cs`
- [ ] Thêm `DbSet<WebSession>` + config vào `AppDbContext.cs`
- [ ] Chạy `DB_ADDITIONS.sql` (tạo bảng web_sessions + stored procedures)
- [ ] Tạo `Proxy/ProxyAccessChecker.cs` (copy từ file cung cấp)
- [ ] Tạo `Proxy/SessionTracker.cs` (copy từ file cung cấp)
- [ ] Thay `Proxy/ProxyServer.cs` bằng `FamilyProxyServer` dùng Titanium
- [ ] Tạo `Jobs/CloseIdleSessionsJob.cs`
- [ ] Đăng ký services + job trong `Program.cs`
- [ ] Thêm 2 endpoint mới vào `AccessLogsController`
- [ ] Thêm `SessionDto`
- [ ] Thêm endpoint download root cert (để cài trên máy con)
- [ ] Test: cài root CA trên máy con → cấu hình proxy → thử truy cập domain bị chặn