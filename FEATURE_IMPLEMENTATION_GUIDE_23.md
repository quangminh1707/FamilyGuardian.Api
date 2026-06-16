# Family Guardian — Hướng dẫn triển khai tính năng mới (Guide 23)

> **Ngày tạo:** 2026-06-01
> **Dựa trên:** V3 doc + Guide 22 (đã xóa AI moderation xong)
> **Tính năng thêm:**
> - ✅ Favicon qua Google API (Frontend only)
> - ✅ Storage Auto-Cleanup Job (Backend Quartz)
> - ✅ Tamper Detection — cảnh báo extension bị tắt (Backend + Frontend)
> - ✅ Analytics Dashboard (Backend API + Frontend Recharts)
> - ✅ Force SafeSearch (Extension only)
> - ✅ Offline Resilience (Extension only)

---

## ⚠️ Quy tắc bắt buộc

- KHÔNG sửa `sp_ExtensionCheckAccess`
- KHÔNG dùng alarm để block tab (chỉ heartbeat 30s)
- KHÔNG await `showBannerAsync` trước khi block
- KHÔNG query notification theo `child_id` — luôn dùng `guardian_id`
- KHÔNG dùng `DateTime.UtcNow` — luôn dùng `DateTime.Now`
- KHÔNG thay đổi logic heartbeat, block, warning đang chạy
- KHÔNG thay đổi logic chụp ảnh, screenshot polling
- KHÔNG hardcode màu: `bg-white`, `text-gray-*`, `border-gray-*`
- Luôn dùng CSS variables: `bg-bg-surface`, `text-tx-primary`, `border-border-base`

---

## SQL cần chạy (✅ đã chạy)

> Chạy SQL này trước khi làm backend. Sau khi chạy xong thì note "✅ đã chạy" rồi mới code.

```sql
-- ===== TÍNH NĂNG: Tamper Detection =====
-- Thêm cột vào notifications để phân biệt loại cảnh báo
ALTER TABLE notifications
  ADD COLUMN notification_type VARCHAR(50) NULL DEFAULT NULL AFTER message;

-- ===== TÍNH NĂNG: Analytics =====
-- Không cần bảng mới, query từ daily_usage_stats và web_access_logs đã có

-- ===== TÍNH NĂNG: Link Health Check (cho Analytics hiện is_safe) =====
-- Bảng allowed_websites đã có cột is_safe, http_status_code, last_checked_at
-- Không cần migration thêm

-- ===== TÍNH NĂNG: Storage Cleanup =====
-- Không cần migration, chỉ cần Quartz job
```

> ✅ SQL đã chạy — chỉ cần thêm cột `notification_type` vào `notifications`.

---

## PHẦN 1 — FAVICON QUA GOOGLE API (Frontend only, ~30 phút)

### Mục tiêu
Thay vì dùng `favicon_url` lưu trong DB (hay bị lỗi CORS, broken link), dùng Google Favicon Service miễn phí: `https://www.google.com/s2/favicons?domain={domain}&sz=64`

### Kiểm tra trước khi sửa
Tìm tất cả chỗ render favicon trong frontend:
- `WebsiteCard.tsx` — có `<img src={website.favicon_url}>`
- `ChildDetailPage.tsx` — có thể hiển thị favicon
- `AddWebsiteModal.tsx`, `EditWebsiteModal.tsx` — preview favicon

### 1.1 Tạo helper function trong `src/lib/formatters.ts`

Thêm vào cuối file (không sửa các function đã có):

```typescript
// Favicon via Google API — luôn trả về URL hợp lệ, không bao giờ broken
export function getFaviconUrl(domain: string): string {
  if (!domain) return '';
  // Normalize domain: xóa protocol nếu có
  const cleanDomain = domain.replace(/^https?:\/\//, '').replace(/\/.*$/, '');
  return `https://www.google.com/s2/favicons?domain=${cleanDomain}&sz=64`;
}
```

### 1.2 Sửa `WebsiteCard.tsx`

**Kiểm tra:** Tìm dòng render `<img>` favicon, thường là:
```tsx
<img src={website.favicon_url || '/default-favicon.png'} ... />
```

**Sửa thành:**
```tsx
import { getFaviconUrl } from '@/lib/formatters';

// Trong JSX, thay src:
<img
  src={getFaviconUrl(website.domain)}
  alt={website.display_name || website.domain}
  className="w-8 h-8 rounded"
  onError={(e) => {
    // Fallback nếu Google API cũng fail
    (e.target as HTMLImageElement).src = `https://www.google.com/s2/favicons?domain=${website.domain}&sz=32`;
  }}
/>
```

### 1.3 Kiểm tra các file khác

Tìm tất cả `favicon_url` trong frontend và thay bằng `getFaviconUrl(domain)`:
- `ChildDetailPage.tsx` — nếu có render favicon
- `AccessRequestCard.tsx` — nếu có render favicon của domain

**Lưu ý:** KHÔNG xóa cột `favicon_url` khỏi DTO/entity backend. Giữ nguyên để tránh breaking change. Frontend chỉ bỏ qua nó và dùng Google API thay.

---

## PHẦN 2 — STORAGE AUTO-CLEANUP JOB (Backend, ~2 giờ)

### Mục tiêu
Quartz job chạy 2:00 sáng mỗi ngày, xóa ảnh cũ hơn 7 ngày (file vật lý + DB record).

### Kiểm tra trước khi sửa
- Xác nhận đường dẫn ảnh: `wwwroot/screenshots/{childId}/{screenshotId}_{timestamp}.jpg`
- Xác nhận `WebsiteScreenshot` entity có `ImagePath` và `CapturedAt`
- Xác nhận Quartz đã setup trong `Program.cs` (có `ExecuteScheduledScreenshotsJob` rồi)

### 2.1 Tạo `Jobs/StorageCleanupJob.cs`

```csharp
using FamilyGuardian.Api.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace FamilyGuardian.Api.Jobs;

[DisallowConcurrentExecution]
public class StorageCleanupJob : IJob
{
    private readonly ILogger<StorageCleanupJob> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWebHostEnvironment _env;

    // Cấu hình: xóa ảnh cũ hơn X ngày
    private const int RetentionDays = 7;

    public StorageCleanupJob(
        ILogger<StorageCleanupJob> logger,
        IServiceScopeFactory scopeFactory,
        IWebHostEnvironment env)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _env = env;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("[StorageCleanup] Bắt đầu dọn dẹp ảnh cũ hơn {Days} ngày", RetentionDays);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoff = DateTime.Now.AddDays(-RetentionDays);

        // Lấy danh sách ảnh cần xóa
        var oldScreenshots = await db.WebsiteScreenshots
            .Where(s => s.CapturedAt < cutoff && s.Status == "captured")
            .ToListAsync();

        if (!oldScreenshots.Any())
        {
            _logger.LogInformation("[StorageCleanup] Không có ảnh nào cần xóa.");
            return;
        }

        int deletedFiles = 0;
        int deletedRecords = 0;

        foreach (var shot in oldScreenshots)
        {
            // Xóa file vật lý
            if (!string.IsNullOrEmpty(shot.ImagePath))
            {
                var filePath = Path.Combine(_env.WebRootPath, shot.ImagePath.TrimStart('/'));
                if (File.Exists(filePath))
                {
                    try
                    {
                        File.Delete(filePath);
                        deletedFiles++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[StorageCleanup] Không xóa được file: {Path}", filePath);
                    }
                }
            }

            // Xóa DB record
            db.WebsiteScreenshots.Remove(shot);
            deletedRecords++;
        }

        await db.SaveChangesAsync();

        _logger.LogInformation(
            "[StorageCleanup] Hoàn thành: xóa {Files} file, {Records} bản ghi DB.",
            deletedFiles, deletedRecords);
    }
}
```

### 2.2 Đăng ký trong `Program.cs`

Tìm đoạn cấu hình Quartz (chỗ có `ExecuteScheduledScreenshotsJob`), thêm vào:

```csharp
// Trong AddQuartz(...) block:
q.AddJob<StorageCleanupJob>(opts => opts.WithIdentity("StorageCleanupJob"));
q.AddTrigger(opts => opts
    .ForJob("StorageCleanupJob")
    .WithIdentity("StorageCleanupTrigger")
    .WithCronSchedule("0 0 2 * * ?")  // 2:00 AM mỗi ngày
);
```

**Không thêm dòng nào khác, không sửa các job đang có.**

---

## PHẦN 3 — TAMPER DETECTION (Backend + Frontend, ~4 giờ)

### Mục tiêu
Khi extension của con bị tắt/gỡ trong khung giờ đang dùng máy → gửi notification cảnh báo realtime đến guardian.

### Logic hoạt động
1. Extension bị tắt → SignalR disconnect → `OnDisconnectedAsync` fire
2. Backend check: con này có `extension_active = true` trước đó không?
3. Nếu có → tạo notification type `tamper_alert` → gửi SignalR `ReceiveNotification` đến guardian
4. Frontend: hiển thị toast cảnh báo đặc biệt + badge đỏ trong notification list

### Kiểm tra trước khi sửa
- Xem `NotificationHub.cs`: có `OnDisconnectedAsync` chưa? Nó đang làm gì?
- Xem `ExtensionMonitorService.cs`: có method `HandleExtensionOfflineAsync` không?
- Xem `NotificationService.cs`: method `CreateNotificationAsync` nhận những tham số gì?
- Xem entity `Notification`: có cột `notification_type` chưa (sau khi chạy SQL trên)

### 3.1 Sửa Entity `Models/Entities/Notification.cs`

Thêm property mới (không xóa property cũ):

```csharp
[Column("notification_type")]
[MaxLength(50)]
public string? NotificationType { get; set; }
// null = notification thường
// "tamper_alert" = extension bị tắt/gỡ
// "extension_offline" = extension offline bình thường
```

### 3.2 Sửa `Services/ExtensionMonitorService.cs`

**Kiểm tra:** Tìm method xử lý khi extension offline (thường là `HandleExtensionOfflineAsync` hoặc trong `OnDisconnectedAsync`).

Tìm đoạn code tạo notification khi extension offline, thêm logic phân biệt tamper vs offline thường:

```csharp
// Trong method HandleExtensionOfflineAsync hoặc tương đương
// KHÔNG xóa logic cũ — chỉ thêm đoạn check tamper

// Kiểm tra xem extension có đang active ngay trước khi offline không
// (tức là bị tắt đột ngột, không phải con tự tắt máy)
var onlineStatus = await _db.UserOnlineStatuses
    .FirstOrDefaultAsync(s => s.UserId == childId);

bool wasPreviouslyActive = onlineStatus != null
    && onlineStatus.ExtensionActive
    && onlineStatus.ExtensionLastSeen > DateTime.Now.AddMinutes(-2);
    // Nếu heartbeat cuối < 2 phút trước → vừa bị tắt đột ngột

if (wasPreviouslyActive)
{
    // Đây là tamper — extension bị tắt đột ngột
    await CreateTamperAlertAsync(childId, guardianId, childName);
}
// else: offline bình thường (máy tắt, ngủ...) — giữ behavior cũ
```

### 3.3 Thêm method `CreateTamperAlertAsync` vào `ExtensionMonitorService.cs`

```csharp
private async Task CreateTamperAlertAsync(int childId, int guardianId, string childName)
{
    // Tạo notification
    var notification = new Notification
    {
        GuardianId = guardianId,  // LUÔN dùng guardian_id
        Title = "⚠️ Cảnh báo: Tiện ích bị tắt",
        Message = $"Tiện ích Family Guardian trên máy của {childName} vừa bị ngắt kết nối đột ngột. Có thể tiện ích đã bị tắt hoặc gỡ bỏ.",
        NotificationType = "tamper_alert",
        IsRead = false,
        CreatedAt = DateTime.Now  // KHÔNG dùng UtcNow
    };

    _db.Notifications.Add(notification);
    await _db.SaveChangesAsync();  // SaveChanges TRƯỚC khi gọi SignalR

    // Gửi realtime về guardian
    await _hubContext.Clients
        .Group($"guardian_{guardianId}")
        .SendAsync("ReceiveNotification", new
        {
            notification.Id,
            notification.Title,
            notification.Message,
            notification.NotificationType,
            notification.CreatedAt,
            IsRead = false
        });

    // Gửi event TamperAlert riêng để frontend có thể hiển thị toast đặc biệt
    await _hubContext.Clients
        .Group($"guardian_{guardianId}")
        .SendAsync("TamperAlert", new
        {
            ChildId = childId,
            ChildName = childName,
            DetectedAt = DateTime.Now
        });
}
```

### 3.4 Sửa `NotificationDto` (nếu có) để include `notificationType`

Tìm DTO trả về notification, thêm field:

```csharp
public string? NotificationType { get; set; }
```

### 3.5 Sửa Frontend — `useExtensionMonitor.ts`

Tìm danh sách SignalR listeners, thêm listener mới (không xóa listener cũ):

```typescript
// Thêm sau listener ExtensionOffline đang có
connection.on('TamperAlert', (data: { childId: number; childName: string; detectedAt: string }) => {
  // Toast đặc biệt — màu đỏ, text rõ ràng
  toast.error(`🚨 ${data.childName} vừa tắt tiện ích Family Guardian!`);
  // Invalidate notifications để bell badge cập nhật
  queryClient.invalidateQueries({ queryKey: ['notifications'] });
});
```

### 3.6 Sửa `NotificationsPage.tsx` — Hiển thị badge đặc biệt cho tamper_alert

**Kiểm tra:** Tìm chỗ render từng notification item trong danh sách.

Thêm badge cho `notification_type === 'tamper_alert'`:

```tsx
// Trong notification list item — thêm badge sau title:
{notification.notificationType === 'tamper_alert' && (
  <span className="inline-flex items-center px-1.5 py-0.5 rounded text-xs font-medium bg-error/15 text-error border border-error/30">
    🚨 Cảnh báo bảo mật
  </span>
)}
```

**Lưu ý về màu sắc:** Dùng `bg-error/15`, `text-error` (CSS variable) — KHÔNG dùng `bg-red-100`, `text-red-600`.

---

## PHẦN 4 — ANALYTICS DASHBOARD (Backend + Frontend, ~1 ngày)

### Mục tiêu
2 chart trên trang Dashboard của Guardian:
- Bar Chart: tổng thời gian online theo ngày trong 7 ngày gần nhất (cho từng con)
- Pie Chart: Top 5 domain truy cập nhiều nhất (tổng thời gian tích lũy, 30 ngày)

### Kiểm tra trước khi sửa
- `daily_usage_stats` có `child_id`, `usage_date`, `total_seconds`, `bonus_seconds`
- `DashboardPage.tsx` hiện tại đang hiển thị gì? (danh sách con? thống kê nhanh?)
- Recharts đã có sẵn trong `package.json` chưa? Nếu chưa: `npm install recharts`

### 4.1 Backend — Tạo `AnalyticsController.cs`

```csharp
using FamilyGuardian.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Controllers;

[ApiController]
[Route("api/analytics")]
[Authorize]
public class AnalyticsController : ControllerBase
{
    private readonly AppDbContext _db;

    public AnalyticsController(AppDbContext db)
    {
        _db = db;
    }

    // GET /api/analytics/weekly?childId=2
    // Trả về tổng seconds mỗi ngày trong 7 ngày gần nhất
    [HttpGet("weekly")]
    public async Task<IActionResult> GetWeeklyUsage([FromQuery] int childId)
    {
        var guardianId = int.Parse(User.FindFirst("sub")?.Value ?? "0");

        // Verify guardian owns this child
        var hasAccess = await _db.GuardianChildRelationships
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
        if (!hasAccess) return Forbid();

        var since = DateTime.Now.Date.AddDays(-6); // 7 ngày kể cả hôm nay

        var stats = await _db.DailyUsageStats
            .Where(s => s.ChildId == childId && s.UsageDate >= DateOnly.FromDateTime(since))
            .GroupBy(s => s.UsageDate)
            .Select(g => new
            {
                Date = g.Key.ToString("yyyy-MM-dd"),
                // effective_seconds = GREATEST(0, total - bonus)
                TotalSeconds = g.Sum(s =>
                    s.TotalSeconds - s.BonusSeconds > 0
                    ? s.TotalSeconds - s.BonusSeconds
                    : 0)
            })
            .OrderBy(x => x.Date)
            .ToListAsync();

        // Fill ngày thiếu với 0
        var result = Enumerable.Range(0, 7)
            .Select(i => since.AddDays(i))
            .Select(date => new
            {
                Date = date.ToString("yyyy-MM-dd"),
                TotalSeconds = stats.FirstOrDefault(s => s.Date == date.ToString("yyyy-MM-dd"))?.TotalSeconds ?? 0
            })
            .ToList();

        return Ok(result);
    }

    // GET /api/analytics/top-domains?childId=2
    // Trả về top 5 domain theo tổng thời gian 30 ngày
    [HttpGet("top-domains")]
    public async Task<IActionResult> GetTopDomains([FromQuery] int childId)
    {
        var guardianId = int.Parse(User.FindFirst("sub")?.Value ?? "0");

        var hasAccess = await _db.GuardianChildRelationships
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
        if (!hasAccess) return Forbid();

        var since = DateTime.Now.Date.AddDays(-29); // 30 ngày

        var topDomains = await _db.DailyUsageStats
            .Where(s => s.ChildId == childId && s.UsageDate >= DateOnly.FromDateTime(since))
            .GroupBy(s => s.Domain)
            .Select(g => new
            {
                Domain = g.Key,
                TotalSeconds = g.Sum(s =>
                    s.TotalSeconds - s.BonusSeconds > 0
                    ? s.TotalSeconds - s.BonusSeconds
                    : 0)
            })
            .OrderByDescending(x => x.TotalSeconds)
            .Take(5)
            .ToListAsync();

        return Ok(topDomains);
    }
}
```

**Kiểm tra:** Xem `User.FindFirst("sub")` hay `User.FindFirst(ClaimTypes.NameIdentifier)` — tùy theo JWT setup hiện tại. Xem các controller khác đang lấy guardianId như thế nào và dùng cách giống nhau.

### 4.2 Frontend — Thêm API functions vào `src/api/analyticsApi.ts` (tạo file mới)

```typescript
import api from './axios';

export interface DailyUsage {
  date: string;        // "2026-05-25"
  totalSeconds: number;
}

export interface DomainUsage {
  domain: string;
  totalSeconds: number;
}

export const getWeeklyUsage = (childId: number) =>
  api.get<DailyUsage[]>(`/analytics/weekly?childId=${childId}`)
     .then(r => r.data);

export const getTopDomains = (childId: number) =>
  api.get<DomainUsage[]>(`/analytics/top-domains?childId=${childId}`)
     .then(r => r.data);
```

### 4.3 Frontend — Tạo component `src/components/UsageChart.tsx`

> Đây là component reusable, hiển thị 2 chart. Guardian chọn con → chart cập nhật.

```tsx
import { useQuery } from '@tanstack/react-query';
import {
  BarChart, Bar, XAxis, YAxis, Tooltip, ResponsiveContainer,
  PieChart, Pie, Cell, Legend
} from 'recharts';
import { getWeeklyUsage, getTopDomains } from '@/api/analyticsApi';
import { getFaviconUrl } from '@/lib/formatters';

interface UsageChartProps {
  childId: number;
  childName: string;
}

// Màu cho Pie chart — dùng màu hệ thống, không hardcode
const PIE_COLORS = ['#6366f1', '#22c55e', '#f59e0b', '#ef4444', '#8b5cf6'];

function formatMinutes(seconds: number): string {
  const m = Math.round(seconds / 60);
  if (m < 60) return `${m} phút`;
  return `${Math.floor(m / 60)}g ${m % 60}p`;
}

function formatDateShort(dateStr: string): string {
  const d = new Date(dateStr);
  return `${d.getDate()}/${d.getMonth() + 1}`;
}

export default function UsageChart({ childId, childName }: UsageChartProps) {
  const { data: weekly, isLoading: loadingWeekly } = useQuery({
    queryKey: ['analytics-weekly', childId],
    queryFn: () => getWeeklyUsage(childId),
    refetchInterval: 30000,
    staleTime: 15000,
  });

  const { data: topDomains, isLoading: loadingDomains } = useQuery({
    queryKey: ['analytics-top-domains', childId],
    queryFn: () => getTopDomains(childId),
    refetchInterval: 30000,
    staleTime: 15000,
  });

  const weeklyData = (weekly ?? []).map(d => ({
    date: formatDateShort(d.date),
    minutes: Math.round(d.totalSeconds / 60),
  }));

  const pieData = (topDomains ?? []).map(d => ({
    name: d.domain,
    value: Math.round(d.totalSeconds / 60),
  }));

  return (
    <div className="space-y-6">
      {/* Header */}
      <h3 className="text-base font-semibold text-tx-primary">
        Thống kê sử dụng — {childName}
      </h3>

      {/* Bar Chart: 7 ngày */}
      <div className="bg-bg-surface border border-border-base rounded-xl p-4">
        <p className="text-sm font-medium text-tx-secondary mb-3">
          Thời gian online 7 ngày qua (phút)
        </p>
        {loadingWeekly ? (
          <div className="h-48 flex items-center justify-center">
            <span className="text-sm text-tx-secondary">Đang tải...</span>
          </div>
        ) : weeklyData.every(d => d.minutes === 0) ? (
          <div className="h-48 flex items-center justify-center">
            <span className="text-sm text-tx-secondary">Chưa có dữ liệu</span>
          </div>
        ) : (
          <ResponsiveContainer width="100%" height={200}>
            <BarChart data={weeklyData} margin={{ top: 4, right: 8, left: -16, bottom: 0 }}>
              <XAxis
                dataKey="date"
                tick={{ fontSize: 12, fill: 'var(--color-tx-secondary)' }}
                axisLine={false}
                tickLine={false}
              />
              <YAxis
                tick={{ fontSize: 12, fill: 'var(--color-tx-secondary)' }}
                axisLine={false}
                tickLine={false}
              />
              <Tooltip
                formatter={(value: number) => [`${value} phút`, 'Thời gian']}
                contentStyle={{
                  background: 'var(--color-bg-elevated)',
                  border: '1px solid var(--color-border-base)',
                  borderRadius: '8px',
                  color: 'var(--color-tx-primary)',
                  fontSize: '12px',
                }}
              />
              <Bar dataKey="minutes" fill="var(--color-brand-DEFAULT)" radius={[4, 4, 0, 0]} />
            </BarChart>
          </ResponsiveContainer>
        )}
      </div>

      {/* Pie Chart: Top 5 domain */}
      <div className="bg-bg-surface border border-border-base rounded-xl p-4">
        <p className="text-sm font-medium text-tx-secondary mb-3">
          Top 5 website sử dụng nhiều nhất (30 ngày)
        </p>
        {loadingDomains ? (
          <div className="h-48 flex items-center justify-center">
            <span className="text-sm text-tx-secondary">Đang tải...</span>
          </div>
        ) : !pieData.length ? (
          <div className="h-48 flex items-center justify-center">
            <span className="text-sm text-tx-secondary">Chưa có dữ liệu</span>
          </div>
        ) : (
          <div className="flex flex-col sm:flex-row items-center gap-4">
            <ResponsiveContainer width={180} height={180}>
              <PieChart>
                <Pie
                  data={pieData}
                  cx="50%"
                  cy="50%"
                  innerRadius={50}
                  outerRadius={80}
                  dataKey="value"
                  paddingAngle={3}
                >
                  {pieData.map((_, i) => (
                    <Cell key={i} fill={PIE_COLORS[i % PIE_COLORS.length]} />
                  ))}
                </Pie>
                <Tooltip
                  formatter={(value: number) => [formatMinutes(value * 60), 'Thời gian']}
                  contentStyle={{
                    background: 'var(--color-bg-elevated)',
                    border: '1px solid var(--color-border-base)',
                    borderRadius: '8px',
                    color: 'var(--color-tx-primary)',
                    fontSize: '12px',
                  }}
                />
              </PieChart>
            </ResponsiveContainer>

            {/* Legend tùy chỉnh — hiển thị favicon */}
            <div className="flex flex-col gap-2 flex-1 w-full">
              {pieData.map((item, i) => (
                <div key={i} className="flex items-center gap-2">
                  <div
                    className="w-2.5 h-2.5 rounded-full flex-shrink-0"
                    style={{ background: PIE_COLORS[i % PIE_COLORS.length] }}
                  />
                  <img
                    src={getFaviconUrl(item.name)}
                    alt=""
                    className="w-4 h-4 rounded flex-shrink-0"
                    onError={(e) => { (e.target as HTMLImageElement).style.display = 'none'; }}
                  />
                  <span className="text-sm text-tx-primary truncate flex-1">{item.name}</span>
                  <span className="text-xs text-tx-secondary flex-shrink-0">
                    {formatMinutes(item.value * 60)}
                  </span>
                </div>
              ))}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
```

### 4.4 Tích hợp vào `DashboardPage.tsx`

**Kiểm tra trước:** DashboardPage hiện đang hiển thị gì? Có danh sách con không?

**Thêm vào DashboardPage:**

```tsx
import { useState } from 'react';
import UsageChart from '@/components/UsageChart';

// Trong component, sau khi đã fetch danh sách con (children):
const [selectedChildId, setSelectedChildId] = useState<number | null>(null);

// Hiển thị children.data?.[0].id làm default khi load xong
// useEffect hoặc điều kiện render

// UI: Selector chọn con + Chart
{children && children.length > 0 && (
  <div className="bg-bg-surface border border-border-base rounded-xl p-5">
    <div className="flex items-center justify-between mb-4">
      <h2 className="text-base font-semibold text-tx-primary">Thống kê hoạt động</h2>
      <select
        value={selectedChildId ?? children[0].id}
        onChange={(e) => setSelectedChildId(Number(e.target.value))}
        className="text-sm border border-border-base rounded-lg px-3 py-1.5
                   bg-bg-surface text-tx-primary focus:outline-none focus:ring-2
                   focus:ring-brand-DEFAULT"
      >
        {children.map(child => (
          <option key={child.id} value={child.id}>{child.fullName}</option>
        ))}
      </select>
    </div>
    <UsageChart
      childId={selectedChildId ?? children[0].id}
      childName={children.find(c => c.id === (selectedChildId ?? children[0].id))?.fullName ?? ''}
    />
  </div>
)}
```

**Lưu ý:** Nếu DashboardPage chưa có query children → dùng cùng queryKey `['children']` và API `getChildren()` từ `childrenApi.ts` đã có.

---

## PHẦN 5 — FORCE SAFESEARCH (Extension only, ~3 giờ)

### Mục tiêu
Khi con search Google/Bing/DuckDuckGo → tự động thêm tham số SafeSearch vào URL.

### ⚠️ Quy tắc Extension
- KHÔNG thay đổi logic heartbeat, block, alarm warning
- KHÔNG thay đổi `captureScreenshotForDomain`
- Chỉ thêm vào `background.js`, không xóa code cũ

### Kiểm tra trước khi sửa
- Xem `manifest.template.json`: có `declarativeNetRequest` permission chưa?
- Xem `background.js`: tìm `chrome.tabs.onUpdated` — nếu đã có thì thêm logic vào handler đó, KHÔNG tạo listener mới

### 5.1 Thêm vào `manifest.template.json`

Trong phần `permissions`, thêm (nếu chưa có):
```json
"declarativeNetRequest"
```

Thêm section mới (không thay đổi section cũ):
```json
"declarative_net_request": {
  "rule_resources": []
}
```

> Không dùng static rules vì cần check config từ server. Dùng dynamic rules trong background.js.

### 5.2 Thêm vào `background.js` — SafeSearch redirect

Thêm vào phần đầu file sau phần khai báo constants (KHÔNG thay đổi các constant cũ):

```javascript
// ===== SAFESEARCH CONFIG =====
// Chạy sau khi get config từ server — biết con có filter_enabled không
let safeSearchEnabled = false; // Set true khi extension biết đang là tài khoản con

// Map domain → tham số SafeSearch
const SAFESEARCH_RULES = {
  'google.com': { param: 'safe', value: 'active' },
  'www.google.com': { param: 'safe', value: 'active' },
  'bing.com': { param: 'adlt', value: 'strict' },
  'www.bing.com': { param: 'adlt', value: 'strict' },
  'duckduckgo.com': { param: 'kp', value: '1' },
};
```

Thêm function SafeSearch (thêm trước hoặc sau các function hiện có, KHÔNG trong function khác):

```javascript
// ===== SAFESEARCH FUNCTION =====
function applySafeSearch(url) {
  if (!safeSearchEnabled) return null;

  try {
    const parsed = new URL(url);
    const hostname = parsed.hostname.replace(/^www\./, '');
    const fullHostname = parsed.hostname;

    const rule = SAFESEARCH_RULES[fullHostname] || SAFESEARCH_RULES[hostname];
    if (!rule) return null;

    // Chỉ áp dụng cho trang search (có query q=)
    if (!parsed.searchParams.has('q') && !parsed.searchParams.has('query')) return null;

    // Kiểm tra đã có tham số chưa
    if (parsed.searchParams.get(rule.param) === rule.value) return null;

    // Thêm tham số SafeSearch
    parsed.searchParams.set(rule.param, rule.value);
    return parsed.toString();
  } catch {
    return null;
  }
}
```

Tìm `chrome.tabs.onUpdated` trong `background.js`. Nếu đã có:

```javascript
// Tìm chỗ hiện có chrome.tabs.onUpdated.addListener
// THÊM vào đầu handler đó (không thay thế handler):
chrome.tabs.onUpdated.addListener(async (tabId, changeInfo, tab) => {
  // ===== CODE CŨ GIỮ NGUYÊN =====
  // ... existing code ...

  // ===== THÊM: SafeSearch check =====
  if (changeInfo.url && safeSearchEnabled) {
    const safeUrl = applySafeSearch(changeInfo.url);
    if (safeUrl) {
      chrome.tabs.update(tabId, { url: safeUrl });
      return; // Dừng sau khi redirect SafeSearch
    }
  }
  // ===== HẾT PHẦN THÊM =====
});
```

Nếu CHƯA có `chrome.tabs.onUpdated`:

```javascript
// Thêm listener mới hoàn toàn
chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if (!changeInfo.url || !safeSearchEnabled) return;
  const safeUrl = applySafeSearch(changeInfo.url);
  if (safeUrl) {
    chrome.tabs.update(tabId, { url: safeUrl });
  }
});
```

Tìm chỗ xử lý `/config` response trong `background.js` (khi gọi `GET /api/extension/config`), thêm:

```javascript
// Trong callback sau khi get config thành công:
// Giả sử config trả về { filterEnabled: true, ... }
safeSearchEnabled = config.filterEnabled === true;
```

> **Kiểm tra:** Tìm `extension/config` trong background.js để biết tên field chính xác. Backend trả về `filter_enabled` hay `filterEnabled`? Dùng đúng tên đó.

---

## PHẦN 6 — OFFLINE RESILIENCE (Extension only, ~4 giờ)

### Mục tiêu
Khi con ngắt mạng → extension vẫn block được web nhờ whitelist cache trong `chrome.storage.local`.

### ⚠️ Quy tắc Extension
- KHÔNG thay đổi logic online (heartbeat, check access từ API)
- Chỉ thêm fallback khi API THẤT BẠI do network error
- KHÔNG cache kết quả blocked — chỉ cache whitelist

### Kiểm tra trước khi sửa
- Tìm hàm check access trong `background.js` — thường là khi `chrome.tabs.onUpdated` hoặc intercepting navigation
- Xem flow hiện tại: gọi `/api/extension/check?domain=x` → nếu allowed thì cho qua, blocked thì redirect
- Tìm chỗ gọi `/api/extension/config` — response trả về whitelist hay không?

### 6.1 Thêm Cache Functions vào `background.js`

Thêm sau phần SafeSearch constants (KHÔNG thay đổi code cũ):

```javascript
// ===== OFFLINE RESILIENCE CACHE =====
const WHITELIST_CACHE_KEY = 'fg_whitelist_cache';
const CACHE_MAX_AGE_MS = 24 * 60 * 60 * 1000; // 24 giờ

// Lưu whitelist vào storage local
async function saveWhitelistCache(domains) {
  try {
    await chrome.storage.local.set({
      [WHITELIST_CACHE_KEY]: {
        domains: domains,        // string[]
        savedAt: Date.now()
      }
    });
  } catch (e) {
    console.warn('[FG] Không lưu được whitelist cache:', e);
  }
}

// Đọc whitelist từ cache
async function getWhitelistCache() {
  try {
    const result = await chrome.storage.local.get(WHITELIST_CACHE_KEY);
    const cache = result[WHITELIST_CACHE_KEY];
    if (!cache || !cache.domains) return null;
    // Cache quá cũ → bỏ qua
    if (Date.now() - cache.savedAt > CACHE_MAX_AGE_MS) return null;
    return cache.domains; // string[]
  } catch {
    return null;
  }
}

// Check domain có trong whitelist cache không
function isDomainInCache(domain, cachedDomains) {
  if (!cachedDomains || !domain) return false;
  const clean = domain.replace(/^www\./, '');
  return cachedDomains.some(d => {
    const cached = d.replace(/^www\./, '');
    return clean === cached || clean.endsWith('.' + cached);
  });
}
```

### 6.2 Cập nhật flow gọi `/api/extension/config`

Tìm chỗ gọi API config trong background.js, sau khi nhận response thành công → lưu cache:

```javascript
// Trong callback success của /api/extension/config:
// Giả sử response là { allowedDomains: [...], filterEnabled: true, ... }
// THÊM vào sau khi xử lý response (không thay đổi xử lý cũ):

if (configData.allowedDomains && Array.isArray(configData.allowedDomains)) {
  await saveWhitelistCache(configData.allowedDomains);
}
```

> **Kiểm tra:** Backend `/api/extension/config` có trả về danh sách domain không? Nếu không, bổ sung vào backend response (xem 6.3).

### 6.3 Backend — Bổ sung `allowedDomains` vào `/api/extension/config` response

**Kiểm tra `ExtensionController.cs`:** Tìm action `Config()`, xem nó đang trả về gì.

Thêm `AllowedDomains` vào response (nếu chưa có):

```csharp
// Trong ExtensionController.cs — action GetConfig()
// Thêm query lấy danh sách domain đang active của child này
var allowedDomains = await _db.AllowedWebsites
    .Where(w => w.ChildId == child.Id && w.IsActive)
    .Select(w => w.Domain)
    .ToListAsync();

// Thêm vào object trả về:
return Ok(new
{
    // ... các field cũ giữ nguyên ...
    AllowedDomains = allowedDomains
});
```

**Lưu ý:** Nếu có DTO riêng cho config response → thêm property `AllowedDomains` vào DTO, không trả về anonymous object.

### 6.4 Thêm Offline Fallback vào check access flow

Tìm hàm check access domain trong `background.js` (nơi gọi `/api/extension/check?domain=`). Bọc phần gọi API bằng try/catch và thêm fallback:

```javascript
// Tìm đoạn gọi /api/extension/check
// THÊM offline fallback — giữ nguyên tất cả code trong try block

async function checkDomainAccess(domain) {
  try {
    // ===== CODE CŨ: gọi API =====
    const token = await getGoogleToken();
    const res = await fetch(`${CONFIG.API_BASE}/api/extension/check?domain=${domain}`, {
      headers: { Authorization: `Bearer ${token}` },
      signal: AbortSignal.timeout(5000) // timeout 5s
    });

    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const data = await res.json();

    // Nếu online thành công → cập nhật cache (nếu cần)
    return data; // { allowed: true/false, ... }

  } catch (err) {
    // ===== THÊM: Offline fallback =====
    const isNetworkError = err instanceof TypeError
      || err.name === 'TimeoutError'
      || err.message?.includes('fetch');

    if (isNetworkError) {
      console.warn('[FG] API không kết nối được, dùng offline cache:', err.message);
      const cachedDomains = await getWhitelistCache();

      if (cachedDomains) {
        const allowed = isDomainInCache(domain, cachedDomains);
        console.log(`[FG] Offline check: ${domain} → ${allowed ? 'allowed' : 'blocked'}`);
        return { allowed, offline: true };
      } else {
        // Không có cache → block luôn để an toàn
        console.warn('[FG] Không có cache, block offline');
        return { allowed: false, offline: true };
      }
    }

    // Lỗi khác (auth, parse...) → throw để caller xử lý
    throw err;
  }
}
```

> **Lưu ý quan trọng:** Nếu check access hiện tại không phải 1 hàm riêng mà inline trong event handler → hãy đọc kỹ flow và chỉ thêm try/catch + fallback, KHÔNG refactor cấu trúc.

---

## PHẦN 7 — KIỂM TRA TOÀN BỘ SAU KHI LÀM XONG

### 7.1 Kiểm tra Backend

```bash
cd FamilyGuardian.Api
dotnet build
```

Checklist:
- [ ] Không có lỗi compile
- [ ] `StorageCleanupJob` inject đúng `IWebHostEnvironment`
- [ ] `AnalyticsController` lấy `guardianId` đúng cách (giống các controller khác)
- [ ] Notification entity có `NotificationType` property
- [ ] `TamperAlert` được gửi SAU `SaveChangesAsync()`
- [ ] KHÔNG có `DateTime.UtcNow` mới nào trong code thêm vào

### 7.2 Kiểm tra Frontend

```bash
cd family-guardian-frontend
npm run build
```

Checklist:
- [ ] Không có TypeScript error
- [ ] `UsageChart` không import màu hardcode
- [ ] `getFaviconUrl` được import đúng từ `@/lib/formatters`
- [ ] `TamperAlert` listener trong `useExtensionMonitor.ts` không conflict với listener cũ
- [ ] Tất cả className dùng CSS variables (`bg-bg-surface`, `text-tx-primary`, v.v.)
- [ ] KHÔNG có `bg-white`, `text-gray-*`, `border-gray-*` trong code mới

### 7.3 Kiểm tra Extension

Checklist (không cần build, chỉ review code):
- [ ] Heartbeat 30s không bị thay đổi
- [ ] `screenshot_poll` alarm 5s không bị thay đổi
- [ ] `captureScreenshotForDomain` không bị thay đổi
- [ ] Block logic (`limitExceeded → tabs.update`) không bị thay đổi
- [ ] `showBannerAsync` vẫn fire-and-forget (không await trước block)
- [ ] SafeSearch chỉ redirect URL, không block hay dùng alarm
- [ ] Offline fallback chỉ kích hoạt khi `TypeError` / `TimeoutError` (network error thực sự)

### 7.4 Kiểm tra Dark Mode

- [ ] Recharts Tooltip dùng CSS variables (`var(--color-bg-elevated)`, v.v.)
- [ ] Select dropdown trong Dashboard dùng `bg-bg-surface text-tx-primary`
- [ ] Badge tamper_alert dùng `bg-error/15 text-error`
- [ ] Loading text dùng `text-tx-secondary`

---

## Thứ tự thực hiện khuyến nghị

```
Bước 1 — Chạy SQL (xem phần đầu)
Bước 2 — Backend: StorageCleanupJob → đăng ký Quartz
Bước 3 — Backend: AnalyticsController
Bước 4 — Backend: Tamper Detection (sửa ExtensionMonitorService + Notification entity)
Bước 5 — Backend: Bổ sung allowedDomains vào /config response (cho Offline Resilience)
Bước 6 — dotnet build → fix lỗi
Bước 7 — Frontend: getFaviconUrl + sửa WebsiteCard + các component dùng favicon
Bước 8 — Frontend: analyticsApi.ts + UsageChart.tsx
Bước 9 — Frontend: Tích hợp UsageChart vào DashboardPage
Bước 10 — Frontend: TamperAlert listener + badge trong NotificationsPage
Bước 11 — npm run build → fix lỗi
Bước 12 — Extension: SafeSearch
Bước 13 — Extension: Offline Resilience cache
Bước 14 — node build-config.js → test thủ công trên Chrome
```

---

## Ghi chú các điểm dễ sai

| Điểm | Đúng | Sai |
|------|------|-----|
| Lấy guardianId trong controller | Xem controller khác đang dùng cách nào | `User.FindFirst("sub")` có thể khác nhau |
| Notification query | `guardian_id` | ~~`child_id`~~ |
| Thời gian | `DateTime.Now` | ~~`DateTime.UtcNow`~~ |
| SignalR order | `SaveChangesAsync()` rồi mới `SendAsync()` | ~~Ngược lại~~ |
| Extension datetime | `toLocalISOString()` | ~~`toISOString()`~~ |
| Màu Recharts | `var(--color-brand-DEFAULT)` | ~~`#6366f1`~~ hardcode |
| Offline fallback | Chỉ khi `TypeError`/`TimeoutError` | ~~Khi mọi lỗi HTTP~~ |
| SafeSearch | Chỉ khi có param `q=` hoặc `query=` | ~~Mọi URL search engine~~ |

---

## PHẦN 8 — SỬA LỖI ENCODING UTF-8 TRONG SCREENSHOT MODAL (Frontend, ~1 giờ)

### Mô tả lỗi
Trong trang Camera (ScreenshotModal / ScreenshotPage), tất cả text tiếng Việt bị hiển thị sai thành ký tự rác kiểu:
- `Ã¢Â€Â™` thay vì `'`
- `Ãƒ` thay vì `Ã`
- `cÃ¢á»nh chá»„p` thay vì `cảnh chụp`
- Toàn bộ label, tiêu đề, thông báo trong modal đều bị ảnh hưởng

**Nguyên nhân:** Chuỗi string hardcode tiếng Việt trong file `.tsx`/`.ts` bị lưu sai encoding (không phải UTF-8), hoặc file bị đọc sai charset khi build. Phổ biến nhất: file được copy/paste từ nguồn Windows-1252 hoặc Latin-1.

### Kiểm tra trước khi sửa

**Bước 1:** Mở file liên quan trong editor và kiểm tra encoding ở góc dưới bên phải (VS Code hiển thị `UTF-8`). Nếu thấy `Latin-1`, `Windows-1252`, hay `ISO 8859-1` → đó là nguyên nhân.

**Bước 2:** Tìm tất cả file liên quan đến tính năng Camera/Screenshot:
```
src/
├── pages/ChildDetailPage.tsx        ← có thể chứa ScreenshotModal
├── components/ScreenshotModal.tsx   ← file chính
├── components/ScheduleModal.tsx     ← modal hẹn giờ (nếu có riêng)
└── api/childrenApi.ts               ← DTO, ít bị ảnh hưởng hơn
```

**Bước 3:** Trong mỗi file, tìm string tiếng Việt hardcode. Ví dụ:
```tsx
// Bị lỗi — nhìn trong editor thấy ký tự lạ:
<p>Lịch chụp ảnh</p>
<span>Tất cả</span>
<button>Chụp ngay</button>
```

### 8.1 Cách sửa — Re-save file đúng encoding

**Cách nhanh nhất (VS Code):**
1. Mở file `ScreenshotModal.tsx`
2. Nhấn `Ctrl+Shift+P` → gõ `Change File Encoding`
3. Chọn `Save with Encoding` → chọn `UTF-8`
4. Lặp lại cho tất cả file bị ảnh hưởng

**Nếu cách trên không được** — thay thế string trực tiếp trong code:

### 8.2 Danh sách string cần kiểm tra và thay thế trong `ScreenshotModal.tsx`

> Đọc file thực tế để xác định đúng vị trí. Dưới đây là các string phổ biến dựa trên ảnh chụp màn hình:

```tsx
// ===== PHẦN HEADER / TAB =====
// Sai → Đúng:
"Lá»‹ch chá»¥p á°nh" → "Lịch chụp ảnh"
"Táº¥t cáº£"         → "Tất cả"
"Hôm nay"           → giữ nguyên nếu đúng
"7 ngày"            → giữ nguyên nếu đúng

// ===== PHẦN FILTER / TOOLBAR =====
"Lá»‹ch"                        → "Lịch"
"TẠ¥t cả nh thÆ°á»ng"          → "Tất cảnh thường"  
"Chá»‰ tab Äang"                → "Chỉ tab đang"
"Háº¹n giá»"                    → "Hẹn giờ"
"Kháº¯c"                        → "Khác"

// ===== PHẦN THÔNG BÁO =====
"Con cháÆ°a má»" mÆ°á»Ÿ..."   → "Con chưa mở website nào..."
"CÃ³ n cháÆ°a má»..."          → "Còn chưa mở..."

// ===== PHẦN SCHEDULED SCREENSHOTS =====
"Khung giá» chá»¥p áº£nh..."    → "Khung giờ chụp ảnh..."
"Khá´ng cÃ³ lá»‹ch chá»¥p"     → "Không có lịch chụp"
"ÄÃ£ huáº·"                    → "Đã huỷ"
"Äang chá»"                    → "Đang chờ"
"ÄÃ£ thá»±c hiá»‡n"            → "Đã thực hiện"
"HáºЁn giá»"                   → "Hẹn giờ"

// ===== NÚT BẤM =====
"Cháº·p ngay"                  → "Chụp ngay"
"HáºЁn giá»"                   → "Hẹn giờ"
"XÃ³a"                         → "Xóa"
"HáºЁn"                        → "Hẹn"
```

### 8.3 Cách thay thế an toàn nhất — Dùng Unicode escape

Nếu file vẫn bị lỗi sau khi re-save encoding, thay thế string sang Unicode escape trong JSX:

```tsx
// Thay vì dùng string tiếng Việt trực tiếp (dễ bị corrupt):
<p>Lịch chụp ảnh</p>

// Dùng Unicode escape (không bao giờ bị lỗi encoding):
<p>L&#7883;ch ch&#7909;p &#7843;nh</p>

// Hoặc tách ra constant ở đầu file:
const LABELS = {
  schedule: 'L\u1ECBch ch\u1EE5p \u1EA3nh',
  captureNow: 'Ch\u1EE5p ngay',
  all: 'T\u1EA5t c\u1EA3',
  today: 'H\xF4m nay',
  schedule7days: '7 ng\xE0y',
  noSchedule: 'Kh\xF4ng c\xF3 l\u1ECBch ch\u1EE5p',
  pending: '\u0110ang ch\u1EDD',
  executed: '\u0110\xE3 th\u1EF1c hi\u1EC7n',
  cancelled: '\u0110\xE3 hu\u1EF7',
} as const;

// Dùng trong JSX:
<p>{LABELS.schedule}</p>
<button>{LABELS.captureNow}</button>
```

### 8.4 Kiểm tra các file khác cùng bị lỗi

Ngoài `ScreenshotModal.tsx`, kiểm tra các file sau — nếu cùng được tạo/copy trong một batch thì có thể bị cùng lỗi:

```
ScheduleScreenshotModal.tsx   ← modal hẹn giờ chụp
WarningConfigModal.tsx        ← modal cấu hình cảnh báo
EditWebsiteModal.tsx          ← modal sửa website
AddWebsiteModal.tsx           ← modal thêm website
```

**Cách kiểm tra nhanh:** Tìm trong file có chứa chuỗi `Ã` hay `á»` không:
```bash
# Chạy trong terminal tại thư mục src/
grep -r "Ã\|á»\|Ä"\|â€" --include="*.tsx" --include="*.ts" -l
```
File nào xuất hiện trong kết quả → file đó bị lỗi encoding.

### 8.5 Ngăn lỗi tái phát

Thêm vào `.editorconfig` ở root project (tạo nếu chưa có):

```ini
root = true

[*]
charset = utf-8
end_of_line = lf
insert_final_newline = true
trim_trailing_whitespace = true

[*.{ts,tsx,js,jsx,json,css,md}]
indent_style = space
indent_size = 2
```

Thêm vào `vite.config.ts` (không thay đổi config cũ):

```typescript
export default defineConfig({
  // ... config cũ giữ nguyên ...
  build: {
    // ... config build cũ ...
    // Đảm bảo output luôn UTF-8:
    charset: 'utf8',
  },
});
```

### 8.6 Checklist sau khi sửa

- [ ] Chạy lệnh grep — không còn file nào chứa `Ã` hay `á»`
- [ ] `ScreenshotModal` hiển thị đúng text tiếng Việt
- [ ] Các tab filter (Tất cả / Hôm nay / 7 ngày) hiển thị đúng
- [ ] Danh sách lịch chụp (Đang chờ / Đã thực hiện / Đã huỷ) hiển thị đúng
- [ ] Nút "Chụp ngay" và "Hẹn giờ" hiển thị đúng
- [ ] Thông báo trống ("Chưa có lịch chụp nào") hiển thị đúng
- [ ] Dark mode vẫn hoạt động bình thường sau khi sửa
- [ ] `npm run build` → 0 errors
