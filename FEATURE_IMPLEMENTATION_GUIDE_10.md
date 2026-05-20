# Family Guardian — Fix WebsiteCard Bonus + blocked.html UX + 24h Picker (Phần 10)

> **Ngày tạo:** 2026-05-17  
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE_9.md (Phần 9)

---

## ⚠️ Quy tắc bất di bất dịch

| Quy tắc | Chi tiết |
|---------|---------|
| `sp_ExtensionCheckAccess` | Tuyệt đối KHÔNG sửa |
| `background.js` | KHÔNG thay đổi logic block/heartbeat — chỉ THÊM vào blocked.html |
| Logic hiện tại | KHÔNG thay đổi, chỉ bổ sung |
| Dark mode | Dùng CSS variables: `bg-bg-surface`, `text-tx-primary`, `border-border-base` |

---

## 📦 SQL — Không có SQL mới trong phần này

> ✅ SQL từ Guide 9 (internet_paused, temp_expires_at, SP update) đã đủ. Không cần thêm gì.

---

## Tổng quan 4 fix trong guide này

| # | Fix | Nơi sửa |
|---|-----|---------|
| 1 | Bonus giờ "Khung giờ" hiện đúng + Isolation theo chế độ | Frontend `WebsiteCard.tsx` |
| 2 | blocked.html giảm ping + hiện đầy đủ thông tin | Extension `blocked.html/js` + Backend `/check` |
| 3 | blocked.html reload khi guardian cập nhật config | Extension `blocked.html/js` |
| 4 | Time picker 24h thay AM/PM | Frontend `AddWebsiteModal`, `EditWebsiteModal` |

---

## FIX 1 — WebsiteCard: Bonus đúng theo chế độ + Isolation

### Root cause

Vấn đề 1: Bonus "Khung giờ" không hiện → do logic tính `extendedEndTime` chưa được implement đúng hoặc props không được truyền.

Vấn đề 2: Khi đổi chế độ (từ phút sang giờ hoặc ngược lại), vẫn hiện bonus của chế độ cũ → do code chưa check `timeLimitMinutes` và `allowedStartTime` để quyết định hiện cái nào.

### Nguyên tắc Isolation

```
Website đang dùng timeLimitMinutes != null → CHỈ hiện bonus phút
Website đang dùng allowedStartTime != null → CHỈ hiện bonus giờ (extended end time)
Không bao giờ hiện cả 2 cùng lúc
```

### Frontend — Sửa `WebsiteCard.tsx`

#### Bước 1.1 — Đọc toàn bộ component, xác định điều kiện render hiện tại

Tìm:
- Đoạn render bonus phút (từ Guide 9): điều kiện `hasBonusTime && ...`
- Đoạn render bonus giờ (từ Guide 9): điều kiện `hasBonusTime && extendedEndTime && ...`

#### Bước 1.2 — Thêm biến xác định chế độ hiện tại

Thêm vào đầu component (sau khi đã có `bonus`, `bonusMinutes`):

```typescript
// Xác định chế độ website (chỉ 1 trong 2)
const isTimeLimitMode  = timeLimitMinutes != null && timeLimitMinutes > 0;
const isTimeWindowMode = allowedStartTime != null && allowedEndTime != null;

// Bonus chỉ có ý nghĩa khi khớp với chế độ hiện tại
const hasTimeLimitBonus  = isTimeLimitMode  && bonus > 0;
const hasTimeWindowBonus = isTimeWindowMode && bonus > 0;
```

#### Bước 1.3 — Sửa điều kiện render bonus phút

```tsx
// ❌ Cũ (hiện cả khi đang ở chế độ khung giờ):
{hasBonusTime && (
  <div>+{bonusMinutes} phút gia hạn</div>
)}

// ✅ Mới — chỉ hiện khi đang ở chế độ Giới hạn phút:
{hasTimeLimitBonus && (
  <div className="mt-2 px-2 py-1.5 rounded-md bg-green-500/8 border border-green-500/20
                  flex items-center justify-between text-xs">
    <span className="text-tx-secondary">
      Gốc: <span className="font-medium text-tx-primary">{timeLimitMinutes} phút</span>
    </span>
    <span className="font-semibold text-green-600 dark:text-green-400">
      +{bonusMinutes} phút gia hạn
    </span>
    <span className="text-tx-secondary">
      Tổng: <span className="font-medium text-tx-primary">{totalAllowedMinutes} phút</span>
    </span>
  </div>
)}
```

#### Bước 1.4 — Sửa điều kiện render bonus giờ (extended end time)

```tsx
// ❌ Cũ — điều kiện không đủ chặt:
{hasBonusTime && extendedEndTime && (
  <div>Gia hạn +{bonusMinutes} phút — đến {extendedEndTime}</div>
)}

// ✅ Mới — chỉ hiện khi đang ở chế độ Khung giờ:
{hasTimeWindowBonus && extendedEndTime && (
  <div className="mt-2 px-2 py-1.5 rounded-md bg-green-500/8 border border-green-500/20
                  flex items-center gap-2 text-xs">
    <svg className="w-3.5 h-3.5 text-green-500 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6v6m0 0v6m0-6h6m-6 0H6"/>
    </svg>
    <span className="text-tx-secondary">
      Gia hạn{' '}
      <span className="font-semibold text-green-600 dark:text-green-400">+{bonusMinutes} phút</span>
      {' '}— đến{' '}
      <span className="font-semibold text-tx-primary">{extendedEndTime}</span>
    </span>
  </div>
)}
```

#### Bước 1.5 — Verify hàm `computeExtendedEndTime`

Đảm bảo hàm tính đúng khi `bonus > 0` và `allowedEndTime` có giá trị:

```typescript
const computeExtendedEndTime = (): string | null => {
  if (!isTimeWindowMode || !allowedEndTime || bonus <= 0) return null;
  
  // allowedEndTime dạng "HH:mm" (24h)
  const [h, m] = allowedEndTime.split(':').map(Number);
  if (isNaN(h) || isNaN(m)) return null;
  
  const totalMin  = h * 60 + m + bonusMinutes;
  const newH = Math.floor(totalMin / 60) % 24;
  const newM = totalMin % 60;
  return `${String(newH).padStart(2, '0')}:${String(newM).padStart(2, '0')}`;
};
const extendedEndTime = computeExtendedEndTime();
```

---

## FIX 2 — blocked.html: Giảm ping + Hiện đầy đủ thông tin

### Backend — Thêm thông tin vào `/check` response

#### Bước 2.1 — Mở `ExtensionController.cs` và `CheckAccessResult`

Thêm vào class `CheckAccessResult` (nếu chưa có từ Guide 9):

```csharp
public string? Reason { get; set; }          // "time_limit_exceeded" | "outside_time_window"
public int? LimitMinutes { get; set; }        // giới hạn phút gốc
public int? UsedSeconds { get; set; }         // đã dùng bao nhiêu giây (effective)
public string? TimeWindowStart { get; set; } // "HH:mm"
public string? TimeWindowEnd { get; set; }   // "HH:mm"
```

#### Bước 2.2 — Populate trong `CheckAccessAsync` sau bonus override

```csharp
// Sau khi xác định allowed/blocked, thêm thông tin để blocked.html hiển thị:
if (!result.Allowed && result.AllowedWebsiteId.HasValue)
{
    var today = DateOnly.FromDateTime(DateTime.Now);
    var stat  = await _context.DailyUsageStats
        .AsNoTracking()
        .FirstOrDefaultAsync(s => s.AllowedWebsiteId == result.AllowedWebsiteId.Value
                               && s.UsageDate == today);

    var website = await _context.AllowedWebsites
        .AsNoTracking()
        .FirstOrDefaultAsync(w => w.Id == result.AllowedWebsiteId.Value);

    if (website != null)
    {
        if (website.TimeLimitMinutes != null)
        {
            result.Reason       = "time_limit_exceeded";
            result.LimitMinutes = website.TimeLimitMinutes;
            result.UsedSeconds  = stat != null
                ? Math.Max(0, stat.TotalSeconds - (stat.BonusSeconds))
                : 0;
        }
        else if (website.AllowedStartTime != null)
        {
            result.Reason          = "outside_time_window";
            result.TimeWindowStart = website.AllowedStartTime?.ToString(@"hh\:mm");
            result.TimeWindowEnd   = website.AllowedEndTime?.ToString(@"hh\:mm");
        }
    }
}
```

> ⚠️ Chỉ THÊM đoạn populate. KHÔNG thay đổi logic allow/block phía trên.

---

### Extension — Sửa `blocked.html` / `blocked.js`

> ⚠️ KHÔNG thay đổi logic poll, redirect, hay request-sending hiện có.
> Chỉ THÊM: rate limiting, DOM update thay vì reload, và hiển thị info.

#### Bước 2.3 — Xác định polling interval hiện tại

Mở `blocked.html`. Tìm `setInterval` hoặc `setTimeout` dùng để poll `/check`. Ghi lại interval hiện tại.

#### Bước 2.4 — Thêm 2-tier polling: nhanh check allowed + chậm update status

```javascript
// ── THÊM MỚI: 2-tier polling config ──
const FAST_POLL_MS  = 8000;   // 8s — check xem đã được phép chưa
const SLOW_POLL_MS  = 20000;  // 20s — cập nhật thông tin hiển thị

let fastPollTimer  = null;
let slowPollTimer  = null;
let lastConfig     = null;    // để detect config thay đổi
// ── KẾT THÚC THÊM MỚI ──
```

#### Bước 2.5 — Tách startPassivePolling thành 2 hàm

> ⚠️ Giữ nguyên nội dung gọi `/check` và xử lý `allowed: true` → redirect. Chỉ cấu trúc lại interval.

```javascript
// ── THÊM MỚI: Hàm cập nhật UI từ response (KHÔNG reload page) ──
function updateBlockedUI(data) {
  const reason = data?.reason ?? 'unknown';
  const reasonEl = document.getElementById('block-reason-text');
  const infoEl   = document.getElementById('block-info-detail');

  if (reasonEl) {
    if (reason === 'time_limit_exceeded') {
      const limit   = data?.limitMinutes ?? '?';
      const usedMin = Math.floor((data?.usedSeconds ?? 0) / 60);
      reasonEl.textContent = `Đã hết ${limit} phút cho phép hôm nay`;
      if (infoEl) infoEl.textContent = `Đã dùng: ${usedMin} phút / ${limit} phút`;
    } else if (reason === 'outside_time_window') {
      const start = data?.timeWindowStart ?? '--:--';
      const end   = data?.timeWindowEnd   ?? '--:--';
      reasonEl.textContent = `Ngoài khung giờ cho phép`;
      if (infoEl) infoEl.textContent = `Khung giờ: ${start} – ${end}`;
    }
  }

  // Detect config thay đổi (limit hoặc window thay đổi) → reload để hiển thị mới
  const newConfig = JSON.stringify({
    limitMinutes:    data?.limitMinutes,
    timeWindowStart: data?.timeWindowStart,
    timeWindowEnd:   data?.timeWindowEnd
  });
  if (lastConfig !== null && lastConfig !== newConfig) {
    window.location.reload();
    return;
  }
  if (lastConfig === null) {
    lastConfig = newConfig;
    updateBlockedUI(data); // cập nhật UI lần đầu
  }
  lastConfig = newConfig;
}

// ── THÊM MỚI: Fast poll — chỉ check allowed, không update UI nặng ──
async function fastPollCheck() {
  try {
    const token  = await getGoogleToken(); // hàm hiện có
    const resp   = await fetch(`${API_BASE}/api/extension/check?domain=${domain}`, {
      headers: { Authorization: `Bearer ${token}` }
    });
    if (!resp.ok) return;
    const data = await resp.json();

    // Nếu được phép → redirect (GIỮ NGUYÊN logic hiện có)
    if (data.allowed) {
      window.location.href = `https://${domain}`;
      return;
    }
  } catch (e) {
    console.warn('Fast poll error:', e);
  }
}

// ── THÊM MỚI: Slow poll — cập nhật thông tin hiển thị ──
async function slowPollStatus() {
  try {
    const token = await getGoogleToken();
    const resp  = await fetch(`${API_BASE}/api/extension/check?domain=${domain}`, {
      headers: { Authorization: `Bearer ${token}` }
    });
    if (!resp.ok) return;
    const data = await resp.json();

    if (data.allowed) {
      window.location.href = `https://${domain}`;
      return;
    }
    updateBlockedUI(data);
  } catch (e) {
    console.warn('Slow poll error:', e);
  }
}

// ── SỬA startPassivePolling: dùng 2 timer ──
function startPassivePolling() {
  // Poll nhanh để detect khi được phép
  fastPollTimer = setInterval(fastPollCheck, FAST_POLL_MS);

  // Poll chậm để cập nhật thông tin hiển thị
  slowPollTimer = setInterval(slowPollStatus, SLOW_POLL_MS);

  // Lần đầu load: lấy ngay status để hiện thông tin
  slowPollStatus();
}
// ── KẾT THÚC ──
```

> ⚠️ Nếu `startPassivePolling` hiện tại đang dùng `setInterval` với hàm gọi `/check` → **giữ nguyên hàm đó**, chỉ đổi interval thành `FAST_POLL_MS` và thêm `slowPollTimer` riêng.
> Nếu `startPassivePolling` đang làm `window.location.reload()` trên mỗi poll → **đây là bug**, đổi thành `updateBlockedUI(data)` như trên.

#### Bước 2.6 — Thêm HTML elements vào `blocked.html`

Tìm phần hiển thị nội dung block. Thêm 2 element:

```html
<!-- Dòng lý do chặn chính -->
<p id="block-reason-text" 
   class="text-base font-semibold text-tx-primary mt-2">
  Trang web này đang bị chặn
</p>

<!-- Chi tiết (giới hạn phút / khung giờ) -->
<p id="block-info-detail" 
   class="text-sm text-tx-secondary mt-1">
  <!-- Sẽ được điền bởi JS -->
</p>
```

> CSS class phải dùng CSS variables của hệ thống (dark mode compatible).
> Nếu `blocked.html` không dùng Tailwind → dùng inline style hoặc class CSS thông thường nhưng phải responsive với dark mode.

#### Bước 2.7 — Verify nút "Gửi yêu cầu" không bị gián đoạn

Mở phần HTML của nút "Xin thêm thời gian". Đảm bảo:
- Nút có `id` hoặc `class` cố định, KHÔNG bị xóa và tạo lại bởi JS
- Hàm submit request là event-driven (onclick/addEventListener), KHÔNG bị reset bởi polling

```javascript
// ✅ ĐÚNG — listener không bị ảnh hưởng bởi polling:
document.getElementById('btn-request').addEventListener('click', handleRequestTime);

// ❌ SAI — gán lại listener sau mỗi poll (sẽ gây duplicate listeners):
// setInterval(() => {
//   document.getElementById('btn-request').onclick = handleRequestTime;
// }, 5000);
```

---

## FIX 3 — blocked.html: Reload khi guardian cập nhật config

Đã được xử lý trong **Bước 2.5** (`updateBlockedUI` → detect config change → `window.location.reload()`).

Confirm flow:
```
Guardian thay đổi config website (thêm phút / đổi khung giờ)
→ Backend cập nhật allowed_websites table
→ Slow poll (20s) của blocked.html gọi /check
→ /check trả về config mới (limitMinutes hoặc timeWindowStart/End khác)
→ updateBlockedUI detect sự thay đổi → window.location.reload()
→ Trang load lại với config mới → hiện thông tin mới cho con
```

---

## FIX 4 — Time picker 24h (thay AM/PM)

> Áp dụng cho: `AddWebsiteModal.tsx`, `EditWebsiteModal.tsx`, và bất kỳ chỗ nào dùng `<input type="time">` cho khung giờ.

### Bước 4.1 — Tạo component `TimeInput24h.tsx`

Tạo file mới `src/components/ui/TimeInput24h.tsx`:

```tsx
import React from 'react';

interface TimeInput24hProps {
  value: string;        // format "HH:mm" (24h)
  onChange: (value: string) => void;
  label?: string;
  disabled?: boolean;
  className?: string;
}

export function TimeInput24h({
  value,
  onChange,
  label,
  disabled = false,
  className = '',
}: TimeInput24hProps) {
  const [hStr, mStr] = (value || '00:00').split(':');
  const hours   = parseInt(hStr, 10) || 0;
  const minutes = parseInt(mStr, 10) || 0;

  const handleHourChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    let h = parseInt(e.target.value, 10);
    if (isNaN(h)) h = 0;
    h = Math.max(0, Math.min(23, h));
    onChange(`${String(h).padStart(2, '0')}:${String(minutes).padStart(2, '0')}`);
  };

  const handleMinuteChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    let m = parseInt(e.target.value, 10);
    if (isNaN(m)) m = 0;
    m = Math.max(0, Math.min(59, m));
    onChange(`${String(hours).padStart(2, '0')}:${String(m).padStart(2, '0')}`);
  };

  // Tên giờ (để gợi nhớ: sáng/chiều/tối)
  const periodLabel =
    hours < 12 ? '(Sáng)' :
    hours < 18 ? '(Chiều)' :
    '(Tối)';

  return (
    <div className={`flex flex-col gap-1 ${className}`}>
      {label && (
        <label className="text-xs font-medium uppercase tracking-wide text-tx-secondary">
          {label}
        </label>
      )}
      <div className={`
        flex items-center gap-1 rounded-lg border px-3 py-2
        bg-bg-subtle border-border-base
        focus-within:border-brand-DEFAULT/60 focus-within:ring-1 focus-within:ring-brand-DEFAULT/20
        ${disabled ? 'opacity-50 cursor-not-allowed' : ''}
      `}>
        {/* Giờ (0-23) */}
        <input
          type="number"
          min={0}
          max={23}
          value={String(hours).padStart(2, '0')}
          onChange={handleHourChange}
          disabled={disabled}
          inputMode="numeric"
          className="w-10 text-center bg-transparent text-tx-primary text-sm
                     font-mono focus:outline-none [appearance:textfield]
                     [&::-webkit-inner-spin-button]:appearance-none
                     [&::-webkit-outer-spin-button]:appearance-none"
          placeholder="HH"
        />
        <span className="text-tx-secondary font-bold text-sm select-none">:</span>
        {/* Phút (0-59) */}
        <input
          type="number"
          min={0}
          max={59}
          step={5}
          value={String(minutes).padStart(2, '0')}
          onChange={handleMinuteChange}
          disabled={disabled}
          inputMode="numeric"
          className="w-10 text-center bg-transparent text-tx-primary text-sm
                     font-mono focus:outline-none [appearance:textfield]
                     [&::-webkit-inner-spin-button]:appearance-none
                     [&::-webkit-outer-spin-button]:appearance-none"
          placeholder="MM"
        />
        {/* Label sáng/chiều/tối gợi nhớ */}
        <span className="ml-1 text-xs text-tx-secondary select-none whitespace-nowrap">
          {periodLabel}
        </span>
      </div>
    </div>
  );
}
```

### Bước 4.2 — Thay thế trong `AddWebsiteModal.tsx`

Tìm `<input type="time" ...>` dùng cho "Khung giờ cho phép". Thay bằng:

```tsx
// Thêm import:
import { TimeInput24h } from '@/components/ui/TimeInput24h';

// Thay thế:
// ❌ Cũ:
<input
  type="time"
  value={startTime}
  onChange={e => setStartTime(e.target.value)}
  className="..."
/>

// ✅ Mới:
<TimeInput24h
  label="TỪ LÚC"
  value={startTime}
  onChange={setStartTime}
/>
<TimeInput24h
  label="ĐẾN LÚC"
  value={endTime}
  onChange={setEndTime}
/>
```

> ⚠️ Giá trị `startTime` và `endTime` vẫn là string "HH:mm" (24h) — KHÔNG thay đổi cách truyền lên API.

### Bước 4.3 — Thay thế trong `EditWebsiteModal.tsx`

Làm tương tự Bước 4.2. Tìm tất cả `<input type="time">` liên quan đến `allowedStartTime`/`allowedEndTime`.

### Bước 4.4 — Kiểm tra `WarningConfigModal.tsx`

Mở file. Nếu có tab "Khung giờ" dùng time input → thay thế tương tự.

### Bước 4.5 — Dark mode verify cho `TimeInput24h`

Component dùng CSS variables nên tự động hỗ trợ dark mode. Verify:
- `bg-bg-subtle` → đúng trong cả light/dark
- `border-border-base` → đúng
- `text-tx-primary`, `text-tx-secondary` → đúng
- `brand-DEFAULT/60` focus ring → đúng

---

## Thứ tự làm việc

```
BƯỚC 1  — Frontend: Thêm biến isTimeLimitMode/isTimeWindowMode vào WebsiteCard
BƯỚC 2  — Frontend: Sửa điều kiện render bonus phút (hasTimeLimitBonus)
BƯỚC 3  — Frontend: Sửa điều kiện render bonus giờ (hasTimeWindowBonus)
BƯỚC 4  — Frontend: Fix computeExtendedEndTime() đúng logic
BƯỚC 5  — Backend: Thêm LimitMinutes/UsedSeconds/TimeWindowStart/End vào CheckAccessResult
BƯỚC 6  — Backend: Populate các field mới trong CheckAccessAsync
BƯỚC 7  — Extension: Thêm FAST_POLL + SLOW_POLL timer vào blocked.html
BƯỚC 8  — Extension: Thêm updateBlockedUI() + detect config change → reload
BƯỚC 9  — Extension: Thêm HTML elements #block-reason-text + #block-info-detail
BƯỚC 10 — Extension: Verify nút "Gửi yêu cầu" không bị gián đoạn bởi poll
BƯỚC 11 — Frontend: Tạo TimeInput24h.tsx component
BƯỚC 12 — Frontend: Thay input[type=time] trong AddWebsiteModal.tsx
BƯỚC 13 — Frontend: Thay input[type=time] trong EditWebsiteModal.tsx
BƯỚC 14 — Frontend: Kiểm tra WarningConfigModal.tsx và thay nếu có
BƯỚC 15 — Test toàn bộ flow
```

---

## Test sau khi hoàn thành

### Test Fix 1 — Bonus Isolation
```
1. Website A: Giới hạn phút (10p), bonus = 30p
   → WebsiteCard A: hiện "+30 phút gia hạn" (phút), KHÔNG hiện extended time
2. Website B: Khung giờ 07:00-09:00, bonus = 30p
   → WebsiteCard B: hiện "+30 phút — đến 09:30" (giờ), KHÔNG hiện bonus phút
3. Đổi Website A từ Giới hạn phút → Khung giờ
   → WebsiteCard A: hiện khung giờ mới, KHÔNG còn hiện "+30 phút gia hạn" nữa
```

### Test Fix 2 — blocked.html info
```
1. Con bị block vì hết 10 phút
   → blocked.html hiện: "Đã hết 10 phút cho phép hôm nay" + "Đã dùng: 10 phút / 10 phút"
2. Con bị block vì ngoài khung giờ 07:00-09:00
   → blocked.html hiện: "Ngoài khung giờ cho phép" + "Khung giờ: 07:00 – 09:00"
3. Guardian đổi limit từ 10p lên 20p (con vẫn bị chặn)
   → blocked.html tự reload sau ≤20s, hiện limit mới "20 phút"
```

### Test Fix 3 — Nút gửi yêu cầu
```
1. Con đang ở blocked.html
2. Thử nhấn "Gửi yêu cầu xin thêm" trong vòng 8s đầu (giữa 2 fast poll)
3. Nút phải respond ngay, KHÔNG bị mờ hoặc disabled khi poll chạy
```

### Test Fix 4 — Time picker 24h
```
1. Mở AddWebsiteModal → chọn "Khung giờ cho phép"
   → Hiện 2 input HH:MM (không có AM/PM)
   → Nhập giờ tối: 21:00 → hiện "(Tối)" gợi nhớ
   → Nhập giờ sáng: 07:00 → hiện "(Sáng)" gợi nhớ
2. Submit → backend nhận đúng "HH:mm" 24h format
```
