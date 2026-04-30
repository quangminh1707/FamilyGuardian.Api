# ⏰ Tính năng: Khung giờ cho phép & Cải tiến Edit Website & Extension Overlay

> **SQL đã tạo xong** — Xem phần SQL ở cuối file, tự chạy trước khi làm backend/frontend.  
> **Không thay đổi logic đang chạy** — chỉ thêm mới, không sửa sp_ExtensionCheckAccess.  
> **Dark mode:** Tất cả component mới phải dùng CSS variable (`bg-bg-surface`, `text-tx-primary`...) đã định nghĩa trong `src/styles/themes/`.

---

## Tổng quan DB hiện có (đã biết)

```
allowed_websites:
  - time_limit_minutes  INT NULL        ← Giới hạn phút/ngày
  - allowed_start_time  TIME NULL       ← Khung giờ bắt đầu  
  - allowed_end_time    TIME NULL       ← Khung giờ kết thúc

Quy tắc: 1 website chỉ dùng 1 trong 2 tính năng:
  - time_limit_minutes != NULL  → đang dùng "Giới hạn phút"
  - allowed_start_time != NULL  → đang dùng "Khung giờ"
  - Cả 2 đều NULL               → không giới hạn

website_warning_configs: cảnh báo theo % cho giới hạn phút (đã có)
website_timewindow_warning_configs: (MỚI) cảnh báo theo phút trước khi hết khung giờ
daily_usage_stats: thêm tw_warning1_sent, tw_warning2_sent (MỚI)
```

---

## SQL đã tạo (đã chạy xong, chỉ để tham khảo)

```sql
-- Bảng cảnh báo khung giờ (tương tự website_warning_configs nhưng cho time window)
CREATE TABLE website_timewindow_warning_configs (
  id                   INT AUTO_INCREMENT PRIMARY KEY,
  allowed_website_id   INT          NOT NULL,
  warn_minutes_before1 INT          NOT NULL DEFAULT 10
    COMMENT 'Cảnh báo trước N phút khi hết khung giờ — mốc 1',
  message1             VARCHAR(500) NOT NULL,
  warn_minutes_before2 INT          NULL     COMMENT 'Mốc 2 tùy chọn',
  message2             VARCHAR(500) NULL,
  is_active            TINYINT(1)   NOT NULL DEFAULT 1,
  created_at           TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at           TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  UNIQUE KEY uq_ttwc (allowed_website_id),
  FOREIGN KEY (allowed_website_id) REFERENCES allowed_websites(id) ON DELETE CASCADE
);

-- Tracking đã gửi cảnh báo khung giờ hôm nay chưa
ALTER TABLE daily_usage_stats
  ADD COLUMN tw_warning1_sent TINYINT(1) NOT NULL DEFAULT 0
    COMMENT 'Đã gửi cảnh báo khung giờ mốc 1 hôm nay',
  ADD COLUMN tw_warning2_sent TINYINT(1) NOT NULL DEFAULT 0
    COMMENT 'Đã gửi cảnh báo khung giờ mốc 2 hôm nay';
```

---

## Backend

### 1. Entity & DbContext

**Tạo file `Models/Entities/WebsiteTimeWindowWarningConfig.cs`:**
```csharp
[Table("website_timewindow_warning_configs")]
public class WebsiteTimeWindowWarningConfig
{
    [Key][Column("id")] public int Id { get; set; }
    [Column("allowed_website_id")] public int AllowedWebsiteId { get; set; }
    [Column("warn_minutes_before1")] public int WarnMinutesBefore1 { get; set; } = 10;
    [Column("message1")] public string Message1 { get; set; } = string.Empty;
    [Column("warn_minutes_before2")] public int? WarnMinutesBefore2 { get; set; }
    [Column("message2")] public string? Message2 { get; set; }
    [Column("is_active")] public bool IsActive { get; set; } = true;
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.Now;
    [Column("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public AllowedWebsite? AllowedWebsite { get; set; }
}
```

**Thêm vào `DailyUsageStat.cs`:**
```csharp
[Column("tw_warning1_sent")] public bool TwWarning1Sent { get; set; } = false;
[Column("tw_warning2_sent")] public bool TwWarning2Sent { get; set; } = false;
```

**Thêm vào `AppDbContext.cs`:**
```csharp
public DbSet<WebsiteTimeWindowWarningConfig> WebsiteTimeWindowWarningConfigs { get; set; }
```

---

### 2. API cho Time Window Warning Config

**Tạo `Controllers/TimeWindowWarningConfigController.cs`:**

| Method | Route | Mô tả |
|--------|-------|-------|
| GET | `/api/timewindow-warning-configs?allowedWebsiteId={id}` | Lấy config của 1 website (204 nếu chưa có) |
| GET | `/api/timewindow-warning-configs/by-child/{childId}` | Tất cả config của con, join lấy domain |
| POST | `/api/timewindow-warning-configs` | Upsert (tạo hoặc cập nhật) |
| DELETE | `/api/timewindow-warning-configs/{id}` | Xóa |

**Request body POST:**
```json
{
  "allowedWebsiteId": 30,
  "warnMinutesBefore1": 10,
  "message1": "Sắp hết khung giờ rồi con ơi!",
  "warnMinutesBefore2": 5,
  "message2": "Còn 5 phút nữa là hết giờ!"
}
```

**Validation:**
- `warnMinutesBefore1` phải > 0
- Nếu có mốc 2: `warnMinutesBefore2` phải > 0 và < `warnMinutesBefore1` (mốc 2 gần hết hơn mốc 1)
- Website phải có `allowed_start_time` và `allowed_end_time` (đang dùng khung giờ)

---

### 3. API cập nhật website — thêm time window

**Tìm endpoint `PUT /api/children/{childId}/websites/{websiteId}` hiện có.**  
Mở rộng request body để nhận cả 2 loại tính năng — và xử lý "chuyển đổi":

```csharp
public class UpdateWebsiteRequest
{
    // Giới hạn phút (null = không giới hạn)
    public int? TimeLimitMinutes { get; set; }

    // Khung giờ (null = không dùng khung giờ)
    public TimeSpan? AllowedStartTime { get; set; }
    public TimeSpan? AllowedEndTime { get; set; }
}
```

**Logic trong service khi update:**
```
Nếu request có TimeLimitMinutes != null:
  → Gán time_limit_minutes = value
  → Xóa allowed_start_time = NULL, allowed_end_time = NULL  ← xóa khung giờ cũ nếu có

Nếu request có AllowedStartTime != null:
  → Gán allowed_start_time, allowed_end_time
  → Xóa time_limit_minutes = NULL  ← xóa giới hạn phút cũ nếu có

Nếu cả 2 đều null:
  → Xóa hết cả 2 (không giới hạn)
```

---

### 4. Cập nhật `ExtensionService.UpdateHeartbeatAsync`

Sau phần check `website_warning_configs` (giới hạn phút) hiện có, thêm block mới cho khung giờ:

```
Nếu website có allowed_start_time và allowed_end_time:
    Tính minutesUntilEnd = phút từ NOW() đến allowed_end_time
    Nếu minutesUntilEnd < 0 → đã qua khung giờ (sp đã handle block rồi, bỏ qua)

    Lấy WebsiteTimeWindowWarningConfig
    Nếu config != null:

        Mốc 1: nếu !tw_warning1_sent AND minutesUntilEnd <= config.WarnMinutesBefore1
            → TwWarning1Sent = true
            → SaveChanges()
            → SendWarningNotificationAsync(child, website, config.Message1, minutesUntilEnd * 60)
            → result.Warning = new HeartbeatWarning { Message = config.Message1, RemainingSeconds = minutesUntilEnd * 60 }

        Mốc 2: nếu config.WarnMinutesBefore2 != null AND !tw_warning2_sent
                AND minutesUntilEnd <= config.WarnMinutesBefore2
            → TwWarning2Sent = true, tương tự mốc 1

    Thêm vào HeartbeatResult:
    result.TimeWindow = website.AllowedStartTime + "→" + website.AllowedEndTime  ← extension dùng
    result.MinutesUntilWindowEnd = minutesUntilEnd  ← extension dùng
```

**Thêm vào `HeartbeatResult`:**
```csharp
public string? TimeWindowDisplay { get; set; }   // "10:00 → 12:00"
public int? MinutesUntilWindowEnd { get; set; }  // phút còn lại đến cuối khung giờ
public int? MinutesRemainingToday { get; set; }  // phút còn lại hôm nay (giới hạn phút)
```

**Cập nhật `ExtensionController.SendHeartbeat` trả thêm:**
```json
{
  "success": true,
  "limitExceeded": false,
  "warning": null,
  "schedule": { ... },
  "timeInfo": {
    "mode": "timeWindow",          // hoặc "minuteLimit" hoặc null
    "timeWindowDisplay": "10:00 → 12:00",
    "minutesUntilWindowEnd": 45,
    "minutesRemainingToday": null
  }
}
```

---

### 5. Thêm `MarkTimeWindowWarningSentAsync` vào `IExtensionService`

Tương tự `MarkWarningSentAsync` nhưng update `tw_warning1_sent` / `tw_warning2_sent`.

**Thêm endpoint `POST /api/extension/tw-warning-ack`:**
```
Query params: allowedWebsiteId, warningNumber
Logic: giống /warning-ack nhưng set tw_warning1_sent hoặc tw_warning2_sent
```

---

## Frontend

### 1. Tách "Khung giờ" ra nút riêng trong ChildDetailPage

**Frontend — `ChildDetailPage.tsx`:**

Khu vực hiện có nút "+ THÊM WEBSITE" → thêm nút thứ 2 kế bên:
```tsx
<Button variant="outline" onClick={() => setShowTimeWindowModal(true)}>
  <Clock className="w-4 h-4" />
  Khung giờ
</Button>
```

---

### 2. Tạo `TimeWindowModal.tsx`

**File:** `src/components/TimeWindowModal.tsx`

**Props:**
```typescript
interface Props {
  childId: number;
  childName: string;
  // Danh sách website (kể cả chưa có khung giờ) để guardian chọn
  websites: { id: number; domain: string; allowedStartTime?: string; allowedEndTime?: string }[];
  onClose: () => void;
}
```

**UI gồm 3 phần:**

**Phần 1 — Chọn website:**
- Hiện tất cả website của con (không lọc theo có time limit)
- Website đang dùng khung giờ → hiện badge "⏰ Đang có khung giờ"
- Website đang dùng giới hạn phút → hiện badge "⚠ Đang dùng giới hạn phút" + tooltip "Lưu sẽ xóa giới hạn phút"
- Chọn 1 website → load khung giờ sẵn có vào form nếu có

**Phần 2 — Thiết lập khung giờ:**
```
Giờ bắt đầu: [input type="time"]
Giờ kết thúc: [input type="time"]
Validate: end > start
Preview: "Con được dùng từ 10:00 đến 12:00 (2 giờ/ngày)"
```

**Phần 3 — Cảnh báo khung giờ (tùy chọn, tương tự WarningConfigModal):**
```
Cảnh báo trước [X] phút khi hết khung giờ
Nội dung: [textarea]
+ Thêm mốc 2 (tùy chọn)
```

**Mutation khi Lưu:**
```typescript
// Bước 1: Update website (xóa time_limit_minutes, set start/end time)
await childrenApi.updateWebsite(childId, websiteId, {
  timeLimitMinutes: null,
  allowedStartTime: "10:00:00",
  allowedEndTime: "12:00:00"
});

// Bước 2: Upsert timewindow warning config (nếu có thiết lập cảnh báo)
await timeWindowWarningConfigApi.upsert({ ... });

toast.success('Đã lưu khung giờ cho phép');
```

---

### 3. Cải tiến `EditWebsiteModal.tsx` (Smart Edit)

**File:** `src/components/EditWebsiteModal.tsx`

Modal nhận `website` object đầy đủ. Logic hiển thị dựa vào trạng thái hiện tại:

```
Xác định currentMode:
  - website.timeLimitMinutes != null  → mode = 'minuteLimit'
  - website.allowedStartTime != null  → mode = 'timeWindow'
  - cả 2 null                         → mode = 'none'
```

**UI cho mode = 'minuteLimit':**
```
Header: "Chỉnh sửa — Giới hạn phút"
[ Icon ⏱ ]  uiverse.io

Giới hạn phút:
[ input number ]  phút/ngày
(Để trống = không giới hạn)

─── Chuyển sang Khung giờ ───────────────────────────
[Nút "Dùng Khung giờ thay thế"] → mở phần time window bên dưới

Buttons: [Hủy] [Lưu thay đổi]
```

**UI cho mode = 'timeWindow':**
```
Header: "Chỉnh sửa — Khung giờ"
[ Icon 🕐 ]  uiverse.io

Giờ bắt đầu: [input time]
Giờ kết thúc: [input time]
Preview: "Con được dùng từ 10:00 đến 12:00"

─── Chuyển sang Giới hạn phút ───────────────────────
[Nút "Dùng Giới hạn phút thay thế"] → mở input phút bên dưới

Buttons: [Hủy] [Lưu thay đổi]
```

**Khi nhấn "Chuyển đổi":**
- Hiện thêm form tương ứng bên dưới
- Khi lưu: gửi loại mới, xóa loại cũ (backend tự xử lý theo logic mục Backend #3)

**Confirm trước khi lưu nếu đang chuyển đổi:**
```
Mở ConfirmModal:
title: "Xác nhận thay đổi tính năng"
message: "Chuyển sang Giới hạn phút sẽ xóa khung giờ hiện tại. Tiếp tục?"
variant: "warning"
```

---

### 4. Cập nhật `WebsiteCard.tsx`

Hiện thêm thông tin trong card:
- Nếu `allowedStartTime != null` → hiện "⏰ 10:00 → 12:00" thay vì progress bar phút
- Nếu `timeLimitMinutes != null` → giữ nguyên progress bar như hiện tại
- Nút ✏️ edit → mở `EditWebsiteModal` (smart edit)

---

### 5. Tạo `timeWindowWarningConfig.api.ts`

```typescript
export interface TimeWindowWarningConfig {
  id: number;
  allowedWebsiteId: number;
  domain?: string;
  warnMinutesBefore1: number;
  message1: string;
  warnMinutesBefore2?: number | null;
  message2?: string | null;
  isActive: boolean;
}

export const timeWindowWarningConfigApi = {
  getByWebsite: (allowedWebsiteId: number) =>
    axios.get<TimeWindowWarningConfig>(`/timewindow-warning-configs`, { params: { allowedWebsiteId } }),

  getByChild: (childId: number) =>
    axios.get<TimeWindowWarningConfig[]>(`/timewindow-warning-configs/by-child/${childId}`),

  upsert: (payload: {
    allowedWebsiteId: number;
    warnMinutesBefore1: number;
    message1: string;
    warnMinutesBefore2?: number | null;
    message2?: string | null;
  }) => axios.post(`/timewindow-warning-configs`, payload),

  delete: (id: number) => axios.delete(`/timewindow-warning-configs/${id}`),
};
```

---

## Extension — `background.js`

> **Không thay đổi logic chặn, heartbeat, ping đang chạy.**  
> Chỉ thêm: đọc `data.timeInfo` từ heartbeat response để hiện overlay.

### 1. Hiển thị overlay thông tin thời gian trong trang

Thêm hàm `showTimeInfoOverlay(tabId, timeInfo)`:
- Vị trí: **góc dưới phải**, nhỏ, bán trong suốt, không che nội dung
- Tự động cập nhật mỗi 30s (theo heartbeat)
- Click vào overlay thì ẩn đi

```javascript
function showTimeInfoOverlay(tabId, timeInfo) {
  if (!tabId || !timeInfo || !timeInfo.mode) return;

  let text = '';
  if (timeInfo.mode === 'timeWindow' && timeInfo.timeWindowDisplay) {
    const mins = timeInfo.minutesUntilWindowEnd;
    text = `⏰ Khung giờ: ${timeInfo.timeWindowDisplay}`;
    if (mins != null && mins > 0) text += ` · Còn ${mins} phút`;
  } else if (timeInfo.mode === 'minuteLimit' && timeInfo.minutesRemainingToday != null) {
    text = `⏱ Còn ${timeInfo.minutesRemainingToday} phút hôm nay`;
  }

  if (!text) return;

  chrome.scripting.executeScript({
    target: { tabId },
    func: (text) => {
      let el = document.getElementById('__fg_time_overlay__');
      if (!el) {
        el = document.createElement('div');
        el.id = '__fg_time_overlay__';
        Object.assign(el.style, {
          position: 'fixed', bottom: '16px', right: '16px',
          zIndex: '2147483646',  // 1 dưới banner warning
          background: 'rgba(0,0,0,0.65)',
          backdropFilter: 'blur(8px)',
          color: '#fff',
          fontSize: '12px', fontWeight: '600',
          padding: '6px 12px', borderRadius: '20px',
          fontFamily: '-apple-system, sans-serif',
          cursor: 'pointer',
          transition: 'opacity 0.3s',
          userSelect: 'none',
        });
        el.onclick = () => el.remove();
        document.body.appendChild(el);
      }
      el.textContent = text;
    },
    args: [text]
  }).catch(() => {}); // Bỏ qua nếu không inject được
}
```

### 2. Gọi overlay trong heartbeat handler

Trong `chrome.alarms.onAlarm.addListener`, sau khi nhận heartbeat response:

```javascript
// Thêm sau phần xử lý data.warning, TRƯỚC phần check data.limitExceeded
if (data.timeInfo) {
  const tabs = await chrome.tabs.query({ active: true, currentWindow: true });
  const tabId = tabs[0]?.id ?? activeTab?.tabId ?? null;
  showTimeInfoOverlay(tabId, data.timeInfo);
}
```

### 3. Không cần thêm permission mới

`scripting` đã có trong manifest rồi.

---

## Checklist hoàn thành

### SQL
- [x] Bảng `website_timewindow_warning_configs` đã tạo
- [x] Cột `tw_warning1_sent`, `tw_warning2_sent` trong `daily_usage_stats` đã thêm

### Backend
- [x] Entity `WebsiteTimeWindowWarningConfig.cs`
- [x] Thêm 2 property `TwWarning1Sent`, `TwWarning2Sent` vào `DailyUsageStat.cs`
- [x] Thêm `DbSet` vào `AppDbContext.cs`
- [x] `TimeWindowWarningConfigController.cs` (4 endpoint)
- [x] `UpdateWebsiteRequest` mở rộng nhận cả time window
- [x] Service logic "chuyển đổi" (gán cái mới, xóa cái cũ)
- [x] `UpdateHeartbeatAsync` thêm block check time window warning
- [x] Thêm `TimeWindowDisplay`, `MinutesUntilWindowEnd`, `MinutesRemainingToday` vào `HeartbeatResult`
- [x] Controller trả `timeInfo` trong heartbeat response
- [x] Endpoint `/api/extension/tw-warning-ack`
- [x] Thêm `MarkTimeWindowWarningSentAsync` vào `IExtensionService`

### Frontend
- [x] `timeWindowWarningConfig.api.ts`
- [x] `TimeWindowModal.tsx` (chọn website + set giờ + cảnh báo)
- [x] `EditWebsiteModal.tsx` smart edit (minute↔timewindow)
- [x] `WebsiteCard.tsx` hiện khung giờ thay progress bar nếu dùng time window
- [x] Nút "Khung giờ" trong `ChildDetailPage.tsx` kế nút Thêm website
- [x] Tất cả component mới dùng CSS variable dark mode

### Extension
- [x] Hàm `showTimeInfoOverlay` trong `background.js`
- [x] Gọi overlay sau mỗi heartbeat response
- [x] Không thay đổi logic chặn/ping/heartbeat hiện có

