# BACKEND - Family Guardian API
## ASP.NET Core 8.0 Web API + MySQL

---

## 1. LUỒNG CHỨC NĂNG CHÍNH

```
[1] Bố mẹ mở app → Đăng nhập Google
         ↓ (lấy avatar + tên từ Google)
[2] Vào Dashboard → Thấy danh sách con (nếu có)
         ↓
[3] Bố mẹ thêm con:
    - Nhấn "Đăng ký Gmail cho con" → redirect accounts.google.com
    - Nhấn "Con đã có Gmail" → Google Login popup → link tài khoản con vào hệ thống
         ↓
[4] Nhấn vào tên con → Trang quản lý con
    - Thêm web được phép (nhập domain → kiểm tra → thêm vào danh sách)
    - Đặt time limit cho từng web
    - Xem thời gian con dùng từng web
         ↓
[5] Proxy (port 8888) chặn tất cả web
    → Chỉ cho phép domain có trong allowed_websites của child đó
    → Ghi log mọi request
    → Cập nhật daily_usage_stats
```

---

## 2. QUY TẮC CHẶN WEB

- **Mặc định: chặn tất cả.** Chỉ những domain bố mẹ thêm vào `allowed_websites` mới được truy cập.
- Subdomain cũng được phép nếu domain cha được phép (vd: `m.youtube.com` được phép nếu `youtube.com` được phép).
- Kiểm tra thêm: khung giờ, giới hạn thời gian/ngày.
- Proxy chạy song song trong ASP.NET Core process, không cần extension.

---

## 3. CÔNG NGHỆ & NUGET PACKAGES

```xml
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="8.*" />
<PackageReference Include="Pomelo.EntityFrameworkCore.MySql" Version="8.*" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Tools" Version="8.*" />
<PackageReference Include="AutoMapper.Extensions.Microsoft.DependencyInjection" Version="12.*" />
<PackageReference Include="FluentValidation.AspNetCore" Version="11.*" />
<PackageReference Include="Serilog.AspNetCore" Version="8.*" />
<PackageReference Include="Serilog.Sinks.Console" Version="5.*" />
<PackageReference Include="Serilog.Sinks.File" Version="5.*" />
<PackageReference Include="Swashbuckle.AspNetCore" Version="6.*" />
<PackageReference Include="Google.Apis.Auth" Version="1.*" />
<PackageReference Include="Quartz.Extensions.Hosting" Version="3.*" />
<PackageReference Include="Microsoft.AspNetCore.SignalR" Version="8.*" />
```

---

## 4. CẤU TRÚC THƯ MỤC

```
FamilyGuardian.API/
├── Controllers/
│   ├── AuthController.cs
│   ├── ChildrenController.cs
│   ├── AllowedWebsitesController.cs
│   ├── AccessLogsController.cs
│   ├── WebsiteCheckController.cs
│   ├── OnlineStatusController.cs
│   ├── NotificationsController.cs
│   └── AdminController.cs
├── Data/
│   └── AppDbContext.cs
├── Models/
│   ├── Entities/
│   │   ├── User.cs
│   │   ├── RefreshToken.cs
│   │   ├── GuardianChildRelationship.cs
│   │   ├── AllowedWebsite.cs
│   │   ├── WebAccessLog.cs
│   │   ├── DailyUsageStat.cs
│   │   ├── UserOnlineStatus.cs
│   │   ├── WebsiteCheckCache.cs
│   │   ├── Notification.cs
│   │   ├── SystemSetting.cs
│   │   └── ProxyIpMapping.cs
│   └── DTOs/
│       ├── Auth/
│       │   ├── GoogleLoginRequest.cs
│       │   ├── LinkChildRequest.cs
│       │   ├── AuthResponse.cs
│       │   └── UserDto.cs
│       ├── Children/
│       │   ├── ChildDto.cs
│       │   └── ChildDetailDto.cs
│       ├── AllowedWebsites/
│       │   ├── AllowedWebsiteDto.cs
│       │   ├── AddWebsiteRequest.cs
│       │   └── UpdateWebsiteRequest.cs
│       ├── Logs/
│       │   ├── AccessLogDto.cs
│       │   ├── DailyUsageDto.cs
│       │   └── UsageHistoryDto.cs
│       ├── WebsiteCheck/
│       │   └── WebsiteCheckResult.cs
│       └── Notifications/
│           ├── NotificationDto.cs
│           └── CreateNotificationRequest.cs
├── Services/
│   ├── Interfaces/
│   │   ├── IAuthService.cs
│   │   ├── IChildService.cs
│   │   ├── IAllowedWebsiteService.cs
│   │   ├── IAccessLogService.cs
│   │   ├── IWebsiteCheckService.cs
│   │   ├── IOnlineStatusService.cs
│   │   ├── INotificationService.cs
│   │   └── IJwtService.cs
│   ├── AuthService.cs
│   ├── ChildService.cs
│   ├── AllowedWebsiteService.cs
│   ├── AccessLogService.cs
│   ├── WebsiteCheckService.cs
│   ├── OnlineStatusService.cs
│   ├── NotificationService.cs
│   └── JwtService.cs
├── Proxy/
│   ├── ProxyServer.cs
│   ├── ProxyConnectionHandler.cs
│   └── DomainMatcher.cs
├── Hubs/
│   └── NotificationHub.cs
├── Jobs/
│   └── SendScheduledNotificationsJob.cs
├── Middleware/
│   ├── ExceptionMiddleware.cs
│   └── RequestLoggingMiddleware.cs
├── Helpers/
│   └── DomainNormalizer.cs
├── appsettings.json
└── Program.cs
```

---

## 5. ENTITIES (Models/Entities/)

### User.cs
```csharp
public class User
{
    public int Id { get; set; }
    public string? GoogleId { get; set; }        // từ Google OAuth payload.Subject
    public string Email { get; set; } = null!;
    public string FullName { get; set; } = null!;
    public string? AvatarUrl { get; set; }       // từ Google payload.Picture
    public UserRole Role { get; set; } = UserRole.Guardian;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public UserOnlineStatus? OnlineStatus { get; set; }
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
    public ICollection<GuardianChildRelationship> AsGuardian { get; set; } = [];
    public ICollection<GuardianChildRelationship> AsChild { get; set; } = [];
    public ICollection<AllowedWebsite> AllowedWebsites { get; set; } = [];
    public ICollection<WebAccessLog> AccessLogs { get; set; } = [];
    public ICollection<Notification> SentNotifications { get; set; } = [];
    public ICollection<Notification> ReceivedNotifications { get; set; } = [];
}
public enum UserRole { Admin, Guardian, Child }
```

### AllowedWebsite.cs
```csharp
public class AllowedWebsite
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public string Domain { get; set; } = null!;          // youtube.com (normalized)
    public string? DisplayName { get; set; }              // YouTube
    public string? FaviconUrl { get; set; }               // https://www.google.com/s2/favicons?domain=youtube.com
    public bool IsActive { get; set; } = true;
    public int? TimeLimitMinutes { get; set; }            // null = không giới hạn
    public TimeOnly? AllowedStartTime { get; set; }       // 07:00
    public TimeOnly? AllowedEndTime { get; set; }         // 21:00
    // Kết quả kiểm tra
    public bool IsVerified { get; set; } = false;
    public bool? IsSafe { get; set; }
    public int? HttpStatusCode { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    // Meta
    public int AddedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public User Child { get; set; } = null!;
    public User Guardian { get; set; } = null!;
    public ICollection<DailyUsageStat> DailyStats { get; set; } = [];
    public ICollection<WebAccessLog> AccessLogs { get; set; } = [];
}
```

### DailyUsageStat.cs
```csharp
public class DailyUsageStat
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public int AllowedWebsiteId { get; set; }
    public string Domain { get; set; } = null!;
    public DateOnly UsageDate { get; set; }
    public int TotalSeconds { get; set; } = 0;
    public int RequestCount { get; set; } = 0;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    public User Child { get; set; } = null!;
    public AllowedWebsite Website { get; set; } = null!;
}
```

### WebAccessLog.cs
```csharp
public class WebAccessLog
{
    public long Id { get; set; }
    public int ChildId { get; set; }
    public string Domain { get; set; } = null!;
    public string? FullUrl { get; set; }
    public AccessResult AccessResult { get; set; }
    public int? AllowedWebsiteId { get; set; }
    public DateTime SessionStart { get; set; } = DateTime.UtcNow;
    public DateTime? SessionEnd { get; set; }
    public int DurationSeconds { get; set; } = 0;

    public User Child { get; set; } = null!;
    public AllowedWebsite? Website { get; set; }
}
public enum AccessResult { Allowed, Blocked }
```

### WebsiteCheckCache.cs
```csharp
public class WebsiteCheckCache
{
    public string Domain { get; set; } = null!;
    public bool? IsReachable { get; set; }
    public int? HttpStatusCode { get; set; }
    public int? ResponseTimeMs { get; set; }
    public bool? IsSafe { get; set; }
    public string? ThreatType { get; set; }
    public string? FaviconUrl { get; set; }
    public string? DisplayName { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
}
```

### ProxyIpMapping.cs
```csharp
public class ProxyIpMapping
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public string IpAddress { get; set; } = null!;
    public string? DeviceName { get; set; }
    public bool IsActive { get; set; } = true;
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User Child { get; set; } = null!;
    public User Guardian { get; set; } = null!;
}
```

---

## 6. APPDBCONTEXT

```csharp
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

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
    public DbSet<ProxyIpMapping> ProxyIpMappings => Set<ProxyIpMapping>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<User>().Property(u => u.Role).HasConversion<string>();
        mb.Entity<WebAccessLog>().Property(l => l.AccessResult).HasConversion<string>();
        mb.Entity<Notification>().Property(n => n.Type).HasConversion<string>();

        mb.Entity<GuardianChildRelationship>()
            .HasIndex(r => new { r.GuardianId, r.ChildId }).IsUnique();

        mb.Entity<AllowedWebsite>()
            .HasIndex(w => new { w.ChildId, w.Domain }).IsUnique();

        mb.Entity<DailyUsageStat>()
            .HasIndex(d => new { d.ChildId, d.AllowedWebsiteId, d.UsageDate }).IsUnique();

        mb.Entity<WebsiteCheckCache>().HasKey(c => c.Domain);

        mb.Entity<UserOnlineStatus>().HasKey(o => o.UserId);
        mb.Entity<UserOnlineStatus>()
            .HasOne(o => o.User).WithOne(u => u.OnlineStatus)
            .HasForeignKey<UserOnlineStatus>(o => o.UserId);

        // Guardian → children
        mb.Entity<GuardianChildRelationship>()
            .HasOne(r => r.Guardian).WithMany(u => u.AsGuardian)
            .HasForeignKey(r => r.GuardianId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<GuardianChildRelationship>()
            .HasOne(r => r.Child).WithMany(u => u.AsChild)
            .HasForeignKey(r => r.ChildId).OnDelete(DeleteBehavior.Cascade);

        // AllowedWebsite → child
        mb.Entity<AllowedWebsite>()
            .HasOne(w => w.Child).WithMany(u => u.AllowedWebsites)
            .HasForeignKey(w => w.ChildId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<AllowedWebsite>()
            .HasOne(w => w.Guardian).WithMany()
            .HasForeignKey(w => w.AddedBy).OnDelete(DeleteBehavior.Restrict);
    }
}
```

---

## 7. CONTROLLERS CHI TIẾT

---

### 7.1 AuthController — `/api/auth`

#### POST `/api/auth/google-login`
**Mục đích:** Bố mẹ đăng nhập hệ thống bằng tài khoản Google của họ.

**[AllowAnonymous]**

**Request:** `{ "idToken": "eyJ..." }`

**Logic chi tiết:**
```
1. Validate idToken:
   var settings = new GoogleJsonWebSignature.ValidationSettings {
       Audience = new[] { configuration["Google:ClientId"] }
   };
   var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);
   // Nếu invalid → throw UnauthorizedException

2. Lấy thông tin từ payload:
   - googleId = payload.Subject
   - email    = payload.Email
   - fullName = payload.Name
   - avatarUrl = payload.Picture  ← QUAN TRỌNG: lưu avatar URL

3. Tìm user theo googleId trong DB:
   var user = await db.Users.FirstOrDefaultAsync(u => u.GoogleId == googleId);

4. Nếu chưa tồn tại → tạo mới:
   user = new User {
       GoogleId  = googleId,
       Email     = email,
       FullName  = fullName,
       AvatarUrl = avatarUrl,  ← lưu avatar
       Role      = UserRole.Guardian
   };
   db.Users.Add(user);

5. Nếu đã tồn tại → cập nhật avatar (Google có thể đổi):
   user.FullName  = fullName;
   user.AvatarUrl = avatarUrl;  ← update mỗi lần login

6. Nếu user.Role == Child → return 403 Forbidden

7. Tạo JWT access token (1h) + refresh token
   Lưu refresh token vào bảng refresh_tokens

8. Upsert user_online_status: is_online = true

9. SaveChanges

10. Return 200 OK:
{
  "accessToken": "eyJ...",
  "refreshToken": "abc123",
  "user": {
    "id": 1,
    "email": "parent@gmail.com",
    "fullName": "Nguyen Van A",
    "avatarUrl": "https://lh3.googleusercontent.com/...",  ← trả về cho frontend hiển thị
    "role": "Guardian"
  }
}
```

#### POST `/api/auth/link-child-google`
**Mục đích:** Bố mẹ link tài khoản Google của con vào hệ thống.

**[Authorize(Roles = "Guardian,Admin")]**

**Request:** `{ "idToken": "eyJ..." }` — idToken của **con**

**Logic chi tiết:**
```
1. Validate idToken của con (cùng ClientId)
2. Lấy googleId, email, fullName, avatarUrl của con từ payload
3. Lấy guardianId từ JWT claims của bố mẹ đang đăng nhập
4. Tìm user theo googleId:
   - Chưa tồn tại → tạo User { Role = Child, ... }
   - Đã tồn tại, role != Child → return 400 "Đây không phải tài khoản trẻ em"
   - Đã tồn tại, role == Child → dùng user cũ
5. Kiểm tra quan hệ đã tồn tại chưa:
   var exists = await db.GuardianChildRelationships.AnyAsync(
       r => r.GuardianId == guardianId && r.ChildId == child.Id);
   if (exists) return 409 "Tài khoản con đã được liên kết"
6. Tạo GuardianChildRelationship { GuardianId, ChildId = child.Id }
7. SaveChanges
8. Return 201 với ChildDto
```

#### POST `/api/auth/refresh`
**[AllowAnonymous]**
- Request: `{ "refreshToken": "..." }`
- Tìm refresh_token trong DB chưa revoked, chưa hết hạn
- Revoke token cũ, tạo token mới (rotation)
- Return `{ accessToken, refreshToken }`

#### POST `/api/auth/logout`
**[Authorize]**
- Revoke tất cả refresh token của user
- Set is_online = false
- Return 200

---

### 7.2 ChildrenController — `/api/children`
**[Authorize(Roles = "Guardian,Admin")]**

#### GET `/api/children`
**Mục đích:** Lấy danh sách tất cả con của guardian đang đăng nhập.

**Logic:**
```
1. Lấy guardianId từ JWT
2. Query:
   SELECT u.*, os.is_online, os.last_seen_at,
          COUNT(DISTINCT aw.id) as active_websites_count,
          SUM(dus.total_seconds) as today_total_seconds
   FROM users u
   JOIN guardian_child_relationships gcr ON gcr.child_id = u.id
   LEFT JOIN user_online_status os ON os.user_id = u.id
   LEFT JOIN allowed_websites aw ON aw.child_id = u.id AND aw.is_active = true
   LEFT JOIN daily_usage_stats dus ON dus.child_id = u.id AND dus.usage_date = TODAY
   WHERE gcr.guardian_id = @guardianId AND u.is_active = true
   GROUP BY u.id

3. Return List<ChildDto>
```

**Response ChildDto:**
```json
{
  "id": 5,
  "fullName": "Nguyen An",
  "email": "an@gmail.com",
  "avatarUrl": "https://lh3.googleusercontent.com/...",
  "isOnline": true,
  "lastSeenAt": "2025-01-01T14:30:00Z",
  "activeWebsitesCount": 3,
  "todayTotalSeconds": 3600
}
```

#### GET `/api/children/{childId}`
**Mục đích:** Chi tiết 1 con (dùng cho trang quản lý).

**Logic:**
```
1. Verify guardian có quyền với childId:
   var hasAccess = await db.GuardianChildRelationships.AnyAsync(
       r => r.GuardianId == guardianId && r.ChildId == childId);
   if (!hasAccess) return 403

2. Load: User + OnlineStatus + AllowedWebsites (kèm today usage) + IP mappings
3. Return ChildDetailDto
```

**Response ChildDetailDto:**
```json
{
  "id": 5,
  "fullName": "Nguyen An",
  "email": "an@gmail.com",
  "avatarUrl": "...",
  "isOnline": true,
  "lastSeenAt": "...",
  "ipAddress": "192.168.1.100",
  "allowedWebsites": [ ... ],
  "proxyIpMappings": [ { "ip": "192.168.1.100", "deviceName": "Laptop con An" } ],
  "todayTotalSeconds": 3600
}
```

#### DELETE `/api/children/{childId}`
- Xóa quan hệ trong guardian_child_relationships
- Không xóa user account của con

#### POST `/api/children/{childId}/ip-mappings`
**Mục đích:** Bố mẹ cấu hình IP máy con để proxy xác định.
- Request: `{ "ipAddress": "192.168.1.100", "deviceName": "Laptop con An" }`
- Upsert vào proxy_ip_mappings

#### GET `/api/children/{childId}/ip-mappings`
- Lấy danh sách IP mappings của con

#### DELETE `/api/children/{childId}/ip-mappings/{id}`
- Xóa 1 IP mapping

---

### 7.3 AllowedWebsitesController — `/api/children/{childId}/websites`
**[Authorize(Roles = "Guardian,Admin")]** + verify guardian sở hữu childId

#### GET `/api/children/{childId}/websites`
**Mục đích:** Lấy danh sách web được phép của con kèm usage hôm nay.

**Logic:**
```
Gọi sp_GetChildAllowedWebsites(childId)
Trả List<AllowedWebsiteDto>
```

**Response AllowedWebsiteDto:**
```json
{
  "id": 1,
  "domain": "youtube.com",
  "displayName": "YouTube",
  "faviconUrl": "https://www.google.com/s2/favicons?domain=youtube.com&sz=64",
  "isActive": true,
  "timeLimitMinutes": 60,
  "allowedStartTime": "07:00",
  "allowedEndTime": "21:00",
  "isVerified": true,
  "isSafe": true,
  "httpStatusCode": 200,
  "lastCheckedAt": "2025-01-01T10:00:00Z",
  "todaySeconds": 3240,
  "todayRequests": 15,
  "limitExceeded": false
}
```

#### POST `/api/children/{childId}/websites`
**Mục đích:** Thêm 1 web mới vào danh sách cho phép.

**Request:**
```json
{
  "domain": "youtube.com",
  "timeLimitMinutes": 60,
  "allowedStartTime": "07:00",
  "allowedEndTime": "21:00"
}
```

**Logic:**
```
1. Normalize domain:
   "https://www.YouTube.com/watch?v=abc" → "youtube.com"
   Dùng DomainNormalizer.Normalize(input)

2. Kiểm tra domain chưa được thêm cho child này
   (unique constraint child_id + domain)

3. Gọi WebsiteCheckService.CheckAsync(domain) để lấy thông tin:
   - isReachable, httpStatusCode, responseTimeMs
   - isSafe (Google Safe Browsing)
   - faviconUrl = "https://www.google.com/s2/favicons?domain={domain}&sz=64"
   - displayName (từ title của trang nếu lấy được)

4. Tạo AllowedWebsite:
   {
     ChildId         = childId,
     Domain          = normalizedDomain,
     DisplayName     = checkResult.DisplayName ?? domain,
     FaviconUrl      = checkResult.FaviconUrl,
     TimeLimitMinutes = request.TimeLimitMinutes,
     AllowedStartTime = request.AllowedStartTime,
     AllowedEndTime   = request.AllowedEndTime,
     IsVerified      = checkResult.IsReachable,
     IsSafe          = checkResult.IsSafe,
     HttpStatusCode  = checkResult.HttpStatusCode,
     LastCheckedAt   = DateTime.UtcNow,
     AddedBy         = guardianId
   }

5. SaveChanges
6. Return 201 với AllowedWebsiteDto
```

**Validation (FluentValidation):**
```csharp
RuleFor(x => x.Domain).NotEmpty().MaximumLength(255)
    .Must(d => DomainNormalizer.IsValidDomain(d))
    .WithMessage("Domain không hợp lệ");
RuleFor(x => x.TimeLimitMinutes).GreaterThan(0).LessThanOrEqualTo(1440)
    .When(x => x.TimeLimitMinutes.HasValue);
RuleFor(x => x.AllowedEndTime).GreaterThan(x => x.AllowedStartTime)
    .When(x => x.AllowedStartTime.HasValue && x.AllowedEndTime.HasValue);
```

#### PUT `/api/children/{childId}/websites/{websiteId}`
**Mục đích:** Sửa thông tin web (time limit, khung giờ).

**Request:**
```json
{
  "timeLimitMinutes": 90,
  "allowedStartTime": "08:00",
  "allowedEndTime": "22:00",
  "isActive": true
}
```

#### DELETE `/api/children/{childId}/websites/{websiteId}`
- Xóa website khỏi danh sách cho phép của con
- Cascade xóa daily_usage_stats liên quan

#### PATCH `/api/children/{childId}/websites/{websiteId}/toggle`
- Toggle is_active (bật/tắt tạm thời không xóa)
- Return `{ "isActive": true/false }`

#### POST `/api/children/{childId}/websites/{websiteId}/recheck`
**Mục đích:** Kiểm tra lại website (force refresh cache).
- Gọi lại WebsiteCheckService.CheckAsync(domain, forceRefresh: true)
- Cập nhật isVerified, isSafe, httpStatusCode, lastCheckedAt trong AllowedWebsite
- Return WebsiteCheckResult

---

### 7.4 WebsiteCheckController — `/api/website-check`
**[Authorize(Roles = "Guardian,Admin")]**

#### GET `/api/website-check?domain=youtube.com`
**Mục đích:** Kiểm tra website có tồn tại và an toàn không (TRƯỚC khi thêm vào danh sách).

**Logic chi tiết:**
```
1. Normalize domain từ query string

2. Kiểm tra cache trong website_check_cache:
   - Nếu có VÀ expires_at > NOW() → return cached result

3. Gửi HEAD request (rồi GET nếu HEAD fail):
   using var client = httpClientFactory.CreateClient("WebCheck");
   try {
       var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head,
           $"https://{domain}"), ct);
       isReachable    = response.IsSuccessStatusCode || (int)response.StatusCode < 500;
       httpStatusCode = (int)response.StatusCode;
   } catch {
       // Thử HTTP
       try {
           var r2 = await client.SendAsync(...http...);
           ...
       } catch {
           isReachable = false;
       }
   }

4. Lấy favicon URL:
   faviconUrl = $"https://www.google.com/s2/favicons?domain={domain}&sz=64"

5. Gọi Google Safe Browsing API v4:
   POST https://safebrowsing.googleapis.com/v4/threatMatches:find?key={SafeBrowsingApiKey}
   Body: {
     "client": { "clientId": "familyguardian", "clientVersion": "1.0" },
     "threatInfo": {
       "threatTypes": ["MALWARE","SOCIAL_ENGINEERING","UNWANTED_SOFTWARE","POTENTIALLY_HARMFUL_APPLICATION"],
       "platformTypes": ["ANY_PLATFORM"],
       "threatEntryTypes": ["URL"],
       "threatEntries": [{ "url": "https://{domain}" }]
     }
   }
   → Nếu response.matches != null && matches.Count > 0:
       isSafe    = false
       threatType = matches[0].threatType  // "MALWARE", etc

6. Lưu vào cache:
   INSERT website_check_cache SET
     domain          = domain,
     is_reachable    = isReachable,
     http_status_code = httpStatusCode,
     response_time_ms = responseTime,
     is_safe         = isSafe,
     threat_type     = threatType,
     favicon_url     = faviconUrl,
     expires_at      = NOW() + INTERVAL 30 MINUTE

7. Return:
{
  "domain": "youtube.com",
  "isReachable": true,
  "httpStatusCode": 200,
  "responseTimeMs": 234,
  "isSafe": true,
  "threatType": null,
  "faviconUrl": "https://www.google.com/s2/favicons?domain=youtube.com&sz=64",
  "checkedAt": "2025-01-01T10:00:00Z"
}
```

---

### 7.5 AccessLogsController — `/api/children/{childId}/logs`
**[Authorize(Roles = "Guardian,Admin")]**

#### GET `/api/children/{childId}/logs`
**Mục đích:** Lịch sử truy cập web của con.

**Query params:**
- `fromDate` (default 7 ngày trước)
- `toDate` (default hôm nay)
- `page` (default 1)
- `pageSize` (default 20, max 100)
- `domain` (filter optional)
- `result` (optional: "allowed" / "blocked")

**Response:**
```json
{
  "items": [
    {
      "id": 1,
      "domain": "youtube.com",
      "displayName": "YouTube",
      "faviconUrl": "...",
      "fullUrl": "https://youtube.com/watch?v=abc",
      "accessResult": "allowed",
      "durationSeconds": 300,
      "sessionStart": "2025-01-01T14:30:00Z",
      "sessionEnd": "2025-01-01T14:35:00Z"
    }
  ],
  "totalCount": 150,
  "page": 1,
  "pageSize": 20
}
```

#### GET `/api/children/{childId}/logs/daily-usage`
**Mục đích:** Thống kê thời gian dùng web theo ngày — dùng để bố mẹ theo dõi.

**Query params:** `date` (default hôm nay)

**Response:**
```json
[
  {
    "domain": "youtube.com",
    "displayName": "YouTube",
    "faviconUrl": "...",
    "totalSeconds": 3240,
    "requestCount": 15,
    "timeLimitMinutes": 60,
    "limitExceeded": false,
    "usagePercent": 90.0,
    "usageDate": "2025-01-01"
  },
  {
    "domain": "google.com",
    "displayName": "Google",
    "faviconUrl": "...",
    "totalSeconds": 600,
    "requestCount": 8,
    "timeLimitMinutes": null,
    "limitExceeded": false,
    "usagePercent": null,
    "usageDate": "2025-01-01"
  }
]
```

#### GET `/api/children/{childId}/logs/history`
**Mục đích:** Lịch sử usage nhiều ngày (7, 14, 30 ngày).

**Query:** `fromDate`, `toDate`

**Response:** Mảng DailyUsageDto theo từng ngày từng domain.

---

### 7.6 OnlineStatusController — `/api/online-status`

#### POST `/api/online-status/heartbeat`
**[Authorize]**
- Frontend gọi mỗi 30 giây
- Upsert user_online_status: is_online = true, last_seen_at = now
- Lấy IP từ HttpContext.Connection.RemoteIpAddress
- Nếu là child → push SignalR "ChildStatusChanged" về guardian
- Return 200

#### GET `/api/online-status/{userId}`
**[Authorize]**
- Return `{ isOnline, lastSeenAt, ipAddress }`

---

### 7.7 NotificationsController — `/api/notifications`

#### POST `/api/notifications`
**[Authorize(Roles = "Guardian,Admin")]**
- Request: `{ childId, title, message, type, scheduledAt }`
- Verify guardian có quyền với childId
- Tạo Notification record
- Nếu scheduledAt null → push SignalR ngay tới child: `"ReceiveNotification"`
- Return 201

#### GET `/api/notifications/child/{childId}`
**[Authorize(Roles = "Guardian,Admin")]**
- Lấy danh sách thông báo đã gửi cho childId

#### GET `/api/notifications/my`
**[Authorize(Roles = "Child")]**
- Child xem thông báo của mình

#### PATCH `/api/notifications/{id}/read`
**[Authorize]**
- Đánh dấu đã đọc

---

## 8. PROXY SERVER — CHẶN WEB (Proxy/)

### Nguyên tắc
- **Chặn tất cả mặc định.** Chỉ cho qua nếu domain có trong `allowed_websites` của child đó.
- Chạy như `BackgroundService` trong cùng ASP.NET Core process, port **8888**.
- Máy con cấu hình: Network Settings → Proxy → `{server_ip}:8888`.
- Xác định child từ IP address của kết nối (tra bảng `proxy_ip_mappings`).

### ProxyServer.cs (BackgroundService)
```csharp
public class ProxyServer : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var port = _config.GetValue<int>("Proxy:Port", 8888);
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        _logger.LogInformation("Proxy server started on port {Port}", port);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct); // fire-and-forget
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Accept error"); }
        }
        listener.Stop();
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using var scope = _scopeFactory.CreateScope();
        {
            var handler = scope.ServiceProvider.GetRequiredService<ProxyConnectionHandler>();
            await handler.HandleAsync(client, ct);
        }
    }
}
```

### ProxyConnectionHandler.cs
```csharp
public class ProxyConnectionHandler
{
    private readonly AppDbContext _db;
    private readonly ILogger<ProxyConnectionHandler> _logger;

    public async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var (requestLine, headers) = await ReadHeadersAsync(stream, ct);
        if (string.IsNullOrEmpty(requestLine)) return;

        var parts     = requestLine.Split(' ');
        var method    = parts[0].ToUpper();
        var url       = parts.Length > 1 ? parts[1] : "";
        var clientIp  = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();

        // Xác định domain
        var domain = method == "CONNECT"
            ? url.Split(':')[0].ToLower()
            : headers.GetValueOrDefault("Host", "").Split(':')[0].ToLower();

        if (string.IsNullOrEmpty(domain)) return;

        // Xác định child từ IP
        var childId = await ResolveChildIdAsync(clientIp);

        // Kiểm tra quyền truy cập
        var (decision, websiteId, reason) = await CheckAccessAsync(childId, domain);

        // Ghi log (async, không block)
        _ = LogAccessAsync(childId, domain, url, decision, websiteId, ct);

        if (method == "CONNECT")
        {
            if (decision == "allowed")
            {
                // Tunnel HTTPS
                var responseBytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                await stream.WriteAsync(responseBytes, ct);
                using var remote = new TcpClient();
                var host = url.Split(':')[0];
                var port = int.Parse(url.Split(':').Length > 1 ? url.Split(':')[1] : "443");
                await remote.ConnectAsync(host, port, ct);
                var t1 = stream.CopyToAsync(remote.GetStream(), ct);
                var t2 = remote.GetStream().CopyToAsync(stream, ct);
                await Task.WhenAny(t1, t2);
            }
            else
            {
                // Block HTTPS
                var resp = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(resp, ct);
            }
        }
        else
        {
            if (decision == "allowed")
            {
                await ForwardHttpAsync(stream, method, url, headers, ct);
            }
            else
            {
                var html  = BuildBlockPage(domain, reason);
                var body  = Encoding.UTF8.GetBytes(html);
                var head  = $"HTTP/1.1 403 Forbidden\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
                await stream.WriteAsync(body, ct);
            }
        }
    }

    private async Task<(string decision, int? websiteId, string reason)> CheckAccessAsync(int? childId, string domain)
    {
        if (childId == null)
            return ("blocked", null, "Thiết bị chưa được cấu hình");

        // Tìm domain khớp (exact hoặc subdomain)
        var website = await _db.AllowedWebsites
            .Where(w => w.ChildId == childId && w.IsActive
                     && (w.Domain == domain || domain.EndsWith("." + w.Domain)))
            .OrderByDescending(w => w.Domain.Length)   // khớp cụ thể nhất
            .FirstOrDefaultAsync();

        if (website == null)
            return ("blocked", null, "Không có trong danh sách cho phép");

        // Kiểm tra khung giờ
        if (website.AllowedStartTime.HasValue && website.AllowedEndTime.HasValue)
        {
            var now = TimeOnly.FromDateTime(DateTime.Now);
            if (now < website.AllowedStartTime || now > website.AllowedEndTime)
                return ("blocked", website.Id,
                    $"Ngoài khung giờ ({website.AllowedStartTime} - {website.AllowedEndTime})");
        }

        // Kiểm tra giới hạn thời gian
        if (website.TimeLimitMinutes.HasValue)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var usage = await _db.DailyUsageStats
                .Where(d => d.ChildId == childId
                         && d.AllowedWebsiteId == website.Id
                         && d.UsageDate == today)
                .FirstOrDefaultAsync();

            if (usage != null && usage.TotalSeconds >= website.TimeLimitMinutes.Value * 60)
                return ("blocked", website.Id,
                    $"Đã hết {website.TimeLimitMinutes} phút cho phép hôm nay");
        }

        return ("allowed", website.Id, "");
    }

    private async Task<int?> ResolveChildIdAsync(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return null;
        var mapping = await _db.ProxyIpMappings
            .FirstOrDefaultAsync(m => m.IpAddress == ip && m.IsActive);
        return mapping?.ChildId;
    }

    private async Task LogAccessAsync(int? childId, string domain, string url,
        string decision, int? websiteId, CancellationToken ct)
    {
        if (childId == null) return;
        try
        {
            var log = new WebAccessLog
            {
                ChildId           = childId.Value,
                Domain            = domain,
                FullUrl           = url.Length > 2000 ? url[..2000] : url,
                AccessResult      = decision == "allowed" ? AccessResult.Allowed : AccessResult.Blocked,
                AllowedWebsiteId  = websiteId,
                SessionStart      = DateTime.UtcNow,
                DurationSeconds   = 0
            };
            _db.WebAccessLogs.Add(log);

            // Cập nhật daily usage nếu được phép
            if (decision == "allowed" && websiteId.HasValue)
            {
                var today = DateOnly.FromDateTime(DateTime.Today);
                var stat = await _db.DailyUsageStats
                    .FirstOrDefaultAsync(d => d.ChildId == childId
                                          && d.AllowedWebsiteId == websiteId
                                          && d.UsageDate == today);
                if (stat == null)
                    _db.DailyUsageStats.Add(new DailyUsageStat
                    {
                        ChildId = childId.Value, AllowedWebsiteId = websiteId.Value,
                        Domain = domain, UsageDate = today, TotalSeconds = 1, RequestCount = 1
                    });
                else
                {
                    stat.TotalSeconds += 1;
                    stat.RequestCount += 1;
                }
            }

            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log access");
        }
    }

    private string BuildBlockPage(string domain, string reason) => $"""
        <!DOCTYPE html>
        <html lang="vi">
        <head>
          <meta charset="UTF-8">
          <meta name="viewport" content="width=device-width, initial-scale=1.0">
          <title>Trang web bị chặn - Family Guardian</title>
          <style>
            * {{ margin: 0; padding: 0; box-sizing: border-box; }}
            body {{
              font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
              min-height: 100vh;
              display: flex;
              align-items: center;
              justify-content: center;
              background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            }}
            .card {{
              background: white;
              padding: 48px 40px;
              border-radius: 20px;
              box-shadow: 0 20px 60px rgba(0,0,0,0.3);
              text-align: center;
              max-width: 480px;
              width: 90%;
            }}
            .icon {{ font-size: 64px; margin-bottom: 16px; }}
            h1 {{ font-size: 24px; color: #1a1a2e; margin-bottom: 8px; font-weight: 700; }}
            .domain {{
              display: inline-block;
              background: #f0f4ff;
              color: #5a67d8;
              padding: 6px 16px;
              border-radius: 20px;
              font-weight: 600;
              font-size: 16px;
              margin: 12px 0;
            }}
            .reason {{
              color: #718096;
              font-size: 14px;
              margin-top: 8px;
              padding: 10px;
              background: #fff5f5;
              border-radius: 8px;
              border-left: 3px solid #fc8181;
            }}
            p {{ color: #4a5568; margin-top: 16px; font-size: 15px; line-height: 1.6; }}
          </style>
        </head>
        <body>
          <div class="card">
            <div class="icon">🛡️</div>
            <h1>Trang web bị chặn</h1>
            <div class="domain">{domain}</div>
            <div class="reason">📋 {reason}</div>
            <p>Trang này không được phép truy cập.<br>Liên hệ bố/mẹ nếu bạn cần hỗ trợ.</p>
          </div>
        </body>
        </html>
        """;
}
```

### DomainNormalizer.cs (Helpers/)
```csharp
public static class DomainNormalizer
{
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var trimmed = input.Trim();
        if (!trimmed.Contains("://")) trimmed = "https://" + trimmed;
        try
        {
            var uri = new Uri(trimmed);
            return uri.Host.Replace("www.", "").ToLowerInvariant();
        }
        catch
        {
            return trimmed.Replace("www.", "").ToLowerInvariant();
        }
    }

    public static bool IsValidDomain(string domain)
    {
        var normalized = Normalize(domain);
        return System.Text.RegularExpressions.Regex.IsMatch(
            normalized, @"^[a-zA-Z0-9][a-zA-Z0-9\-\.]+\.[a-zA-Z]{2,}$");
    }
}
```

---

## 9. SIGNALR HUB (Hubs/NotificationHub.cs)

```csharp
[Authorize]
public class NotificationHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = User?.FindFirst("sub")?.Value;
        if (userId != null)
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? ex)
    {
        var userId = User?.FindFirst("sub")?.Value;
        if (userId != null)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user_{userId}");
        await base.OnDisconnectedAsync(ex);
    }

    // Events từ server xuống client:
    // "ReceiveNotification"  → { id, title, message, type, createdAt }   → tới child
    // "ChildStatusChanged"   → { userId, isOnline, lastSeenAt }          → tới guardian
}
```

---

## 10. JWT SERVICE

```csharp
// GenerateAccessToken(User user) → JWT với claims:
//   "sub"   = user.Id.ToString()
//   "email" = user.Email
//   "name"  = user.FullName
//   "role"  = user.Role.ToString()   ← dùng cho [Authorize(Roles="Guardian")]
//   "jti"   = Guid.NewGuid().ToString()
//   exp     = UtcNow + 1h
//   Ký HS256

// GenerateRefreshToken() = Guid.NewGuid().ToString("N") // 32 ký tự hex
```

---

## 11. APPSETTINGS.JSON

```json
{
  "ConnectionStrings": {
    "Default": "Server=localhost;Database=family_guardian;User=root;Password=yourpassword;Port=3306;"
  },
  "Jwt": {
    "SecretKey": "your-secret-key-minimum-32-characters-here!!",
    "Issuer": "FamilyGuardianAPI",
    "Audience": "FamilyGuardianClient",
    "AccessTokenExpiryHours": 1,
    "RefreshTokenExpiryDays": 7
  },
  "Google": {
    "ClientId": "546760398169-irn9q5hb22579cqp7icprlc1hgtd5hoc.apps.googleusercontent.com",
    "ClientSecret": "your-client-secret",
    "SafeBrowsingApiKey": "your-safe-browsing-api-key"
  },
  "Proxy": {
    "Port": 8888,
    "Enabled": true
  },
  "WebsiteCheck": {
    "TimeoutMs": 5000,
    "CacheMinutes": 30
  }
}
```

---

## 12. PROGRAM.CS (đầy đủ)

```csharp
// Các đăng ký cần có:
// 1. Serilog
// 2. DbContext (MySQL + EF Core + UseSnakeCaseNamingConvention)
// 3. Authentication JWT Bearer (kèm SignalR query string token)
// 4. Authorization
// 5. AutoMapper
// 6. FluentValidation
// 7. HttpClient:
//    - "WebCheck": timeout 5s, AllowAutoRedirect, bỏ qua SSL errors
// 8. SignalR
// 9. Quartz (SendScheduledNotificationsJob, mỗi 1 phút)
// 10. Services (Scoped/Singleton như đã khai báo)
// 11. ProxyServer (HostedService)
// 12. CORS: AllowFrontend → origins: localhost:5173,5174,3000

// Pipeline:
// app.UseSwagger() + UseSwaggerUI(RoutePrefix = "swagger")
// app.UseCors("AllowFrontend")
// app.UseMiddleware<ExceptionMiddleware>()
// app.UseAuthentication()
// app.UseAuthorization()
// app.MapControllers()
// app.MapHub<NotificationHub>("/hubs/notifications")
```

---

## 13. CHECKLIST

- [ ] Tạo project ASP.NET Core 8 Web API
- [ ] Cài NuGet packages
- [ ] Tạo tất cả Entities
- [ ] Tạo AppDbContext với cấu hình đầy đủ
- [ ] Chạy `database.sql` trong MySQL Workbench
- [ ] Implement DomainNormalizer helper
- [ ] Implement JwtService (generate + validate)
- [ ] Implement AuthService + AuthController:
  - [ ] google-login (lưu avatarUrl từ Google)
  - [ ] link-child-google
  - [ ] refresh (rotation)
  - [ ] logout
- [ ] Implement ChildrenController (list, detail, delete, ip-mappings CRUD)
- [ ] Implement WebsiteCheckService (HTTP check + Safe Browsing API)
- [ ] Implement WebsiteCheckController
- [ ] Implement AllowedWebsitesController (list, add, update, delete, toggle, recheck)
- [ ] Implement AccessLogsController (logs phân trang, daily-usage, history)
- [ ] Implement OnlineStatusController (heartbeat, get)
- [ ] Implement NotificationsController
- [ ] Implement ProxyServer (BackgroundService)
- [ ] Implement ProxyConnectionHandler (CONNECT tunnel + HTTP forward + block page)
- [ ] Implement NotificationHub (SignalR)
- [ ] Implement SendScheduledNotificationsJob (Quartz)
- [ ] Implement ExceptionMiddleware
- [ ] Cấu hình CORS, JWT, Quartz trong Program.cs
- [ ] Test Swagger: mọi endpoint đều có thể test