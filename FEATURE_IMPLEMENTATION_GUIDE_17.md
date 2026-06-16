# Family Guardian — AI Toggle UI + Schedule Toggle + CSS/Responsive Fix (Phần 17)

> **Ngày tạo:** 2026-05-26
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE_16.md (Phần 16)

---

## ⚠️ Quy tắc bất di bất dịch

| Quy tắc | Chi tiết |
|---------|---------|
| `sp_ExtensionCheckAccess` | Tuyệt đối KHÔNG sửa |
| background.js logic | KHÔNG thay đổi |
| Logic giới hạn 1.000 ảnh/tháng | KHÔNG thay đổi, chỉ thêm UI phản ánh |
| Logic fire-and-forget AI check | KHÔNG thay đổi |
| Dark mode | CSS variables: `bg-bg-surface`, `text-tx-primary`, `border-border-base`, `brand-DEFAULT` |

---

## SQL cần chạy (đã làm xong)

```sql
-- Bảng lưu cài đặt runtime (bật/tắt AI, v.v.)
CREATE TABLE app_settings (
  `key`       VARCHAR(100) NOT NULL PRIMARY KEY,
  `value`     VARCHAR(500) NOT NULL,
  updated_at  DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP
                           ON UPDATE CURRENT_TIMESTAMP,
  updated_by  INT NULL,
  CONSTRAINT fk_appsettings_user FOREIGN KEY (updated_by) REFERENCES users(id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Giá trị mặc định
INSERT INTO app_settings (`key`, `value`) VALUES ('content_moderation_enabled', 'false');
```

> ✅ Sau khi chạy SQL → note "đã chạy" rồi mới làm backend.

---

## PHẦN A — Backend: API bật/tắt AI từ Frontend

### A.1 Thêm `AppSetting` entity

Tạo `Models/AppSetting.cs`:

```csharp
using System.ComponentModel.DataAnnotations.Schema;

namespace FamilyGuardian.Api.Models;

[Table("app_settings")]
public class AppSetting
{
    [Column("key")]
    public string Key { get; set; } = string.Empty;

    [Column("value")]
    public string Value { get; set; } = string.Empty;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    [Column("updated_by")]
    public int? UpdatedBy { get; set; }
}
```

### A.2 Thêm DbSet vào `AppDbContext.cs`

```csharp
public DbSet<AppSetting> AppSettings { get; set; }
```

### A.3 Sửa `ContentModerationService.cs` — đọc Enabled từ DB thay config

Mở `ContentModerationService.cs`. Tìm:

```csharp
if (!_config.GetValue<bool>("ContentModeration:Enabled")) return;
```

Thay bằng (đọc từ DB, fallback về config):

```csharp
// Đọc trạng thái từ DB (ưu tiên) → fallback về appsettings
var dbSetting = await _context.AppSettings
    .AsNoTracking()
    .FirstOrDefaultAsync(s => s.Key == "content_moderation_enabled");

var enabled = dbSetting != null
    ? dbSetting.Value == "true"
    : _config.GetValue<bool>("ContentModeration:Enabled");

if (!enabled) return;
```

Tương tự cập nhật `GetMonthlyUsageAsync` để trả thêm trạng thái enabled:

```csharp
public async Task<ContentModerationUsageDto> GetMonthlyUsageAsync()
{
    var limit = _config.GetValue<int>("ContentModeration:MonthlyLimit", 1000);
    var used  = await GetMonthlyCheckCountAsync();
    var reset = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1).AddMonths(1);

    var dbSetting = await _context.AppSettings
        .AsNoTracking()
        .FirstOrDefaultAsync(s => s.Key == "content_moderation_enabled");
    var enabled = dbSetting != null
        ? dbSetting.Value == "true"
        : _config.GetValue<bool>("ContentModeration:Enabled");

    return new ContentModerationUsageDto
    {
        UsedThisMonth  = used,
        MonthlyLimit   = limit,
        IsLimitReached = used >= limit,
        IsEnabled      = enabled,          // ← THÊM field này
        ResetDate      = reset.ToString("dd/MM/yyyy")
    };
}
```

Thêm `IsEnabled` vào `ContentModerationUsageDto`:

```csharp
public class ContentModerationUsageDto
{
    public int UsedThisMonth { get; set; }
    public int MonthlyLimit { get; set; }
    public bool IsLimitReached { get; set; }
    public bool IsEnabled { get; set; }    // ← THÊM
    public string ResetDate { get; set; } = string.Empty;
}
```

### A.4 Thêm interface method `ToggleAsync`

Thêm vào `IContentModerationService`:

```csharp
Task<ContentModerationUsageDto> ToggleAsync(int adminId, bool enabled);
```

Implement trong `ContentModerationService`:

```csharp
public async Task<ContentModerationUsageDto> ToggleAsync(int adminId, bool enabled)
{
    var limit = _config.GetValue<int>("ContentModeration:MonthlyLimit", 1000);
    var used  = await GetMonthlyCheckCountAsync();

    // Không cho bật nếu đã đạt giới hạn tháng
    if (enabled && used >= limit)
    {
        var reset = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1).AddMonths(1);
        return new ContentModerationUsageDto
        {
            UsedThisMonth  = used,
            MonthlyLimit   = limit,
            IsLimitReached = true,
            IsEnabled      = false,
            ResetDate      = reset.ToString("dd/MM/yyyy")
        };
    }

    var setting = await _context.AppSettings
        .FirstOrDefaultAsync(s => s.Key == "content_moderation_enabled");

    if (setting == null)
    {
        setting = new AppSetting { Key = "content_moderation_enabled" };
        _context.AppSettings.Add(setting);
    }

    setting.Value     = enabled ? "true" : "false";
    setting.UpdatedAt = DateTime.Now;
    setting.UpdatedBy = adminId;
    await _context.SaveChangesAsync();

    _logger.LogInformation(
        "ContentModeration toggled to {State} by user {UserId}",
        enabled ? "ON" : "OFF", adminId);

    return await GetMonthlyUsageAsync();
}
```

### A.5 Thêm endpoints vào `SettingsController.cs` (tạo mới nếu chưa có)

Kiểm tra đã có `SettingsController` chưa. Nếu chưa → tạo file `Controllers/SettingsController.cs`:

```csharp
using FamilyGuardian.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FamilyGuardian.Api.Controllers;

[ApiController]
[Route("api/settings")]
[Authorize(Roles = "guardian,admin")]
public class SettingsController : ControllerBase
{
    private readonly IContentModerationService _moderation;

    public SettingsController(IContentModerationService moderation)
    {
        _moderation = moderation;
    }

    // GET /api/settings/content-moderation
    // Lấy trạng thái + usage tháng hiện tại
    [HttpGet("content-moderation")]
    public async Task<IActionResult> GetModerationStatus()
    {
        var usage = await _moderation.GetMonthlyUsageAsync();
        return Ok(usage);
    }

    // PATCH /api/settings/content-moderation
    // Bật/tắt AI moderation
    [HttpPatch("content-moderation")]
    public async Task<IActionResult> ToggleModeration([FromBody] ToggleModerationDto dto)
    {
        // Dùng đúng cách lấy userId hiện tại của project
        var userId = GetCurrentUserId();
        var result = await _moderation.ToggleAsync(userId, dto.Enabled);
        return Ok(result);
    }

    // DTO
    public class ToggleModerationDto
    {
        public bool Enabled { get; set; }
    }

    // Thêm GetCurrentUserId() — dùng đúng cách project đang dùng
    private int GetCurrentUserId()
    {
        var claim = User.FindFirst("sub") ?? User.FindFirst("userId");
        return int.TryParse(claim?.Value, out var id) ? id : 0;
    }
}
```

---

## PHẦN B — Frontend: Toggle AI + Usage trong ScreenshotModal

### B.1 Thêm API functions vào `childrenApi.ts` (hoặc tạo `settingsApi.ts`)

```typescript
// src/api/settingsApi.ts (tạo file mới)
import api from './axiosInstance'; // dùng đúng axios instance của project

export interface ContentModerationUsage {
  usedThisMonth: number;
  monthlyLimit: number;
  isLimitReached: boolean;
  isEnabled: boolean;
  resetDate: string;
}

export const getModerationStatus = async (): Promise<ContentModerationUsage> => {
  const res = await api.get<ContentModerationUsage>('/settings/content-moderation');
  return res.data;
};

export const toggleModeration = async (enabled: boolean): Promise<ContentModerationUsage> => {
  const res = await api.patch<ContentModerationUsage>(
    '/settings/content-moderation',
    { enabled }
  );
  return res.data;
};
```

### B.2 Thêm vào `ScreenshotModal.tsx`

#### B.2.1 — Thêm query + mutation (KHÔNG thay đổi gì cũ)

```typescript
import { getModerationStatus, toggleModeration } from '@/api/settingsApi';

// Trong component:
const { data: moderationStatus, refetch: refetchModeration } = useQuery({
  queryKey: ['moderation-status'],
  queryFn: getModerationStatus,
  staleTime: 30000,
});

const toggleModerationMutation = useMutation({
  mutationFn: (enabled: boolean) => toggleModeration(enabled),
  onSuccess: (data) => {
    refetchModeration();
    if (data.isLimitReached && !data.isEnabled) {
      toast.warning(
        `Đã đạt ${data.monthlyLimit} ảnh/tháng. Tính năng AI tự động khóa. Reset ngày ${data.resetDate}.`
      );
    } else {
      toast.success(data.isEnabled ? 'Đã bật nhận diện nội dung 18+' : 'Đã tắt nhận diện nội dung 18+');
    }
  },
  onError: () => toast.error('Không thể thay đổi cài đặt'),
});
```

#### B.2.2 — Thêm UI trong header modal (cạnh nút Chụp ngay)

Thêm vào khu vực header, trước nút Chụp ngay:

```tsx
{/* AI Moderation Toggle */}
<div className="flex items-center gap-2 mr-1">
  {moderationStatus && (
    <div className="flex items-center gap-1.5">
      {/* Progress usage */}
      <span className={`text-[10px] font-medium hidden sm:block
        ${moderationStatus.isLimitReached
          ? 'text-red-400'
          : moderationStatus.usedThisMonth >= moderationStatus.monthlyLimit * 0.9
            ? 'text-yellow-400'
            : 'text-tx-secondary'
        }`}>
        {moderationStatus.isLimitReached
          ? `⚠️ ${moderationStatus.usedThisMonth}/${moderationStatus.monthlyLimit}`
          : `${moderationStatus.usedThisMonth}/${moderationStatus.monthlyLimit}`
        }
      </span>

      {/* Toggle button */}
      <button
        onClick={() => {
          if (moderationStatus.isLimitReached && !moderationStatus.isEnabled) {
            toast.warning(
              `Đã đạt ${moderationStatus.monthlyLimit} ảnh/tháng. Reset ngày ${moderationStatus.resetDate}.`
            );
            return;
          }
          toggleModerationMutation.mutate(!moderationStatus.isEnabled);
        }}
        disabled={toggleModerationMutation.isPending}
        title={
          moderationStatus.isLimitReached && !moderationStatus.isEnabled
            ? `Đã đạt giới hạn ${moderationStatus.monthlyLimit} ảnh/tháng. Reset ${moderationStatus.resetDate}`
            : moderationStatus.isEnabled
              ? 'Tắt nhận diện nội dung 18+'
              : 'Bật nhận diện nội dung 18+'
        }
        className={`flex items-center gap-1 px-2 py-1 rounded-lg text-[10px]
                   font-bold uppercase tracking-wide transition-all border
                   ${moderationStatus.isLimitReached && !moderationStatus.isEnabled
                     ? 'bg-red-500/10 text-red-400 border-red-500/20 cursor-not-allowed opacity-70'
                     : moderationStatus.isEnabled
                       ? 'bg-green-500/10 text-green-400 border-green-500/20 hover:bg-green-500/20'
                       : 'bg-bg-subtle text-tx-secondary border-border-base hover:text-tx-primary'
                   }`}
      >
        {/* Icon */}
        <svg className="w-3 h-3 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
            d="M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17H4a2 2 0 01-2-2V5a2 2 0 012-2h16a2 2 0 012 2v10a2 2 0 01-2 2h-1"/>
        </svg>
        <span className="hidden sm:inline">
          {moderationStatus.isLimitReached && !moderationStatus.isEnabled
            ? 'Đã khóa'
            : moderationStatus.isEnabled ? 'AI ON' : 'AI OFF'
          }
        </span>
      </button>
    </div>
  )}
</div>
```

---

## PHẦN C — Thêm nút Toggle Hẹn giờ trong Header

Người dùng muốn ẩn/hiện section hẹn giờ bằng nút trong header.

### C.1 Thêm lại state `showSchedule`

```typescript
// Thêm lại state này (đã bị xóa ở Guide 16):
const [showSchedule, setShowSchedule] = useState(false);
```

### C.2 Sửa section hẹn giờ — bọc bằng conditional

```tsx
{/* Section hẹn giờ — hiện/ẩn theo showSchedule */}
{showSchedule && (
  <div className="px-5 py-3 border-b border-border-base bg-bg-subtle/40 shrink-0">
    {/* Giữ nguyên nội dung section từ Guide 16 */}
    ...
  </div>
)}
```

### C.3 Thêm nút Hẹn giờ vào header

Thêm vào khu vực action buttons trong header (sau AI toggle, trước nút Chụp ngay):

```tsx
{/* Nút toggle Hẹn giờ */}
<button
  onClick={() => {
    if (!showSchedule) {
      // Pre-fill ngày giờ hiện tại khi mở
      const now = new Date();
      setScheduleDate(now.toISOString().slice(0, 10));
      setScheduleTime(now.toTimeString().slice(0, 5));
    }
    setShowSchedule(s => !s);
  }}
  className={`flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs
             font-medium transition-colors border
             ${showSchedule
               ? 'bg-brand-DEFAULT/15 text-brand-DEFAULT border-brand-DEFAULT/30'
               : 'bg-bg-subtle text-tx-secondary border-border-base hover:text-tx-primary'
             }`}
>
  <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
          d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z"/>
  </svg>
  <span className="hidden sm:inline">Hẹn giờ</span>
</button>
```

---

## PHẦN D — CSS Fix + Responsive

### D.1 Sửa Header Modal — responsive mobile

Header hiện tại có thể bị overflow trên mobile. Sửa layout header:

```tsx
{/* Header — responsive */}
<div className="flex items-center justify-between px-4 sm:px-5 py-3 sm:py-4
                border-b border-border-base shrink-0 gap-2 flex-wrap sm:flex-nowrap">
  {/* Left: title */}
  <div className="min-w-0">
    <h2 className="text-sm font-semibold text-tx-primary flex items-center gap-1.5 truncate">
      <svg className="w-4 h-4 text-brand-DEFAULT shrink-0" .../>
      Ảnh chụp màn hình
    </h2>
    <p className="text-xs text-tx-secondary mt-0.5 truncate">{websiteName}</p>
  </div>

  {/* Right: actions — scroll ngang trên mobile nếu cần */}
  <div className="flex items-center gap-1.5 sm:gap-2 shrink-0 flex-wrap justify-end">
    {/* AI Toggle */}
    {/* Nút Hẹn giờ */}
    {/* Nút Chụp ngay */}
    {/* Nút Đóng */}
  </div>
</div>
```

### D.2 Grid ảnh responsive

```tsx
{/* Grid: 1 cột mobile, 2 cột tablet, 3 cột desktop */}
<div className="grid grid-cols-1 xs:grid-cols-2 sm:grid-cols-2 md:grid-cols-3 gap-2 sm:gap-3">
  {capturedList.map(s => (
    <div className="relative rounded-xl overflow-hidden border border-border-base
                    cursor-pointer group hover:border-brand-DEFAULT/50
                    transition-all hover:shadow-lg aspect-video bg-bg-subtle">
      ...
    </div>
  ))}
</div>
```

### D.3 Section hẹn giờ responsive

```tsx
{/* Wrap flex items — stack dọc trên mobile */}
<div className="flex flex-col sm:flex-row sm:items-end gap-2 sm:gap-3 flex-wrap">
  {/* Ngày */}
  <div className="flex flex-col gap-1 flex-1 sm:flex-none">
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
                 dark:[color-scheme:dark] w-full sm:w-auto"
    />
  </div>

  {/* Giờ — TimeInput24h */}
  <div className="flex flex-col gap-1 flex-1 sm:flex-none">
    <TimeInput24h
      label="Giờ chụp"
      value={scheduleTime}
      onChange={setScheduleTime}
    />
  </div>

  {/* Nút hẹn */}
  <button
    onClick={() => scheduleMutation.mutate()}
    disabled={!scheduleDate || !scheduleTime || scheduleMutation.isPending}
    className="h-9 flex items-center justify-center gap-1.5 px-4 rounded-xl
               text-xs font-bold bg-brand-DEFAULT/10 text-brand-DEFAULT
               border border-brand-DEFAULT/25 hover:bg-brand-DEFAULT/20
               transition-colors disabled:opacity-40 disabled:cursor-not-allowed
               w-full sm:w-auto"
  >
    <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
            d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z"/>
    </svg>
    {scheduleMutation.isPending ? 'Đang hẹn...' : 'Hẹn giờ'}
  </button>
</div>
```

### D.4 Modal size responsive

```tsx
{/* Modal container */}
<div className="relative w-full max-w-3xl
                max-h-[100dvh] sm:max-h-[90vh]
                rounded-none sm:rounded-2xl overflow-hidden
                bg-bg-surface border-0 sm:border border-border-base
                shadow-2xl flex flex-col pointer-events-auto">
```

> `max-h-[100dvh]` trên mobile → fullscreen. `rounded-none` trên mobile → không có border radius. `sm:` → tablet/desktop bình thường.

### D.5 Filter bar responsive

```tsx
{/* Filter bar — scroll ngang trên mobile nếu nhiều chip */}
<div className="flex items-center gap-1.5 sm:gap-2 px-4 sm:px-5 py-2.5
                border-b border-border-base shrink-0
                overflow-x-auto scrollbar-none">
  {/* Giữ nguyên nội dung filter */}
  ...
</div>
```

### D.6 Dark mode — checklist các class cần kiểm tra trong ScreenshotModal

```tsx
// ✅ Đúng — dùng CSS variables:
bg-bg-surface          // card background
bg-bg-subtle           // section background nhạt hơn
bg-bg-subtle/40        // section hẹn giờ
text-tx-primary        // text chính
text-tx-secondary      // text phụ
border-border-base     // border
brand-DEFAULT          // accent color

// ❌ Sai — cần thay:
bg-white              → bg-bg-surface
bg-gray-*             → bg-bg-subtle / bg-bg-muted
text-gray-*           → text-tx-primary / text-tx-secondary
border-gray-*         → border-border-base
dark:bg-gray-*        → xóa, dùng CSS variable thay

// ✅ Input date/time — BẮT BUỘC:
dark:[color-scheme:dark]   // thiếu cái này thì input sáng trong dark mode

// ✅ Lightbox:
bg-black/92             // đúng cho cả 2 mode
```

### D.7 Scrollbar ẩn trên mobile

Thêm vào `tailwind.config.js` (nếu chưa có):

```javascript
plugins: [
  // Nếu không dùng plugin, thêm utility thủ công trong CSS:
],
```

Thêm vào global CSS (`index.css` hoặc `globals.css`):

```css
.scrollbar-none {
  scrollbar-width: none;
  -ms-overflow-style: none;
}
.scrollbar-none::-webkit-scrollbar {
  display: none;
}
```

---

## PHẦN E — Extension (chỉ verify)

Mở `background.js`. Verify các logic đang chạy:
- `screenshot_poll` alarm → KHÔNG thay đổi
- `captureScreenshotForDomain` → KHÔNG thay đổi
- `normalizeAccessReason` → KHÔNG thay đổi

Không có thay đổi nào ở extension trong phần này.

---

## Thứ tự làm việc

```
A1  — Chạy SQL tạo bảng app_settings + insert giá trị mặc định
A2  — Tạo AppSetting.cs entity
A3  — Thêm DbSet AppSettings vào AppDbContext
A4  — Sửa ContentModerationService: đọc Enabled từ DB
A5  — Thêm IsEnabled vào ContentModerationUsageDto
A6  — Thêm ToggleAsync vào interface + implement
A7  — Tạo SettingsController.cs với 2 endpoints

B1  — Tạo src/api/settingsApi.ts
B2  — Thêm useQuery + useMutation cho moderation status vào ScreenshotModal
B3  — Thêm UI AI Toggle vào header modal

C1  — Thêm lại state showSchedule
C2  — Bọc section hẹn giờ bằng {showSchedule && (...)}
C3  — Thêm nút Hẹn giờ vào header với pre-fill logic

D1  — Sửa header modal responsive (flex-wrap, gap)
D2  — Sửa grid ảnh: grid-cols-1 mobile, 2 tablet, 3 desktop
D3  — Sửa section hẹn giờ: stack dọc mobile, ngang desktop
D4  — Modal: fullscreen mobile (rounded-none, max-h-[100dvh])
D5  — Filter bar: overflow-x-auto scrollbar-none
D6  — Kiểm tra và thay tất cả hardcode màu → CSS variables
D7  — Thêm .scrollbar-none vào global CSS
D8  — Test dark/light mode + mobile/desktop
```

---

## Checklist kiểm tra trước khi code

### Backend
- [ ] `GetCurrentUserId()` — dùng đúng cách project đang dùng trong SettingsController
- [ ] `AppSetting` entity dùng đúng namespace của project
- [ ] `ContentModerationService` đọc từ DB trước, fallback config
- [ ] Khi `isLimitReached = true` và `enabled = true` → `ToggleAsync` trả về blocked, không lưu DB

### Frontend
- [ ] Import path `settingsApi.ts` đúng
- [ ] `toast.warning(...)` đúng method name trong project
- [ ] AI Toggle button hiện đúng 3 state: ON (xanh), OFF (gray), LOCKED (đỏ)
- [ ] Khi LOCKED: click → toast warning, không gọi API
- [ ] `showSchedule` state đã được thêm lại
- [ ] Nút Hẹn giờ pre-fill ngày giờ khi mở
- [ ] Mobile: modal fullscreen, grid 1 cột, section hẹn giờ stack dọc

---

## Test

```
TEST A — AI Toggle bình thường
1. Mở ScreenshotModal → thấy "AI OFF" (gray)
2. Click "AI OFF" → API call → đổi thành "AI ON" (xanh)
3. Toast: "Đã bật nhận diện nội dung 18+"
4. Restart backend → vẫn ON (lưu DB không mất khi restart)
5. Click "AI ON" → đổi thành "AI OFF"

TEST B — AI Toggle khi đạt 1.000 ảnh
1. (Giả lập) Sửa DB: usage = 1000
2. Mở modal → thấy "⚠️ 1000/1000" + "Đã khóa" (đỏ)
3. Click → Toast warning "Đã đạt 1.000 ảnh/tháng. Reset ngày XX/XX"
4. Button không gọi API (không tốn tiền)

TEST C — Hẹn giờ toggle
1. Mở modal → section hẹn giờ ẩn
2. Click nút "Hẹn giờ" → section mở, ngày giờ pre-filled = hiện tại
3. Click lại → section ẩn

TEST D — Responsive
Mobile (< 640px):
→ Modal fullscreen, không bo góc
→ Grid ảnh 1 cột
→ Section hẹn giờ stack dọc, input full width
→ Header text "Hẹn giờ" / "AI ON" ẩn, chỉ hiện icon

Tablet (640-768px):
→ Modal có bo góc, max-h 90vh
→ Grid 2 cột

Desktop (> 768px):
→ Grid 3 cột
→ Tất cả text header hiện đầy đủ

TEST E — Dark/Light Mode
→ Toggle dark/light → tất cả element đúng màu
→ Input date/time đúng màu trong cả 2 mode
→ AI toggle badge đúng màu (xanh/gray/đỏ) trong cả 2 mode
```
