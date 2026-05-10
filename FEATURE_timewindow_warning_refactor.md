# FEATURE: Refactor Tab "Khung Giờ" trong WarningConfigModal + Cảnh Báo Theo Giờ Cụ Thể

> **Trạng thái SQL:** ✅ Đã chạy (xem phần SQL bên dưới — tự chạy trước khi làm backend/frontend)

---

## Tổng quan thay đổi

### Vấn đề hiện tại
1. Tab "Khung giờ" trong `WarningConfigModal` có **section "Khung giờ cho phép"** (input giờ bắt đầu/kết thúc + preview) — đây là chức năng của `EditWebsiteModal`, không phải của modal cảnh báo → cần **xóa bỏ**.
2. Website selector trong tab này hiện `"Chưa giới hạn"` thay vì hiện khung giờ thực tế (vd: `07:00 → 21:00 · 14 giờ/ngày`).
3. Cảnh báo khung giờ chỉ hỗ trợ cảnh báo theo **N phút trước khi hết giờ**. Cần thêm tùy chọn **cảnh báo vào 1 giờ cụ thể** (vd: 20:45). Hai tùy chọn này **loại trừ nhau** (chỉ chọn 1).

### Thay đổi tổng thể
| Layer | File | Thay đổi |
|-------|------|----------|
| DB | `website_timewindow_warning_configs` | Thêm 3 cột: `warn_mode`, `warn_at_time1`, `warn_at_time2` |
| Backend Entity | `WebsiteTimeWindowWarningConfig.cs` | Thêm 3 property mới |
| Backend Service | `ExtensionService.cs` | Update logic heartbeat xử lý `warn_at_time` mode |
| Backend Controller | `TimeWindowWarningConfigController.cs` | Update DTO nhận/trả field mới |
| Frontend API | `timeWindowWarningConfig.api.ts` | Update type + request body |
| Frontend Component | `WarningConfigModal.tsx` | Refactor toàn bộ Tab 2 |

---

## BƯỚC 1: SQL (đã tạo rồi, chỉ tham khảo)

```sql
-- Thêm 3 cột mới vào bảng website_timewindow_warning_configs
ALTER TABLE website_timewindow_warning_configs
  ADD COLUMN warn_mode ENUM('minutes_before', 'at_time') NOT NULL DEFAULT 'minutes_before'
    AFTER is_active,
  ADD COLUMN warn_at_time1 TIME NULL
    AFTER warn_mode,
  ADD COLUMN warn_at_time2 TIME NULL
    AFTER warn_at_time1;
```

**Giải thích:**
- `warn_mode = 'minutes_before'` → dùng `warn_minutes_before1/2` (hành vi cũ, mặc định)
- `warn_mode = 'at_time'` → dùng `warn_at_time1/2` (giờ cụ thể trong ngày)
- Data cũ vẫn hoạt động bình thường (default `minutes_before`)

---

## BƯỚC 2: Backend

### 2.1 Entity — `WebsiteTimeWindowWarningConfig.cs`

Thêm 3 property mới vào entity hiện tại (KHÔNG xóa/sửa property cũ):

```csharp
// Thêm vào class WebsiteTimeWindowWarningConfig
public string WarnMode { get; set; } = "minutes_before"; // "minutes_before" | "at_time"
public TimeOnly? WarnAtTime1 { get; set; }
public TimeOnly? WarnAtTime2 { get; set; }
```

Trong `AppDbContext.cs`, nếu có config Fluent API cho bảng này thì thêm:
```csharp
entity.Property(e => e.WarnMode).HasDefaultValue("minutes_before");
entity.Property(e => e.WarnAtTime1).HasColumnName("warn_at_time1");
entity.Property(e => e.WarnAtTime2).HasColumnName("warn_at_time2");
```

---

### 2.2 Controller DTO — `TimeWindowWarningConfigController.cs`

#### Request DTO — thêm 3 field mới (giữ nguyên field cũ):

```csharp
public class UpsertTimeWindowWarningConfigRequest
{
    public int AllowedWebsiteId { get; set; }

    // === Mode chọn 1 trong 2 ===
    // "minutes_before" (default) hoặc "at_time"
    public string WarnMode { get; set; } = "minutes_before";

    // --- Mode: minutes_before ---
    public int? WarnMinutesBefore1 { get; set; }
    public string? Message1 { get; set; }
    public int? WarnMinutesBefore2 { get; set; }
    public string? Message2 { get; set; }

    // --- Mode: at_time ---
    // Format "HH:mm" vd: "20:45"
    public string? WarnAtTime1 { get; set; }
    public string? WarnAtTimeMessage1 { get; set; }
    public string? WarnAtTime2 { get; set; }
    public string? WarnAtTimeMessage2 { get; set; }

    public bool IsActive { get; set; } = true;
}
```

#### Response DTO — thêm field mới vào response:
```csharp
// Trong response object (hoặc return trực tiếp entity), thêm các field:
// WarnMode, WarnAtTime1 (string "HH:mm"), WarnAtTime2 (string "HH:mm")
```

#### Logic upsert — trong action POST:
```csharp
config.WarnMode = request.WarnMode;
if (request.WarnMode == "at_time")
{
    config.WarnAtTime1 = request.WarnAtTime1 != null
        ? TimeOnly.Parse(request.WarnAtTime1) : null;
    config.WarnAtTime2 = request.WarnAtTime2 != null
        ? TimeOnly.Parse(request.WarnAtTime2) : null;
    config.Message1 = request.WarnAtTimeMessage1;
    config.Message2 = request.WarnAtTimeMessage2;
    // Clear minutes_before fields
    config.WarnMinutesBefore1 = null;
    config.WarnMinutesBefore2 = null;
}
else // minutes_before
{
    config.WarnMinutesBefore1 = request.WarnMinutesBefore1;
    config.WarnMinutesBefore2 = request.WarnMinutesBefore2;
    config.Message1 = request.Message1;
    config.Message2 = request.Message2;
    // Clear at_time fields
    config.WarnAtTime1 = null;
    config.WarnAtTime2 = null;
}
```

---

### 2.3 Service — `ExtensionService.cs` (Phần xử lý heartbeat khung giờ)

> ⚠️ KHÔNG thay đổi logic `minutes_before` cũ. Chỉ thêm nhánh `at_time`.

Tìm đoạn code xử lý `tw_warning1_sent` / `tw_warning2_sent` trong `UpdateHeartbeatAsync`. Thêm nhánh xử lý `at_time`:

```csharp
// === Xử lý cảnh báo khung giờ ===
if (website.AllowedStartTime != null && twConfig != null && twConfig.IsActive)
{
    var now = TimeOnly.FromDateTime(DateTime.Now);
    var endTime = TimeOnly.FromTimeSpan(website.AllowedEndTime!.Value);

    if (twConfig.WarnMode == "at_time")
    {
        // --- Mode: at_time ---
        // Mốc 1
        if (twConfig.WarnAtTime1 != null && !stats.TwWarning1Sent)
        {
            // Cảnh báo nếu giờ hiện tại >= WarnAtTime1 và WarnAtTime1 nằm trong khung giờ
            if (now >= twConfig.WarnAtTime1.Value && now <= endTime)
            {
                stats.TwWarning1Sent = true;
                await _context.SaveChangesAsync();
                await SendWarningNotificationAsync(..., twConfig.Message1);
                result.Warning = new HeartbeatWarning
                {
                    Message = twConfig.Message1 ?? "Sắp hết khung giờ!",
                    RemainingSeconds = (int)(endTime - now).TotalSeconds
                };
            }
        }
        // Mốc 2
        if (twConfig.WarnAtTime2 != null && !stats.TwWarning2Sent)
        {
            if (now >= twConfig.WarnAtTime2.Value && now <= endTime)
            {
                stats.TwWarning2Sent = true;
                await _context.SaveChangesAsync();
                await SendWarningNotificationAsync(..., twConfig.Message2);
                if (result.Warning == null)
                {
                    result.Warning = new HeartbeatWarning
                    {
                        Message = twConfig.Message2 ?? "Sắp hết khung giờ!",
                        RemainingSeconds = (int)(endTime - now).TotalSeconds
                    };
                }
            }
        }
    }
    else // minutes_before (logic cũ — KHÔNG đổi)
    {
        // ... giữ nguyên logic hiện tại với minutesUntilEnd <= warnMinutesBefore1 ...
    }
}
```

**Lưu ý quan trọng:**
- Thứ tự xử lý: Warning check **TRƯỚC** khi check `limitExceeded` (giữ nguyên nguyên tắc cũ)
- `tw_warning1_sent` / `tw_warning2_sent` reset mỗi ngày bởi logic hiện tại → không cần thay đổi
- `warn_at_time` phải nằm **trong khung giờ cho phép** thì mới có nghĩa (validate ở frontend)

---

## BƯỚC 3: Frontend

### 3.1 API Type — `timeWindowWarningConfig.api.ts`

Cập nhật interface và request:

```typescript
// Existing type — thêm field mới
export interface TimeWindowWarningConfig {
  id: number;
  allowedWebsiteId: number;
  warnMode: 'minutes_before' | 'at_time'; // MỚI
  // minutes_before mode
  warnMinutesBefore1: number | null;
  message1: string | null;
  warnMinutesBefore2: number | null;
  message2: string | null;
  // at_time mode — MỚI
  warnAtTime1: string | null;   // "HH:mm"
  warnAtTime2: string | null;   // "HH:mm"
  isActive: boolean;
}

// Request body — thêm field mới (giữ field cũ)
export interface UpsertTimeWindowWarningConfigRequest {
  allowedWebsiteId: number;
  warnMode: 'minutes_before' | 'at_time'; // MỚI
  // minutes_before
  warnMinutesBefore1?: number;
  message1?: string;
  warnMinutesBefore2?: number;
  message2?: string;
  // at_time — MỚI
  warnAtTime1?: string;          // "HH:mm"
  warnAtTimeMessage1?: string;
  warnAtTime2?: string;          // "HH:mm"
  warnAtTimeMessage2?: string;
  isActive?: boolean;
}
```

---

### 3.2 Component — `WarningConfigModal.tsx` (Tab 2 — Khung giờ)

#### Thay đổi cần làm trong Tab 2:

**A. Xóa bỏ "Section Khung giờ cho phép" (bước 2 cũ)**
- Xóa toàn bộ UI với input `allowedStartTime` / `allowedEndTime` và preview "Con được dùng từ..."
- Xóa state liên quan: `startTime`, `endTime`, mutation `updateWebsiteTime` (nếu có)
- Xóa API call `PUT /{childId}/websites/{websiteId}` trong modal này

**B. Website selector — hiện thông tin khung giờ thay vì "Chưa giới hạn"**

Thay dòng hiện `"Chưa giới hạn"` (thường là subtitle của website card trong selector):

```tsx
// TRƯỚC:
<span className="text-xs text-tx-secondary">Chưa giới hạn</span>

// SAU — hiện khung giờ từ data website:
{website.allowedStartTime && website.allowedEndTime ? (
  <span className="text-xs text-tx-secondary">
    {formatTimeRange(website.allowedStartTime, website.allowedEndTime)}
  </span>
) : (
  <span className="text-xs text-tx-secondary">Không có khung giờ</span>
)}
```

Thêm helper function (đặt trong file hoặc formatters.ts):
```typescript
// Hiện "07:00 → 21:00 · 14 giờ/ngày"
function formatTimeRange(start: string, end: string): string {
  const fmt = (t: string) => t.substring(0, 5); // "07:00:00" → "07:00"
  const startH = parseInt(start.split(':')[0]);
  const startM = parseInt(start.split(':')[1]);
  const endH = parseInt(end.split(':')[0]);
  const endM = parseInt(end.split(':')[1]);
  const totalMins = (endH * 60 + endM) - (startH * 60 + startM);
  const hours = Math.floor(totalMins / 60);
  const mins = totalMins % 60;
  const durationStr = mins > 0 ? `${hours} giờ ${mins} phút` : `${hours} giờ`;
  return `${fmt(start)} → ${fmt(end)} · ${durationStr}/ngày`;
}
```

**C. Renumber bước — Cảnh báo là bước 2 (không còn bước về khung giờ)**

Số thứ tự trong UI: `1` (Chọn website) → `2` (Cảnh báo) [trước là 3]

**D. Thêm tùy chọn "Cảnh báo vào giờ cụ thể" (loại trừ nhau với "Trước N phút")**

Thêm state mới vào Tab 2:
```typescript
const [warnMode, setWarnMode] = useState<'minutes_before' | 'at_time'>('minutes_before');
const [warnAtTime1, setWarnAtTime1] = useState('');
const [warnAtTimeMessage1, setWarnAtTimeMessage1] = useState('');
const [warnAtTime2, setWarnAtTime2] = useState('');
const [warnAtTimeMessage2, setWarnAtTimeMessage2] = useState('');
```

Khi load config hiện tại:
```typescript
useEffect(() => {
  if (twConfig) {
    setWarnMode(twConfig.warnMode ?? 'minutes_before');
    setWarnAtTime1(twConfig.warnAtTime1 ?? '');
    setWarnAtTime2(twConfig.warnAtTime2 ?? '');
    // ... load các field còn lại
  }
}, [twConfig]);
```

**UI cho phần cảnh báo (bước 2 mới):**

```tsx
{/* Toggle chọn mode — 2 nút loại trừ nhau */}
<div className="flex gap-2 p-1 bg-bg-subtle rounded-lg">
  <button
    onClick={() => setWarnMode('minutes_before')}
    className={cn(
      'flex-1 py-2 px-3 rounded-md text-sm font-medium transition-all',
      warnMode === 'minutes_before'
        ? 'bg-brand-DEFAULT text-white shadow-sm'
        : 'text-tx-secondary hover:text-tx-primary'
    )}
  >
    ⏱ Trước N phút khi hết giờ
  </button>
  <button
    onClick={() => setWarnMode('at_time')}
    className={cn(
      'flex-1 py-2 px-3 rounded-md text-sm font-medium transition-all',
      warnMode === 'at_time'
        ? 'bg-brand-DEFAULT text-white shadow-sm'
        : 'text-tx-secondary hover:text-tx-primary'
    )}
  >
    🕐 Vào giờ cụ thể
  </button>
</div>

{/* Mode: minutes_before — giữ nguyên UI cũ */}
{warnMode === 'minutes_before' && (
  <div>{/* ... UI input số phút hiện tại ... */}</div>
)}

{/* Mode: at_time — UI mới */}
{warnMode === 'at_time' && (
  <div className="space-y-3">
    {/* Mốc 1 */}
    <div className="space-y-2">
      <label className="text-sm font-medium text-tx-primary">Cảnh báo lúc</label>
      <input
        type="time"
        value={warnAtTime1}
        onChange={(e) => setWarnAtTime1(e.target.value)}
        className="w-full px-3 py-2 rounded-lg border border-border-base bg-bg-surface
                   text-tx-primary focus:outline-none focus:border-brand-DEFAULT"
      />
      <textarea
        value={warnAtTimeMessage1}
        onChange={(e) => setWarnAtTimeMessage1(e.target.value)}
        placeholder="Nội dung cảnh báo... (vd: Sắp hết thời gian dùng mạng hôm nay!)"
        className="w-full px-3 py-2 rounded-lg border border-border-base bg-bg-surface
                   text-tx-primary text-sm resize-none h-16
                   focus:outline-none focus:border-brand-DEFAULT"
      />
    </div>

    {/* Mốc 2 — tùy chọn */}
    {/* Toggle thêm mốc 2 giống pattern hiện tại */}
    <div>
      {showAtTime2 ? (
        <div className="space-y-2">
          <label className="text-sm font-medium text-tx-primary">Cảnh báo thêm lúc (tùy chọn)</label>
          <input type="time" value={warnAtTime2} onChange={(e) => setWarnAtTime2(e.target.value)} ... />
          <textarea value={warnAtTimeMessage2} ... />
          <button onClick={() => setShowAtTime2(false)} className="text-xs text-red-400">
            − Xóa mốc 2
          </button>
        </div>
      ) : (
        <button onClick={() => setShowAtTime2(true)}
          className="text-xs text-brand-DEFAULT hover:underline">
          + Thêm mốc cảnh báo thứ 2
        </button>
      )}
    </div>

    {/* Validate hint: giờ cảnh báo phải nằm trong khung giờ cho phép */}
    {selectedWebsite && warnAtTime1 && (
      <p className="text-xs text-tx-secondary">
        💡 Giờ cảnh báo nên nằm trong khung giờ {formatTimeRange(selectedWebsite.allowedStartTime!, selectedWebsite.allowedEndTime!)}
      </p>
    )}
  </div>
)}
```

**Khi submit (lưu config):**
```typescript
const requestBody: UpsertTimeWindowWarningConfigRequest = {
  allowedWebsiteId: selectedWebsiteId,
  warnMode,
  isActive: true,
  ...(warnMode === 'minutes_before' ? {
    warnMinutesBefore1: minutesBefore1,
    message1: message1,
    warnMinutesBefore2: showMoc2 ? minutesBefore2 : undefined,
    message2: showMoc2 ? message2 : undefined,
  } : {
    warnAtTime1: warnAtTime1 || undefined,
    warnAtTimeMessage1: warnAtTimeMessage1 || undefined,
    warnAtTime2: showAtTime2 ? (warnAtTime2 || undefined) : undefined,
    warnAtTimeMessage2: showAtTime2 ? (warnAtTimeMessage2 || undefined) : undefined,
  })
};
```

---

## BƯỚC 4: Dark mode — Kiểm tra các class cần dùng

Tất cả element mới trong WarningConfigModal phải dùng CSS variables:

| Mục đích | Class Tailwind |
|----------|---------------|
| Background input | `bg-bg-surface` |
| Background container | `bg-bg-elevated` hoặc `bg-bg-subtle` |
| Text chính | `text-tx-primary` |
| Text phụ | `text-tx-secondary` |
| Border | `border-border-base` |
| Brand color | `bg-brand-DEFAULT` / `text-brand-DEFAULT` |
| Focus border | `focus:border-brand-DEFAULT` |

**KHÔNG dùng:** `bg-white`, `bg-gray-*`, `text-gray-*`, `bg-purple-*` — những màu cứng này sẽ vỡ dark mode.

Toggle button mode (minutes_before vs at_time) phải dùng pattern:
- Active: `bg-brand-DEFAULT text-white`
- Inactive: `text-tx-secondary hover:text-tx-primary`
- Container: `bg-bg-subtle rounded-lg`

---

## BƯỚC 5: Kiểm tra không phá logic cũ

### Backend checklist:
- [ ] `WarnMode` default `'minutes_before'` → data cũ không bị ảnh hưởng
- [ ] Heartbeat: warning check vẫn chạy **TRƯỚC** `limitExceeded` check
- [ ] Không sửa `sp_ExtensionCheckAccess`
- [ ] Không thay đổi `tw-warning-ack` endpoint

### Frontend checklist:
- [ ] Tab 1 (Giới hạn phút) trong `WarningConfigModal` → **không chạm vào**
- [ ] `EditWebsiteModal` → **không chạm vào**
- [ ] `AddWebsiteModal` → **không chạm vào**
- [ ] `WebsiteCard.tsx` → **không chạm vào**
- [ ] Extension `background.js` → **không chạm vào**
- [ ] Khi reset tab 2, mode mặc định về `'minutes_before'`
- [ ] Load config hiện tại đúng: nếu `warnMode = 'at_time'` thì UI hiện đúng section

---

## Tóm tắt thứ tự làm

1. **Chạy SQL** (nếu chưa chạy)
2. **Backend:**
   - Sửa entity `WebsiteTimeWindowWarningConfig.cs`
   - Sửa Controller DTO + upsert logic
   - Sửa Service heartbeat (thêm nhánh `at_time`, giữ nhánh `minutes_before`)
3. **Frontend:**
   - Sửa `timeWindowWarningConfig.api.ts` (type mới)
   - Sửa `WarningConfigModal.tsx` Tab 2 (xóa section khung giờ, sửa selector label, thêm mode toggle)
4. **Test:**
   - Tạo config `minutes_before` → heartbeat vẫn cảnh báo đúng
   - Tạo config `at_time` → heartbeat cảnh báo vào đúng giờ
   - Dark mode: kiểm tra cả 2 mode không có màu cứng
