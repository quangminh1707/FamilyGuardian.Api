# Family Guardian — AI 18+ + Fix Schedule UI/Timezone + Dark Mode Audit (Phần 16)

> **Ngày tạo:** 2026-05-22
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE_15.md (Phần 15)

---

## ⚠️ Quy tắc bất di bất dịch

| Quy tắc | Chi tiết |
|---------|---------|
| `sp_ExtensionCheckAccess` | Tuyệt đối KHÔNG sửa |
| background.js logic | KHÔNG thay đổi |
| Logic hiện tại | KHÔNG thay đổi, chỉ sửa bug + bổ sung |
| Dark mode | CSS variables: `bg-bg-surface`, `text-tx-primary`, `border-border-base`, `brand-DEFAULT` |

---

## Kết quả đọc code

| File | Phát hiện |
|------|-----------|
| `AddWebsiteModal.tsx` | Time picker = `<TimeInput24h label value onChange />` từ `'../ui/TimeInput24h'` |
| `formatters.ts` | Đã có `normalizeBackendDate` — dùng lại khi hiển thị `scheduledAt` |
| `formatters.ts` | **CHƯA CÓ** `toLocalISOString` — cần thêm để fix timezone gửi lên backend |

---

## SQL cần chạy (đã làm xong)

```sql
-- Thêm 3 cột AI moderation vào website_screenshots
ALTER TABLE website_screenshots
  ADD COLUMN ai_moderation_status VARCHAR(20) NULL
      COMMENT 'safe | flagged | error | skipped',
  ADD COLUMN ai_adult_score       VARCHAR(20) NULL
      COMMENT 'VERY_UNLIKELY|UNLIKELY|POSSIBLE|LIKELY|VERY_LIKELY',
  ADD COLUMN ai_checked_at        DATETIME   NULL;
```

> ✅ Sau khi chạy SQL → note "đã chạy" rồi mới làm backend.
> Không cần bảng riêng để đếm usage — đếm trực tiếp từ cột `ai_checked_at` trong `website_screenshots`.



---

## PHẦN A — Fix Timezone lệch 7h trong Schedule

### A.1 Nguyên nhân

```typescript
// ❌ VẤN ĐỀ:
const dt = new Date(`${scheduleDate}T${scheduleTime}`); // → local Date object
dt.toISOString()  // → UTC string "2026-05-22T08:16:00.000Z"
// Backend nhận "08:16:00" → lưu sai, phải là "15:16:00"

// ✅ FIX: format thành local string, không convert sang UTC
```

### A.2 Thêm vào `src/lib/formatters.ts`

Mở file. Thêm vào **cuối file** (KHÔNG thay đổi gì cũ):

```typescript
/**
 * Format Date thành local ISO string KHÔNG có timezone suffix
 * Dùng khi gửi datetime lên backend (backend lưu DateTime.Now local UTC+7)
 * Tránh dùng .toISOString() vì nó convert sang UTC làm lệch 7h
 */
export function toLocalISOString(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
    + `T${pad(date.getHours())}:${pad(date.getMinutes())}:00`;
}
```

### A.3 Sửa `ScreenshotModal.tsx` — scheduleMutation

```typescript
// Thêm import:
import { normalizeBackendDate, toLocalISOString } from '@/lib/formatters';
// (hoặc relative path đúng với project)

// Trong scheduleMutation.mutationFn:
// ❌ CŨ:
const dt = new Date(`${scheduleDate}T${scheduleTime}`);
return scheduleScreenshot(childId, domain, dt.toISOString());

// ✅ MỚI:
const dt = new Date(`${scheduleDate}T${scheduleTime}`);
return scheduleScreenshot(childId, domain, toLocalISOString(dt));
```

### A.4 Fix hiển thị scheduledAt trong danh sách chờ

```tsx
// ❌ CŨ (không có normalize):
new Date(s.scheduledAt).toLocaleString('vi-VN')

// ✅ MỚI (dùng normalizeBackendDate đã có sẵn):
normalizeBackendDate(s.scheduledAt).toLocaleString('vi-VN')
```

---

## PHẦN B — Redesign Schedule UI (Inline + TimeInput24h)

### B.1 Thay đổi so với Guide 15

| Guide 15 | Guide 16 (mới) |
|----------|----------------|
| Nút toggle "Hẹn giờ" trong header | Bỏ nút toggle |
| Panel collapsible (ẩn/hiện) | Section inline, luôn hiển thị |
| `<input type="time">` | `<TimeInput24h>` (giống Khung giờ cho phép) |
| `showSchedule` state | Bỏ state này |
| Pre-fill khi click nút | Pre-fill khi modal mount (`useEffect`) |

### B.2 Sửa `ScreenshotModal.tsx`

#### B.2.1 — Xóa state showSchedule

```typescript
// XÓA dòng này:
const [showSchedule, setShowSchedule] = useState(false);
```

#### B.2.2 — Thêm import TimeInput24h

```typescript
import { TimeInput24h } from '@/components/ui/TimeInput24h';
// hoặc relative path đúng — kiểm tra cách import trong AddWebsiteModal.tsx
// AddWebsiteModal dùng: import { TimeInput24h } from '../ui/TimeInput24h';
// → trong ScreenshotModal dùng path tương tự
```

#### B.2.3 — Pre-fill ngày giờ khi modal mount

```typescript
// Thêm useEffect ngay sau khai báo state scheduleDate/scheduleTime:
useEffect(() => {
  const now = new Date();
  setScheduleDate(now.toISOString().slice(0, 10)); // YYYY-MM-DD (date only, không timezone issue)
  setScheduleTime(now.toTimeString().slice(0, 5));  // HH:mm
}, []); // chỉ chạy 1 lần khi mount
```

#### B.2.4 — Sửa nút Header (bỏ nút Hẹn giờ toggle)

```tsx
{/* Header chỉ còn Chụp ngay + Đóng */}
<div className="flex items-center gap-2">
  {/* NÚT CHỤP NGAY — giữ nguyên từ Guide 15 */}
  <button
    onClick={() => requestMutation.mutate()}
    disabled={requestMutation.isPending || isTakingShot}
    className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-medium
               bg-brand-DEFAULT text-white hover:bg-brand-DEFAULT/90
               transition-colors disabled:opacity-60 disabled:cursor-not-allowed"
  >
    {isTakingShot ? (
      <>
        <svg className="w-3.5 h-3.5 animate-spin" fill="none" viewBox="0 0 24 24">
          <circle className="opacity-25" cx="12" cy="12" r="10"
                  stroke="currentColor" strokeWidth="4"/>
          <path className="opacity-75" fill="currentColor"
                d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"/>
        </svg>
        Đang chụp...
      </>
    ) : (
      <>
        <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
            d="M3 9a2 2 0 012-2h.93a2 2 0 001.664-.89l.812-1.22A2 2 0 0110.07 4h3.86a2 2 0 011.664.89l.812 1.22A2 2 0 0018.07 7H19a2 2 0 012 2v9a2 2 0 01-2 2H5a2 2 0 01-2-2V9z"/>
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                d="M15 13a3 3 0 11-6 0 3 3 0 016 0z"/>
        </svg>
        Chụp ngay
      </>
    )}
  </button>

  {/* NÚT ĐÓNG — giữ nguyên */}
  <button onClick={onClose} className="p-1.5 rounded-lg text-tx-secondary
    hover:text-tx-primary hover:bg-bg-subtle transition-colors">
    <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
            d="M6 18L18 6M6 6l12 12"/>
    </svg>
  </button>
</div>
```

#### B.2.5 — Section hẹn giờ inline (thay panel collapsible)

Thay toàn bộ đoạn `{showSchedule && (...)}` bằng section cố định dưới đây.
Đặt **ngay sau Header**, trước filter bar:

```tsx
{/* ── Section Hẹn giờ — luôn hiển thị, không cần toggle ── */}
<div className="px-5 py-3 border-b border-border-base bg-bg-subtle/40 shrink-0">
  <p className="text-[10px] font-bold uppercase tracking-widest text-tx-secondary mb-2.5">
    Hẹn giờ chụp tự động
  </p>

  <div className="flex items-end gap-3 flex-wrap">
    {/* Chọn ngày */}
    <div className="flex flex-col gap-1">
      <label className="text-[10px] font-bold uppercase tracking-widest text-tx-secondary">
        Ngày
      </label>
      <input
        type="date"
        value={scheduleDate}
        min={new Date().toISOString().slice(0, 10)}
        onChange={e => setScheduleDate(e.target.value)}
        className="h-9 px-3 rounded-xl text-xs border border-border-base
                   bg-bg-surface text-tx-primary focus:outline-none
                   focus:border-brand-DEFAULT transition-colors
                   dark:[color-scheme:dark]"
      />
    </div>

    {/* Chọn giờ — dùng TimeInput24h giống Khung giờ cho phép */}
    <div className="flex flex-col gap-1">
      <TimeInput24h
        label="Giờ chụp"
        value={scheduleTime}
        onChange={setScheduleTime}
      />
    </div>

    {/* Nút xác nhận */}
    <button
      onClick={() => scheduleMutation.mutate()}
      disabled={!scheduleDate || !scheduleTime || scheduleMutation.isPending}
      className="h-9 flex items-center gap-1.5 px-4 rounded-xl text-xs font-bold
                 bg-brand-DEFAULT/10 text-brand-DEFAULT border border-brand-DEFAULT/25
                 hover:bg-brand-DEFAULT/20 transition-colors
                 disabled:opacity-40 disabled:cursor-not-allowed"
    >
      <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
              d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z"/>
      </svg>
      {scheduleMutation.isPending ? 'Đang hẹn...' : 'Hẹn giờ'}
    </button>
  </div>

  {/* Danh sách lịch đang chờ */}
  {scheduled.length > 0 && (
    <div className="mt-3 space-y-1.5">
      <p className="text-[10px] font-bold uppercase tracking-widest text-tx-secondary">
        Lịch đang chờ
      </p>
      {scheduled.map(s => (
        <div key={s.id}
             className="flex items-center justify-between px-3 py-1.5
                        rounded-xl bg-bg-surface border border-border-base">
          <span className="text-xs text-tx-primary">
            📅 {normalizeBackendDate(s.scheduledAt).toLocaleString('vi-VN', {
              day: '2-digit', month: '2-digit',
              hour: '2-digit', minute: '2-digit'
            })}
          </span>
          <button
            onClick={() => cancelScheduleMutation.mutate(s.id)}
            className="text-[11px] text-tx-secondary hover:text-red-400
                       transition-colors ml-3"
          >
            Hủy
          </button>
        </div>
      ))}
    </div>
  )}
</div>
```

---

## PHẦN C — AI Nhận diện 18+ (Google Cloud Vision SafeSearch)

**Chọn: Google Cloud Vision SafeSearch**
- ✅ Free 1.000 ảnh/tháng mãi mãi (không trial, không hết hạn)
- ✅ Không tốn CPU/RAM server — chỉ HTTP call ra ngoài
- ✅ Detect: adult, violence, racy
- ✅ **Tự động khóa khi đạt 1.000 ảnh/tháng → reset đầu tháng sau** (đề phòng bị trừ tiền)
- 💲 Vượt 1.000 nếu không khóa: $1.50/1.000 ảnh

**Logic đếm usage:** Đếm trực tiếp từ `ai_checked_at` trong `website_screenshots` — không cần bảng riêng. Đầu tháng mới tự reset vì query theo tháng hiện tại.

### C.1 Setup Google Cloud Vision (đã xong C.1)

1. Vào https://console.cloud.google.com → tạo project
2. Enable **Cloud Vision API**
3. **APIs & Services → Credentials → Create Credentials → API Key**
4. Restrict key: chỉ cho Cloud Vision API
5. Copy API key

### C.2 Thêm config

> ⚠️ `appsettings.Development.json` nằm trong `.gitignore` → đây là file chứa config thật (API key thật).
> `appsettings.json` chỉ là file mẫu commit lên git → chỉ ghi placeholder, không ghi key thật.

**Thêm vào `appsettings.Development.json`** (file thật, không commit):

```json
{
  "ConnectionStrings": { ... },
  "Jwt": { ... },
  "Google": { ... },

  "ContentModeration": {
    "Enabled": false,
    "GoogleVisionApiKey": "API_KEY_THẬT_CỦA_BẠN_Ở_ĐÂY",
    "FlagThreshold": "LIKELY",
    "MonthlyLimit": 1000
  }
}
```

**Thêm vào `appsettings.json`** (file mẫu, commit lên git — chỉ ghi placeholder):

```json
{
  "ConnectionStrings": { ... },
  "Jwt": { ... },
  "Google": { ... },

  "ContentModeration": {
    "Enabled": false,
    "GoogleVisionApiKey": "",
    "FlagThreshold": "LIKELY",
    "MonthlyLimit": 1000
  }
}
```

> `Enabled: false` mặc định — bật thủ công khi muốn dùng (`true`).
> `FlagThreshold`: `LIKELY` (khuyến nghị) hoặc `POSSIBLE` (nhạy hơn, nhiều false positive hơn).
> `MonthlyLimit`: 1000 — khi đạt giới hạn tự động skip, sang tháng mới tự reset.

### C.3 Thêm 3 field vào `WebsiteScreenshot.cs` entity

```csharp
// Thêm vào class WebsiteScreenshot (KHÔNG thay đổi field hiện có):
[Column("ai_moderation_status")]
[MaxLength(20)]
public string? AiModerationStatus { get; set; }
// Values: "safe" | "flagged" | "error" | "skipped"
// "skipped" = đã đạt giới hạn tháng, không gọi API

[Column("ai_adult_score")]
[MaxLength(20)]
public string? AiAdultScore { get; set; }

[Column("ai_checked_at")]
public DateTime? AiCheckedAt { get; set; }
```

### C.4 Tạo `Services/ContentModerationService.cs`

```csharp
using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Hubs;
using FamilyGuardian.Api.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace FamilyGuardian.Api.Services;

public interface IContentModerationService
{
    Task CheckAndNotifyAsync(int screenshotId);
    Task<ContentModerationUsageDto> GetMonthlyUsageAsync();
}

public class ContentModerationUsageDto
{
    public int UsedThisMonth { get; set; }
    public int MonthlyLimit { get; set; }
    public bool IsLimitReached { get; set; }
    public string ResetDate { get; set; } = string.Empty;
}

public class ContentModerationService : IContentModerationService
{
    private static readonly string[] Levels =
        ["VERY_UNLIKELY", "UNLIKELY", "POSSIBLE", "LIKELY", "VERY_LIKELY"];

    private readonly AppDbContext _context;
    private readonly IHubContext<NotificationHub> _hub;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ContentModerationService> _logger;

    public ContentModerationService(
        AppDbContext context,
        IHubContext<NotificationHub> hub,
        IHttpClientFactory http,
        IConfiguration config,
        IWebHostEnvironment env,
        ILogger<ContentModerationService> logger)
    {
        _context = context;
        _hub     = hub;
        _http    = http;
        _config  = config;
        _env     = env;
        _logger  = logger;
    }

    // ── Đếm số lần gọi API trong tháng hiện tại ──
    private async Task<int> GetMonthlyCheckCountAsync()
    {
        var now        = DateTime.Now;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var monthEnd   = monthStart.AddMonths(1);

        // Chỉ đếm các lần thực sự gọi API (bỏ qua "error" và "skipped")
        return await _context.WebsiteScreenshots
            .CountAsync(s => s.AiCheckedAt >= monthStart
                          && s.AiCheckedAt <  monthEnd
                          && s.AiModerationStatus != "error"
                          && s.AiModerationStatus != "skipped");
    }

    public async Task<ContentModerationUsageDto> GetMonthlyUsageAsync()
    {
        var limit = _config.GetValue<int>("ContentModeration:MonthlyLimit", 1000);
        var used  = await GetMonthlyCheckCountAsync();
        var reset = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1).AddMonths(1);

        return new ContentModerationUsageDto
        {
            UsedThisMonth  = used,
            MonthlyLimit   = limit,
            IsLimitReached = used >= limit,
            ResetDate      = reset.ToString("dd/MM/yyyy")
        };
    }

    public async Task CheckAndNotifyAsync(int screenshotId)
    {
        if (!_config.GetValue<bool>("ContentModeration:Enabled")) return;

        var apiKey = _config["ContentModeration:GoogleVisionApiKey"];
        if (string.IsNullOrEmpty(apiKey)) return;

        var shot = await _context.WebsiteScreenshots.FindAsync(screenshotId);
        if (shot == null || shot.Status != "captured") return;

        var limit     = _config.GetValue<int>("ContentModeration:MonthlyLimit", 1000);
        var threshold = _config["ContentModeration:FlagThreshold"] ?? "LIKELY";

        // ── Kiểm tra giới hạn tháng TRƯỚC khi gọi API ──
        var usedThisMonth = await GetMonthlyCheckCountAsync();
        if (usedThisMonth >= limit)
        {
            var reset = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1).AddMonths(1);
            _logger.LogWarning(
                "ContentModeration limit reached ({Used}/{Limit}). " +
                "Screenshot {Id} skipped. Resets {Reset:dd/MM/yyyy}.",
                usedThisMonth, limit, screenshotId, reset);

            // Đánh dấu skipped — không gọi API → không tốn quota → không bị trừ tiền
            shot.AiModerationStatus = "skipped";
            shot.AiCheckedAt        = DateTime.Now;
            await _context.SaveChangesAsync();
            return;
        }

        // ── Còn quota → gọi Google Vision ──
        try
        {
            var baseDir  = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
            var fullPath = Path.Combine(baseDir, shot.ImagePath.TrimStart('/'));
            if (!File.Exists(fullPath))
            {
                shot.AiModerationStatus = "error";
                await _context.SaveChangesAsync();
                return;
            }

            var imageBytes = await File.ReadAllBytesAsync(fullPath);
            var base64     = Convert.ToBase64String(imageBytes);

            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            var payload = new
            {
                requests = new[]
                {
                    new
                    {
                        image    = new { content = base64 },
                        features = new[] { new { type = "SAFE_SEARCH_DETECTION" } }
                    }
                }
            };

            var resp = await client.PostAsJsonAsync(
                $"https://vision.googleapis.com/v1/images:annotate?key={apiKey}",
                payload);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Google Vision API error: {Status}", resp.StatusCode);
                shot.AiModerationStatus = "error";
                await _context.SaveChangesAsync();
                return;
            }

            var json = await resp.Content.ReadAsStringAsync();
            var doc  = JsonDocument.Parse(json);
            var ss   = doc.RootElement
                          .GetProperty("responses")[0]
                          .GetProperty("safeSearchAnnotation");

            var adult    = ss.GetProperty("adult").GetString()    ?? "UNKNOWN";
            var violence = ss.GetProperty("violence").GetString() ?? "UNKNOWN";
            var racy     = ss.GetProperty("racy").GetString()     ?? "UNKNOWN";

            bool IsFlagged(string level) =>
                Array.IndexOf(Levels, level) >= Array.IndexOf(Levels, threshold);

            var flagged = IsFlagged(adult) || IsFlagged(violence) || IsFlagged(racy);

            shot.AiModerationStatus = flagged ? "flagged" : "safe";
            shot.AiAdultScore       = adult;
            shot.AiCheckedAt        = DateTime.Now;
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "ContentModeration: Id={Id}, Status={Status}, Adult={Adult}, Usage={Used}/{Limit}",
                screenshotId, shot.AiModerationStatus, adult, usedThisMonth + 1, limit);

            // ── Log cảnh báo khi gần đạt giới hạn (90%) ──
            if (usedThisMonth + 1 >= (int)(limit * 0.9))
            {
                _logger.LogWarning(
                    "ContentModeration approaching monthly limit: {Used}/{Limit}. " +
                    "Will auto-block at {Limit}.",
                    usedThisMonth + 1, limit, limit);
            }

            if (!flagged) return;

            // ── Tạo notification cho guardian ──
            var notif = new Notification
            {
                GuardianId = shot.RequestedBy,
                ChildId    = shot.ChildId,
                Title      = "⚠️ Nội dung không phù hợp",
                Message    = $"Ảnh chụp từ {shot.Domain} có thể chứa nội dung 18+. Nhấn để xem.",
                Type       = "content_warning",
                IsRead     = false,
                CreatedAt  = DateTime.Now,
                SentAt     = DateTime.Now
            };
            _context.Notifications.Add(notif);
            await _context.SaveChangesAsync();

            await _hub.Clients.Group($"guardian_{shot.RequestedBy}")
                .SendAsync("ContentWarning", new
                {
                    screenshotId = shot.Id,
                    childId      = shot.ChildId,
                    domain       = shot.Domain,
                    adultScore   = adult,
                    imageUrl     = $"/{shot.ImagePath}"
                });

            _logger.LogWarning(
                "Content flagged: Id={Id}, Domain={Domain}, Adult={Adult}",
                screenshotId, shot.Domain, adult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ContentModeration error for screenshot {Id}", screenshotId);
            shot.AiModerationStatus = "error";
            await _context.SaveChangesAsync();
        }
    }
}
```

### C.5 Gọi sau khi lưu ảnh thành công

Mở `ScreenshotService.cs`. Tìm `SaveScreenshotAsync`.
Inject `IContentModerationService` vào constructor (KHÔNG thay đổi gì khác):

```csharp
// Thêm field:
private readonly IContentModerationService _moderation;
// Thêm vào constructor parameter + gán: _moderation = moderation;

// Trong SaveScreenshotAsync — sau dòng await _context.SaveChangesAsync() thành công:
// Fire-and-forget: tự kiểm tra quota trước khi gọi API, không block response
_ = Task.Run(() => _moderation.CheckAndNotifyAsync(screenshot.Id));
```

### C.6 Endpoint xem usage (tùy chọn)

Thêm vào controller phù hợp:

```csharp
// GET /api/content-moderation/usage
[HttpGet("usage")]
[Authorize(Roles = "guardian,admin")]
public async Task<IActionResult> GetModerationUsage()
{
    var usage = await _moderationService.GetMonthlyUsageAsync();
    return Ok(usage);
    // Trả về: { usedThisMonth: 234, monthlyLimit: 1000, isLimitReached: false, resetDate: "01/06/2026" }
}
```

### C.7 Đăng ký trong `Program.cs`

```csharp
builder.Services.AddHttpClient();  // nếu chưa có
builder.Services.AddScoped<IContentModerationService, ContentModerationService>();
```

### C.8 Frontend: ScreenshotDto + Badge 18+

Thêm vào `ScreenshotDto` trong `childrenApi.ts`:
```typescript
aiModerationStatus?: 'safe' | 'flagged' | 'error' | 'skipped' | null;
aiAdultScore?: string | null;
```

Thêm badge vào thumbnail trong `ScreenshotModal.tsx`:
```tsx
{/* Badge 18+ khi bị flag */}
{s.aiModerationStatus === 'flagged' && (
  <div className="absolute top-1.5 left-1.5 px-1.5 py-0.5 rounded-md
                  bg-red-500 text-white text-[10px] font-bold z-10 shadow-lg">
    ⚠️ 18+
  </div>
)}
```

### C.9 Frontend: SignalR listener ContentWarning

Mở hook SignalR Guardian. Thêm listener (KHÔNG thay đổi gì cũ):

```typescript
connection.on("ContentWarning", (data: {
  screenshotId: number;
  childId: number;
  domain: string;
  adultScore: string;
}) => {
  toast.warning(`⚠️ Phát hiện nội dung 18+ trong ảnh từ ${data.domain}!`);
  queryClient.invalidateQueries({
    queryKey: ['screenshots', data.childId, data.domain]
  });
});
```

---

## PHẦN D — Dark Mode / Light Mode Audit

### D.1 Input date/time trong ScreenshotModal

Tất cả `<input type="date">` và `<input type="time">` phải có `dark:[color-scheme:dark]`:

```tsx
// ✅ Bắt buộc cho date/time input để browser render đúng dark mode:
className="... dark:[color-scheme:dark]"
```

### D.2 Pattern sai → đúng (kiểm tra tất cả file component)

```tsx
// ❌ SAI:
"bg-white"          → "bg-bg-surface"
"bg-gray-50"        → "bg-bg-subtle"
"bg-gray-100"       → "bg-bg-muted"
"text-gray-900"     → "text-tx-primary"
"text-gray-500"     → "text-tx-secondary"
"text-gray-400"     → "text-tx-muted"
"border-gray-200"   → "border-border-base"
"border-gray-300"   → "border-border-strong"

// ❌ Dark-only hardcode (sẽ sai ở light mode):
"dark:bg-gray-800"  → dùng CSS variable thay
"dark:text-white"   → dùng "text-tx-primary" thay
```

### D.3 Danh sách file cần scan

Mở từng file, dùng Ctrl+F tìm các string sai:

| File | Ưu tiên |
|------|---------|
| `ScreenshotModal.tsx` | Cao — mới tạo, có input date |
| `WebsiteCard.tsx` | Cao |
| `WarningConfigModal.tsx` | Cao |
| `EditWebsiteModal.tsx` | Trung bình — đã dùng variables tốt |
| `AddWebsiteModal.tsx` | Thấp — đã dùng variables tốt |
| Pages (`ChildDetailPage`, `NotificationsPage`...) | Trung bình |

### D.4 Transition mượt khi switch mode

Mở file layout chính (thường `AppLayout.tsx` hoặc `main.tsx`).
Tìm element root wrapper (thường `<div className="...">` bao toàn bộ app).
Thêm `transition-colors duration-200`:

```tsx
// AppLayout hoặc wrapper chính:
<div className="min-h-screen bg-bg-canvas transition-colors duration-200">
```

### D.5 Hiệu ứng card hover

Trong `WebsiteCard.tsx`, thêm shadow hover mượt (nếu chưa có):
```tsx
// Thêm vào className của card container:
"hover:shadow-md hover:shadow-black/8 dark:hover:shadow-black/25
 transition-all duration-200"
```

### D.6 Kiểm tra Sidebar

Mở Sidebar component. Đảm bảo không bị ảnh hưởng bởi light mode:
```tsx
// Sidebar phải dùng màu cố định tối (theo system prompt: "Sidebar giữ tối ở cả 2 mode")
// Nếu đang dùng CSS variable → kiểm tra trong light.css và dark.css
// sidebar variable phải có cùng giá trị tối ở cả 2 theme
```

---

## Thứ tự làm việc

```
A1  — Thêm toLocalISOString vào formatters.ts (cuối file)
A2  — Sửa scheduleMutation dùng toLocalISOString thay toISOString
A3  — Sửa hiển thị scheduledAt dùng normalizeBackendDate

B1  — Xóa state showSchedule trong ScreenshotModal
B2  — Thêm import TimeInput24h (dùng path giống AddWebsiteModal)
B3  — Thêm useEffect pre-fill ngày giờ khi mount
B4  — Bỏ nút "Hẹn giờ" toggle trong header
B5  — Thay section hẹn giờ thành inline với TimeInput24h
B6  — Test: mở modal → ngày giờ pre-filled → chọn giờ → hẹn → API nhận đúng

C1  — Chạy SQL ALTER TABLE website_screenshots
C2  — Thêm 3 field vào WebsiteScreenshot.cs
C3  — Tạo ContentModerationService.cs
C4  — Thêm config vào appsettings.json (Enabled: false)
C5  — Inject + gọi fire-and-forget trong SaveScreenshotAsync
C6  — AddHttpClient + đăng ký service trong Program.cs
C7  — Thêm fields vào ScreenshotDto + badge 18+ trong modal
C8  — Thêm SignalR listener ContentWarning trong hook Guardian

D1  — Scan ScreenshotModal.tsx → thêm dark:[color-scheme:dark] vào date input
D2  — Scan WebsiteCard.tsx, WarningConfigModal.tsx → sửa hardcode màu
D3  — Thêm transition-colors duration-200 vào root wrapper
D4  — Kiểm tra Sidebar giữ tối ở cả 2 mode
D5  — Test switch mode → transition mượt
```

---

## Checklist

### Timezone fix
- [ ] `toLocalISOString` không dùng UTC, format "YYYY-MM-DDTHH:mm:00"
- [ ] `scheduleMutation` dùng `toLocalISOString` thay `.toISOString()`
- [ ] Hiển thị scheduledAt dùng `normalizeBackendDate` (đã có trong formatters.ts)
- [ ] Test: chọn 3:16pm → API nhận "T15:16:00" → hiển thị đúng "15:16"

### Schedule UI
- [ ] Không còn nút toggle "Hẹn giờ" trong header
- [ ] Section hẹn giờ luôn hiển thị (inline)
- [ ] Time picker = `<TimeInput24h>` (giống Khung giờ cho phép)
- [ ] Pre-fill ngày giờ hiện tại khi modal mở

### AI Moderation
- [ ] `Enabled: false` trong appsettings.json mặc định
- [ ] Fire-and-forget — không block upload response
- [ ] Badge ⚠️ 18+ hiện trên thumbnail bị flag
- [ ] Toast warning khi nhận SignalR ContentWarning

### Dark Mode
- [ ] date input có `dark:[color-scheme:dark]`
- [ ] Không còn `bg-white`, `text-gray-*`, `border-gray-*` hardcode
- [ ] Transition mượt 200ms khi switch mode
- [ ] Sidebar giữ tối cả 2 mode

---

## Test

```
TEST A — Timezone
Hẹn giờ 3:16pm → API payload: "2026-05-22T15:16:00" (không có Z/UTC)
Danh sách chờ hiện: "15:16" đúng (không phải 08:16)

TEST B — Schedule UI
Mở modal → section hẹn giờ hiện ngay không cần click
→ Ngày = hôm nay, giờ = giờ hiện tại (pre-filled)
→ Time picker giống hệt trong "Thêm website / Khung giờ cho phép"

TEST C — AI (sau khi bật Enabled: true + thêm API key)
Chụp ảnh bình thường → log: AiModerationStatus = "safe"
Verify API call thành công (không crash backend)
Không test ảnh 18+ thật

TEST D — Dark/Light
Switch mode → transition mượt 200ms
Dark mode: không có element trắng bất thường
Light mode: không có element tối bất thường
Date input: hiển thị đúng màu trong cả 2 mode
```
