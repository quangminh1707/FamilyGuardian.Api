# Family Guardian — Hướng dẫn triển khai Fix & Feature (Phần 4)

> **Ngày tạo:** 2026-05-14  
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE_3.md (Phần 3)

---

## ⚠️ Quy tắc bất di bất dịch

| Quy tắc | Chi tiết |
|---------|---------|
| `sp_ExtensionCheckAccess` | Tuyệt đối KHÔNG sửa |
| Extension background.js | KHÔNG thay đổi logic đang chạy |
| Logic hiện tại | KHÔNG thay đổi bất kỳ logic nào đang hoạt động |
| blocked.html | KHÔNG thay đổi giao diện extension |

---

## 📦 SQL (đã làm xong, chỉ đọc tham khảo rồi làm tiếp)

> ⚠️ Đã làm xong, tiếp tục làm backend.

```sql
DROP PROCEDURE IF EXISTS sp_GetChildAllowedWebsites;

DELIMITER ;;

CREATE PROCEDURE `sp_GetChildAllowedWebsites`(IN p_child_id INT)
BEGIN
    SELECT
        aw.id,
        aw.domain,
        aw.display_name,
        aw.favicon_url,
        aw.is_active,
        aw.time_limit_minutes,
        aw.allowed_start_time,
        aw.allowed_end_time,
        aw.is_verified,
        aw.is_safe,
        aw.http_status_code,
        aw.last_checked_at,
        aw.created_at,
        -- Usage hôm nay
        COALESCE(dus.total_seconds, 0)                                                AS today_seconds,
        COALESCE(dus.bonus_seconds, 0)                                                AS today_bonus_seconds,
        GREATEST(0, COALESCE(dus.total_seconds, 0) - COALESCE(dus.bonus_seconds, 0)) AS effective_seconds,
        COALESCE(dus.request_count, 0)                                                AS today_requests,
        -- Có vượt giới hạn không (dùng effective_seconds = total - bonus)
        CASE
            WHEN aw.time_limit_minutes IS NULL THEN FALSE
            WHEN GREATEST(0, COALESCE(dus.total_seconds, 0) - COALESCE(dus.bonus_seconds, 0))
                 >= (aw.time_limit_minutes * 60) THEN TRUE
            ELSE FALSE
        END AS limit_exceeded
    FROM allowed_websites aw
    LEFT JOIN daily_usage_stats dus
           ON dus.allowed_website_id = aw.id
          AND dus.usage_date = CURDATE()
    WHERE aw.child_id = p_child_id
    ORDER BY aw.is_active DESC, aw.domain;
END;;

DELIMITER ;
```
--
> ✅ **Sau khi chạy SQL:** SP mới trả thêm 2 cột `today_bonus_seconds` và `effective_seconds`. Backend cần map thêm 2 cột này nếu đang dùng DTO từ SP result (xem Bước 2 Bug 1 bên dưới).

---

## Bug 1: `extend_time` — Bonus seconds không được cộng vào

### Nguyên nhân

Có 2 nơi cần sửa:
1. **SP `sp_GetChildAllowedWebsites`**: Tính `limit_exceeded` dựa trên `total_seconds` mà không trừ `bonus_seconds` → SP vẫn báo `limit_exceeded=true` dù đã cộng bonus
2. **`UpdateHeartbeatAsync` trong C#**: Nếu service tự tính thời gian còn lại (không dùng SP), cũng cần trừ `bonus_seconds`
3. **`RespondToRequestAsync`**: Cần verify logic cộng `bonus_seconds` vào đúng row

### BƯỚC 1 — Kiểm tra Backend trước

**Đọc `ExtensionService.cs` — method `UpdateHeartbeatAsync`:**
- Tìm chỗ tính `remainingSeconds` hoặc check `limitExceeded`
- Xem có query `DailyUsageStats` không
- Xem có dùng kết quả từ SP hay tự tính
- **Nếu tự tính:** cần thêm trừ `bonus_seconds`
- **Nếu dùng SP:** cần sửa SP (SQL đã hướng dẫn ở trên)

**Đọc `AccessRequestService.cs` — method `RespondToRequestAsync`:**
- Tìm case `extend_time`
- Verify query tìm `DailyUsageStats` dùng đúng `ChildId`, `AllowedWebsiteId`, `UsageDate`
- Verify `stat.BonusSeconds += bonusMinutes * 60` được gọi
- Verify `await _context.SaveChangesAsync()` được gọi sau khi cộng

### BƯỚC 2 — Sửa DTO map từ SP result

SP mới trả thêm 2 cột: `today_bonus_seconds` và `effective_seconds`. Tìm class/record đang dùng để map kết quả SP (đọc code để xác định tên), thêm:

```csharp
// Thêm vào DTO map từ sp_GetChildAllowedWebsites result
public int TodayBonusSeconds { get; set; }
public int EffectiveSeconds { get; set; }
```

### BƯỚC 3 — Sửa `ExtensionService.cs` — UpdateHeartbeatAsync

> ⚠️ Đọc method trước. SP mới đã tính đúng `limit_exceeded` qua cột `limit_exceeded`. Nhưng nếu service TỰ TÍNH thêm `remainingSeconds` / warning threshold từ `today_seconds`, cần đổi sang `effective_seconds`.

```csharp
// Tìm đoạn đọc seconds từ SP result, ví dụ:
// var usedSeconds = spWebsite.TodaySeconds;
// THAY BẰNG (dùng cột mới SP trả về):
var usedSeconds = spWebsite.EffectiveSeconds; // GREATEST(0, total - bonus)

// Mọi chỗ tính remaining, SecondsUntilBlock, SecondsUntilWarning1/2
// đều đổi sang dùng usedSeconds mới — KHÔNG thay đổi công thức tính
```

### BƯỚC 3 — Verify `RespondToRequestAsync` — case `extend_time`

> ⚠️ Đọc toàn bộ case hiện tại. Kiểm tra từng bước:

```csharp
// Verify flow đúng thứ tự:

// 1. Tìm website
var website = await _context.AllowedWebsites
    .FirstOrDefaultAsync(w => w.ChildId == request.ChildId && w.Domain == request.Domain && w.IsActive);
if (website == null) return (false, "Không tìm thấy website trong danh sách");

// 2. Tìm stat HÔM NAY
var today = DateOnly.FromDateTime(DateTime.Now);
var stat = await _context.DailyUsageStats
    .FirstOrDefaultAsync(s => s.ChildId == request.ChildId
                           && s.AllowedWebsiteId == website.Id  // ← dùng website.Id, không phải website.ChildId
                           && s.UsageDate == today);

// 3. Cộng bonus (nếu stat tồn tại)
if (stat != null)
{
    var bonusMinutes = dto.DurationMinutes ?? request.RequestedDurationMinutes ?? 30;
    stat.BonusSeconds = (stat.BonusSeconds ?? 0) + (bonusMinutes * 60);
    // Reset warning để con không bị cảnh báo lại ngay
    stat.Warning1Sent = false;
    stat.Warning2Sent = false;
}
// Nếu stat == null: con chưa dùng web đó hôm nay → không cần bonus

// 4. Save
await _context.SaveChangesAsync();
```

**Kiểm tra Entity `DailyUsageStats`:**
```csharp
// Tìm class DailyUsageStats.cs, đảm bảo có property:
public int BonusSeconds { get; set; } = 0;
// Hoặc nullable:
public int? BonusSeconds { get; set; }
```

---

## Bug 2: Phân biệt Giới hạn phút vs Khung giờ trong guardian UI

### Tổng quan

Khi con gửi yêu cầu `time_limit_exceeded`, guardian cần biết:
- Website đó dùng **Giới hạn phút** → cho phép gia hạn thêm X phút (extend_time)
- Website đó dùng **Khung giờ** → cho phép điều chỉnh giờ kết thúc (extend_window)

### BƯỚC 1 — Kiểm tra Backend

**Đọc `AccessRequestService.cs` — method `GetRequestsAsync`:**
- Xem query `SELECT` từ `access_requests`
- Xem các field đang được map sang `AccessRequestDto`

**Đọc `AccessRequestDto.cs`:**
- Xem các field đang có
- Cần thêm: thông tin loại giới hạn của website

### BƯỚC 2 — Sửa `AccessRequestDto.cs`

Thêm các field mới (KHÔNG xóa field cũ):
```csharp
// Thêm vào cuối class
/// Loại giới hạn: "minutes" | "time_window" | null
public string? WebsiteRestrictionType { get; set; }
/// Nếu time_limit_minutes: giới hạn phút hiện tại
public int? WebsiteTimeLimitMinutes { get; set; }
/// Nếu time_window: giờ bắt đầu hiện tại "HH:mm"
public string? WebsiteAllowedStartTime { get; set; }
/// Nếu time_window: giờ kết thúc hiện tại "HH:mm"
public string? WebsiteAllowedEndTime { get; set; }
```

### BƯỚC 3 — Sửa `GetRequestsAsync` — map thêm restriction type

> ⚠️ Đọc method trước. Thêm JOIN hoặc sub-query để lấy thông tin website.

Thêm logic sau khi query `access_requests` (sau khi đã có list requests):

```csharp
// Sau khi có list các requests, enrich những request có reason = time_limit_exceeded
var timeLimitRequests = result.Where(r => r.Reason == "time_limit_exceeded").ToList();
if (timeLimitRequests.Any())
{
    // Lấy tất cả domain cần check
    var domainChildPairs = timeLimitRequests
        .Select(r => new { r.Domain, r.ChildId })
        .Distinct()
        .ToList();

    foreach (var dto in timeLimitRequests)
    {
        var website = await _context.AllowedWebsites
            .FirstOrDefaultAsync(w => w.ChildId == dto.ChildId
                                   && w.Domain == dto.Domain
                                   && w.IsActive);
        if (website == null) continue;

        if (website.TimeLimitMinutes.HasValue)
        {
            dto.WebsiteRestrictionType = "minutes";
            dto.WebsiteTimeLimitMinutes = website.TimeLimitMinutes;
        }
        else if (website.AllowedStartTime.HasValue)
        {
            dto.WebsiteRestrictionType = "time_window";
            dto.WebsiteAllowedStartTime = website.AllowedStartTime.Value.ToString(@"HH\:mm");
            dto.WebsiteAllowedEndTime = website.AllowedEndTime?.ToString(@"HH\:mm");
        }
    }
}
```

> **Nếu dùng LINQ projection (`.Select(r => new AccessRequestDto {...})`)**:
> Sau projection, chạy vòng lặp enrich ở trên trước khi return.

### BƯỚC 4 — Thêm action `extend_window` trong `RespondToRequestAsync`

> ⚠️ Đọc method hiện tại. Thêm case mới, KHÔNG sửa case cũ.

**Thêm field vào `RespondAccessRequestDto` (C#):**
```csharp
// Thêm field mới (không xóa field cũ)
/// Chỉ dùng khi Action = "extend_window": giờ kết thúc mới "HH:mm"
public string? NewEndTime { get; set; }
/// Chỉ dùng khi Action = "extend_window": giờ bắt đầu mới "HH:mm" (optional)
public string? NewStartTime { get; set; }
```

**Thêm case `extend_window`:**
```csharp
else if (dto.Action == "extend_window")
{
    request.Status = "approved_temp";

    if (string.IsNullOrEmpty(dto.NewEndTime))
        return (false, "Giờ kết thúc mới không được để trống");

    var website = await _context.AllowedWebsites
        .FirstOrDefaultAsync(w => w.ChildId == request.ChildId
                               && w.Domain == request.Domain
                               && w.IsActive);
    if (website == null) return (false, "Không tìm thấy website trong danh sách");

    // Cập nhật giờ kết thúc
    if (TimeSpan.TryParse(dto.NewEndTime, out var newEnd))
        website.AllowedEndTime = newEnd;

    // Cập nhật giờ bắt đầu nếu có
    if (!string.IsNullOrEmpty(dto.NewStartTime) && TimeSpan.TryParse(dto.NewStartTime, out var newStart))
        website.AllowedStartTime = newStart;

    // Reset tw_warning flags trong daily_usage_stats
    var today = DateOnly.FromDateTime(DateTime.Now);
    var stat = await _context.DailyUsageStats
        .FirstOrDefaultAsync(s => s.ChildId == request.ChildId
                               && s.AllowedWebsiteId == website.Id
                               && s.UsageDate == today);
    if (stat != null)
    {
        stat.TwWarning1Sent = false;
        stat.TwWarning2Sent = false;
    }

    await _context.SaveChangesAsync();

    // Gửi SignalR để extension auto-redirect (nếu có child group)
    await _hub.Clients
        .Group($"child_{request.ChildId}")
        .SendAsync("AccessApproved", new { childId = request.ChildId, domain = request.Domain });

    request.RespondedAt = DateTime.Now;
    request.Status = "approved_temp";
    await _context.SaveChangesAsync();

    return (true, "Đã cập nhật khung giờ cho phép");
}
```

### BƯỚC 5 — Sửa `AccessRequestCard.tsx` — UI phân biệt restriction type

**Kiểm tra trước:**
- Đọc `AccessRequestCard.tsx` hiện tại
- Tìm case render `time_limit_exceeded`
- Xem `AccessRequestDto` TypeScript interface

**Thêm field vào TypeScript interface:**
```typescript
// Thêm vào AccessRequestDto interface (KHÔNG xóa field cũ)
websiteRestrictionType?: 'minutes' | 'time_window' | null;
websiteTimeLimitMinutes?: number;
websiteAllowedStartTime?: string; // "HH:mm"
websiteAllowedEndTime?: string;   // "HH:mm"
```

**Thêm vào `RespondAccessRequestDto` TypeScript:**
```typescript
newEndTime?: string;   // "HH:mm"
newStartTime?: string; // "HH:mm"
```

**Sửa phần render actions cho `time_limit_exceeded` trong `AccessRequestCard.tsx`:**

```tsx
// Thay thế block render actions khi reason === 'time_limit_exceeded'
if (request.reason === 'time_limit_exceeded') {
  return (
    <div className="space-y-3 mt-3">
      {/* Thông tin giới hạn hiện tại */}
      <div className="px-3 py-2 rounded-lg bg-bg-elevated border border-border-base/50">
        {request.websiteRestrictionType === 'time_window' ? (
          <p className="text-xs text-tx-secondary">
            🕐 Khung giờ hiện tại:{' '}
            <span className="font-medium text-tx-primary">
              {request.websiteAllowedStartTime} – {request.websiteAllowedEndTime}
            </span>
          </p>
        ) : (
          <p className="text-xs text-tx-secondary">
            ⏱ Giới hạn hiện tại:{' '}
            <span className="font-medium text-tx-primary">
              {request.websiteTimeLimitMinutes ?? '?'} phút/ngày
            </span>
          </p>
        )}
      </div>

      {/* Nếu là time_window: show time pickers */}
      {request.websiteRestrictionType === 'time_window' ? (
        <WindowExtendForm
          request={request}
          mutation={mutation}
          onReject={() => setConfirmAction({ action: 'reject' })}
        />
      ) : (
        /* Nếu là minutes: show gia hạn phút */
        <MinutesExtendForm
          request={request}
          mutation={mutation}
          onReject={() => setConfirmAction({ action: 'reject' })}
        />
      )}
    </div>
  );
}
```

**Sub-component `MinutesExtendForm`:**
```tsx
function MinutesExtendForm({
  request, mutation, onReject,
}: {
  request: AccessRequestDto;
  mutation: any;
  onReject: () => void;
}) {
  const [minutes, setMinutes] = useState(
    request.requestedDurationMinutes ?? 30
  );

  return (
    <div className="space-y-2">
      <div className="flex items-center gap-2">
        <span className="text-xs text-tx-secondary">Gia hạn thêm:</span>
        <div className="flex gap-1.5">
          {[15, 30, 60].map(m => (
            <button
              key={m}
              onClick={() => setMinutes(m)}
              className={`px-2.5 py-1 text-xs rounded-lg border transition-colors ${
                minutes === m
                  ? 'bg-amber-500/20 text-amber-500 border-amber-500/40'
                  : 'bg-bg-subtle text-tx-secondary border-border-base hover:border-amber-500/30'
              }`}
            >
              {m}p
            </button>
          ))}
          <input
            type="number"
            value={minutes}
            onChange={e => setMinutes(Math.max(5, Number(e.target.value)))}
            min={5}
            max={480}
            className="w-16 px-2 py-1 text-xs text-center rounded-lg
                       bg-bg-subtle border border-border-base text-tx-primary
                       focus:outline-none focus:border-amber-500/50"
          />
          <span className="text-xs text-tx-secondary self-center">phút</span>
        </div>
      </div>
      <div className="flex gap-2">
        <button
          onClick={() => mutation.mutate({ action: 'extend_time', durationMinutes: minutes })}
          disabled={mutation.isPending}
          className="flex-1 px-3 py-2 text-xs rounded-lg font-medium
                     bg-amber-500/10 text-amber-600 dark:text-amber-400
                     border border-amber-500/30 hover:bg-amber-500/20
                     transition-colors disabled:opacity-50"
        >
          ⏱ Gia hạn {minutes} phút
        </button>
        <button
          onClick={onReject}
          disabled={mutation.isPending}
          className="px-3 py-2 text-xs rounded-lg font-medium
                     bg-red-500/10 text-red-600 dark:text-red-400
                     border border-red-500/30 hover:bg-red-500/20
                     transition-colors disabled:opacity-50"
        >
          ✕
        </button>
      </div>
    </div>
  );
}
```

**Sub-component `WindowExtendForm`:**
```tsx
function WindowExtendForm({
  request, mutation, onReject,
}: {
  request: AccessRequestDto;
  mutation: any;
  onReject: () => void;
}) {
  const [startTime, setStartTime] = useState(request.websiteAllowedStartTime ?? '07:00');
  const [endTime, setEndTime] = useState(() => {
    // Đề xuất mặc định: kéo dài thêm 30 phút so với giờ kết thúc hiện tại
    if (!request.websiteAllowedEndTime) return '21:00';
    const [h, m] = request.websiteAllowedEndTime.split(':').map(Number);
    const total = h * 60 + m + 30;
    return `${String(Math.floor(total / 60) % 24).padStart(2, '0')}:${String(total % 60).padStart(2, '0')}`;
  });

  return (
    <div className="space-y-2">
      <p className="text-xs text-tx-secondary">Điều chỉnh khung giờ:</p>
      <div className="grid grid-cols-2 gap-2">
        <div>
          <label className="text-[11px] text-tx-secondary block mb-1">Từ lúc</label>
          <input
            type="time"
            value={startTime}
            onChange={e => setStartTime(e.target.value)}
            className="w-full px-2.5 py-1.5 text-sm rounded-lg
                       bg-bg-subtle border border-border-base text-tx-primary
                       focus:outline-none focus:border-brand-DEFAULT/60"
          />
        </div>
        <div>
          <label className="text-[11px] text-tx-secondary block mb-1">Đến lúc</label>
          <input
            type="time"
            value={endTime}
            onChange={e => setEndTime(e.target.value)}
            className="w-full px-2.5 py-1.5 text-sm rounded-lg
                       bg-bg-subtle border border-border-base text-tx-primary
                       focus:outline-none focus:border-brand-DEFAULT/60"
          />
        </div>
      </div>
      <div className="flex gap-2">
        <button
          onClick={() => mutation.mutate({
            action: 'extend_window',
            newStartTime: startTime,
            newEndTime: endTime,
          })}
          disabled={mutation.isPending}
          className="flex-1 px-3 py-2 text-xs rounded-lg font-medium
                     bg-brand-DEFAULT/10 text-brand-DEFAULT
                     border border-brand-DEFAULT/30 hover:bg-brand-DEFAULT/20
                     transition-colors disabled:opacity-50"
        >
          🕐 Cập nhật khung giờ
        </button>
        <button
          onClick={onReject}
          disabled={mutation.isPending}
          className="px-3 py-2 text-xs rounded-lg font-medium
                     bg-red-500/10 text-red-600 dark:text-red-400
                     border border-red-500/30 hover:bg-red-500/20
                     transition-colors disabled:opacity-50"
        >
          ✕
        </button>
      </div>
    </div>
  );
}
```

**Cập nhật mutation onSuccess trong `AccessRequestCard.tsx`:**
```tsx
onSuccess: (_, dto) => {
  // ... các case cũ ...
  else if (dto.action === 'extend_time') toast.success(`Đã gia hạn thêm ${dto.durationMinutes} phút`);
  else if (dto.action === 'extend_window') toast.success('Đã cập nhật khung giờ cho phép');
  // ...
},
```

---

## UI 3: Filter Dropdown với Icon

### Phạm vi
**Chỉ Frontend.** Thay thế flat chips bằng icon + dropdown. KHÔNG thay đổi logic filter.

### Kiểm tra trước
- Đọc `NotificationsPage.tsx` — xem filter chips hiện tại (status filter + reason filter)
- Đọc icon library đang dùng (lucide-react, heroicons...)

### Tạo component `FilterDropdown.tsx`

```tsx
import { useState, useRef, useEffect } from 'react';

interface FilterOption<T extends string> {
  value: T;
  label: string;
  icon?: string;
  color?: string; // tailwind class cho màu indicator
}

interface FilterDropdownProps<T extends string> {
  label: string;       // Label nút: "Trạng thái", "Loại yêu cầu"
  options: FilterOption<T>[];
  value: T;
  onChange: (v: T) => void;
  align?: 'left' | 'right';
}

export function FilterDropdown<T extends string>({
  label, options, value, onChange, align = 'left',
}: FilterDropdownProps<T>) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  // Đóng khi click bên ngoài
  useEffect(() => {
    function handleClick(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
      }
    }
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, []);

  const selected = options.find(o => o.value === value);
  const isFiltered = value !== options[0].value; // nếu khác default → đang filter

  return (
    <div ref={ref} className="relative">
      {/* Trigger button */}
      <button
        onClick={() => setOpen(p => !p)}
        className={`flex items-center gap-2 px-3 py-2 text-xs font-medium rounded-xl
                   border transition-all ${
          isFiltered
            ? 'bg-brand-DEFAULT/10 border-brand-DEFAULT/50 text-brand-DEFAULT'
            : 'bg-bg-subtle border-border-base text-tx-secondary hover:text-tx-primary hover:border-border-base/80'
        }`}
      >
        {/* Filter icon */}
        <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round"
            d="M3 4h18M7 8h10M11 12h2M13 16h-2" />
        </svg>

        {/* Label + active value */}
        <span>
          {isFiltered ? selected?.label : label}
        </span>

        {/* Chevron */}
        <svg
          className={`w-3 h-3 transition-transform ${open ? 'rotate-180' : ''}`}
          fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}
        >
          <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
        </svg>
      </button>

      {/* Dropdown */}
      {open && (
        <div className={`absolute top-full mt-1.5 z-30 w-48
                        bg-bg-elevated border border-border-base rounded-xl
                        shadow-xl shadow-black/20 py-1 overflow-hidden
                        ${align === 'right' ? 'right-0' : 'left-0'}`}>
          {options.map(opt => (
            <button
              key={opt.value}
              onClick={() => { onChange(opt.value); setOpen(false); }}
              className={`w-full flex items-center gap-2.5 px-3 py-2.5 text-xs text-left
                         transition-colors ${
                value === opt.value
                  ? 'bg-brand-DEFAULT/10 text-brand-DEFAULT'
                  : 'text-tx-secondary hover:bg-bg-subtle hover:text-tx-primary'
              }`}
            >
              {/* Checkmark */}
              <span className={`w-3.5 h-3.5 flex-shrink-0 ${value === opt.value ? 'opacity-100' : 'opacity-0'}`}>
                ✓
              </span>
              {opt.icon && <span>{opt.icon}</span>}
              <span>{opt.label}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
```

### Tích hợp vào `NotificationsPage.tsx`

Thay thế các filter chip sections hiện tại bằng:

```tsx
{/* Filter bar — chỉ hiện trong tab Yêu cầu */}
{activeTab === 'requests' && (
  <div className="flex items-center gap-2 mb-5">
    <FilterDropdown
      label="Trạng thái"
      options={[
        { value: 'pending', label: 'Chờ duyệt' },
        { value: 'handled', label: 'Đã xử lý' },
        { value: 'all', label: 'Tất cả' },
      ]}
      value={requestFilter}
      onChange={handleStatusFilterChange}
    />

    <FilterDropdown
      label="Loại yêu cầu"
      options={[
        { value: 'all', label: 'Tất cả loại' },
        { value: 'internet_paused', label: 'Tạm dừng Internet', icon: '⏸' },
        { value: 'time_limit_exceeded', label: 'Hết giờ sử dụng', icon: '⏱' },
        { value: 'not_in_whitelist', label: 'Web mới', icon: '🌐' },
      ]}
      value={reasonFilter}
      onChange={v => { setReasonFilter(v); }}
    />

    {/* Active filter tags */}
    {(requestFilter !== 'pending' || reasonFilter !== 'all') && (
      <button
        onClick={() => { setRequestFilter('pending'); setReasonFilter('all'); }}
        className="ml-auto text-[11px] text-tx-secondary hover:text-red-400
                   flex items-center gap-1 transition-colors"
      >
        ✕ Xóa bộ lọc
      </button>
    )}
  </div>
)}

{/* Filter bar — chỉ hiện trong tab Thông báo */}
{activeTab === 'notifications' && (
  <div className="flex items-center gap-2 mb-5">
    <FilterDropdown
      label="Trạng thái đọc"
      options={[
        { value: 'all', label: 'Tất cả' },
        { value: 'unread', label: 'Chưa đọc' },
        { value: 'read', label: 'Đã đọc' },
      ]}
      value={notifFilter}
      onChange={handleNotifFilterChange}
    />
  </div>
)}
```

---

## UI 4: Tab Bar — Thiết kế lại với hiệu ứng đẹp

### Kiểm tra trước
- Đọc phần tab switcher hiện tại trong `NotificationsPage.tsx`
- Xem CSS variables đang có: `bg-bg-surface`, `brand-DEFAULT`, v.v.

### Thay thế tab bar

> ⚠️ Chỉ thay đổi phần render tab + phần header. KHÔNG thay đổi state logic và query logic.

```tsx
{/* ─── Page header ─── */}
<div className="mb-6">
  <h1 className="text-2xl font-bold text-tx-primary tracking-tight">Thông Báo</h1>
  <p className="text-sm text-tx-secondary mt-1">
    Theo dõi yêu cầu và thông báo từ hệ thống.
  </p>
</div>

{/* ─── Tab bar + actions ─── */}
<div className="flex items-center justify-between mb-1">
  {/* Sliding pill tabs */}
  <div className="relative flex p-1 rounded-2xl bg-bg-subtle border border-border-base">
    {/* Sliding indicator */}
    <div
      className="absolute top-1 bottom-1 rounded-xl bg-bg-surface border border-border-base/80
                 shadow-sm transition-all duration-200 ease-out"
      style={{
        width: 'calc(50% - 4px)',
        left: activeTab === 'requests' ? '4px' : 'calc(50%)',
      }}
    />

    {/* Tab: Yêu cầu */}
    <button
      onClick={() => setActiveTab('requests')}
      className={`relative z-10 flex items-center gap-2 px-5 py-2.5 text-sm font-medium
                 rounded-xl transition-colors duration-200 min-w-[120px] justify-center ${
        activeTab === 'requests' ? 'text-tx-primary' : 'text-tx-secondary hover:text-tx-primary'
      }`}
    >
      Yêu cầu
      {pendingRequestsCount > 0 && (
        <span className={`inline-flex items-center justify-center min-w-[18px] h-[18px] px-1
                         text-[10px] font-bold rounded-full transition-colors ${
          activeTab === 'requests'
            ? 'bg-amber-500 text-white'
            : 'bg-bg-muted text-tx-secondary'
        }`}>
          {pendingRequestsCount}
        </span>
      )}
    </button>

    {/* Tab: Thông báo */}
    <button
      onClick={() => setActiveTab('notifications')}
      className={`relative z-10 flex items-center gap-2 px-5 py-2.5 text-sm font-medium
                 rounded-xl transition-colors duration-200 min-w-[120px] justify-center ${
        activeTab === 'notifications' ? 'text-tx-primary' : 'text-tx-secondary hover:text-tx-primary'
      }`}
    >
      Thông báo
      {unreadCount > 0 && (
        <span className={`inline-flex items-center justify-center min-w-[18px] h-[18px] px-1
                         text-[10px] font-bold rounded-full transition-colors ${
          activeTab === 'notifications'
            ? 'bg-red-500 text-white'
            : 'bg-bg-muted text-tx-secondary'
        }`}>
          {unreadCount > 99 ? '99+' : unreadCount}
        </span>
      )}
    </button>
  </div>

  {/* Action button: chỉ hiện trong tab thông báo */}
  {activeTab === 'notifications' && (
    <button
      onClick={handleMarkAllRead}
      className="flex items-center gap-1.5 px-3 py-2 text-xs font-medium
                 text-tx-secondary hover:text-tx-primary border border-border-base
                 rounded-xl bg-bg-subtle hover:bg-bg-surface transition-all"
    >
      <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24"
           stroke="currentColor" strokeWidth={2}>
        <path strokeLinecap="round" strokeLinejoin="round"
          d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
      </svg>
      Đánh dấu tất cả đã đọc
    </button>
  )}
</div>

{/* Tab content với fade animation */}
<div className="mt-4">
  <div
    key={activeTab}  {/* key change triggers remount = fade in */}
    className="animate-in fade-in duration-150"
  >
    {/* Nội dung tab đang có */}
  </div>
</div>
```

> **Nếu không có `animate-in` (shadcn/tailwind-animate):** Dùng CSS thuần:
```tsx
// Thêm vào global CSS hoặc tailwind.config:
// @keyframes fadeIn { from { opacity: 0; transform: translateY(4px); } to { opacity: 1; transform: translateY(0); } }
// .fade-in { animation: fadeIn 150ms ease-out; }
```

### Alignment tổng thể

Đảm bảo layout page Notifications:
```tsx
<div className="max-w-3xl mx-auto px-4 py-6 space-y-0">
  {/* Header */}
  {/* Tab bar + action button — flex items-center justify-between */}
  {/* Filter bar — flex items-center gap-2 */}
  {/* Danh sách — space-y-3 */}
</div>
```

---

## Checklist cuối

### Bug 1 — extend_time
- [ ] SP `sp_GetChildAllowedWebsites` đã sửa: dùng `effective_seconds` (trừ `bonus_seconds`) cho `limit_exceeded`
- [ ] `UpdateHeartbeatAsync`: dùng `effectiveUsed = TotalSeconds - BonusSeconds` cho warning checks
- [ ] `RespondToRequestAsync` case `extend_time`: query `DailyUsageStats` đúng `AllowedWebsiteId`
- [ ] `BonusSeconds` được cộng và SaveChanges được gọi
- [ ] `Warning1Sent`, `Warning2Sent` được reset về false
- [ ] Test: hết 10p → gửi yêu cầu → guardian gia hạn 5p → heartbeat không còn `limitExceeded`

### Bug 2 — time_limit vs time_window
- [ ] `AccessRequestDto` có 4 field mới: `WebsiteRestrictionType`, `WebsiteTimeLimitMinutes`, `WebsiteAllowedStartTime`, `WebsiteAllowedEndTime`
- [ ] `GetRequestsAsync` enrich các request `time_limit_exceeded` với info website
- [ ] `RespondAccessRequestDto` có `NewEndTime`, `NewStartTime`
- [ ] `RespondToRequestAsync` xử lý case `extend_window` cập nhật `allowed_websites`
- [ ] `RespondToRequestAsync` case `extend_window` reset `TwWarning1Sent`, `TwWarning2Sent`
- [ ] TypeScript interface cập nhật đủ field
- [ ] `AccessRequestCard.tsx`: render `MinutesExtendForm` khi `websiteRestrictionType === 'minutes'`
- [ ] `AccessRequestCard.tsx`: render `WindowExtendForm` khi `websiteRestrictionType === 'time_window'`
- [ ] `WindowExtendForm`: default end time = current end time + 30p
- [ ] Test: website khung giờ → con hết giờ → guardian thấy time pickers → update → con vào được

### UI 3 — Filter Dropdown
- [ ] `FilterDropdown.tsx` component đã tạo
- [ ] Click trigger button → dropdown hiện ra
- [ ] Click bên ngoài → dropdown đóng
- [ ] Selected option có checkmark ✓
- [ ] Khi đang filter → trigger button đổi màu `brand-DEFAULT/10`
- [ ] Nút "Xóa bộ lọc" hiện khi có filter active
- [ ] Filter hoạt động đúng (vẫn dùng state/logic cũ)

### UI 4 — Tab Bar
- [ ] Sliding pill indicator chạy mượt giữa 2 tab
- [ ] Badge count hiện đúng màu theo tab active
- [ ] "Đánh dấu tất cả đã đọc" chỉ hiện trong tab Thông báo
- [ ] Fade animation khi đổi tab
- [ ] Alignment đúng: header → tab bar → filter bar → danh sách

---

## Lưu ý Dark Mode

- `FilterDropdown` dùng `bg-bg-elevated`, `bg-bg-subtle`, `text-tx-secondary` → hoạt động đúng dark/light
- Tab bar dùng `bg-bg-subtle` (container) + `bg-bg-surface` (indicator) → giống pattern đang có trong hệ thống
- Sliding indicator dùng `transition-all duration-200` — kiểm tra không bị lag trên mobile
