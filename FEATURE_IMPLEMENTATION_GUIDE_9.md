# Family Guardian — Fix API + 2 tính năng mới (Phần 9)

> **Ngày tạo:** 2026-05-16  
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE_8.md (Phần 8)

---

## ⚠️ Quy tắc bất di bất dịch

| Quy tắc | Chi tiết |
|---------|---------|
| `sp_ExtensionCheckAccess` | Tuyệt đối KHÔNG sửa |
| `background.js` | KHÔNG thay đổi logic block/allow/heartbeat đang chạy |
| `DateTime.Now` | KHÔNG đổi sang `UtcNow` |
| Dark mode | Dùng CSS variables, KHÔNG hardcode màu |

---

## 🔍 Phân tích lỗi từ log

**Lỗi 1:**
```
Unknown column 'a.temp_expires_at' in 'field list'
at ExtensionMonitorService.CleanupExpiredTempAccess() — line 108
```
→ Bảng `allowed_websites` thiếu cột `temp_expires_at`. Entity C# đã có property, DB chưa có cột.

**Lỗi 2:**
```
Unknown column 'u0.internet_paused' in 'field list'
at ExtensionMonitorService.CheckExtensions() — line 47
```
→ Bảng `users` thiếu cột `internet_paused`. Entity C# đã có property, DB chưa có cột.

**Cả 2 lỗi:** Chỉ cần thêm cột vào DB bằng SQL. KHÔNG cần sửa code C#.

---

## 📦 SQL — Chạy theo thứ tự, note lại là đã xong

### SQL 1 — Thêm cột `temp_expires_at` vào `allowed_websites`

```sql
-- Kiểm tra trước
SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS 
WHERE TABLE_SCHEMA = 'family_guardian' 
  AND TABLE_NAME = 'allowed_websites' 
  AND COLUMN_NAME = 'temp_expires_at';

-- Nếu trả về 0 rows → chạy tiếp:
ALTER TABLE allowed_websites 
ADD COLUMN temp_expires_at DATETIME NULL DEFAULT NULL 
AFTER updated_at;
```

### SQL 2 — Thêm cột `internet_paused` vào `users`

```sql
-- Kiểm tra trước
SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS 
WHERE TABLE_SCHEMA = 'family_guardian' 
  AND TABLE_NAME = 'users' 
  AND COLUMN_NAME = 'internet_paused';

-- Nếu trả về 0 rows → chạy tiếp:
ALTER TABLE users 
ADD COLUMN internet_paused TINYINT(1) NOT NULL DEFAULT 0 
AFTER filter_enabled;
```

### SQL 3 — Cập nhật lại `sp_GetChildAllowedWebsites` (bổ sung `temp_expires_at`)

> Guide 8 đã cập nhật SP nhưng chưa có `temp_expires_at`. Chạy lại để bổ sung.

```sql
DROP PROCEDURE IF EXISTS sp_GetChildAllowedWebsites;

DELIMITER //
CREATE PROCEDURE sp_GetChildAllowedWebsites(IN p_child_id INT)
BEGIN
    SELECT 
        -- Tất cả cột AllowedWebsite entity (EF Core yêu cầu đủ)
        aw.id,
        aw.child_id,
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
        aw.added_by,
        aw.created_at,
        aw.updated_at,
        aw.temp_expires_at,          -- ← bổ sung từ SQL 1
        -- Dữ liệu usage hôm nay
        COALESCE(dus.total_seconds, 0)   AS today_seconds,
        COALESCE(dus.bonus_seconds, 0)   AS bonus_seconds,
        GREATEST(0, COALESCE(dus.total_seconds, 0) - COALESCE(dus.bonus_seconds, 0))
                                         AS effective_seconds,
        COALESCE(dus.request_count, 0)   AS today_requests,
        -- limit_exceeded tính theo effective_seconds
        CASE 
            WHEN aw.time_limit_minutes IS NOT NULL 
                 AND GREATEST(0, COALESCE(dus.total_seconds, 0) - COALESCE(dus.bonus_seconds, 0))
                     >= (aw.time_limit_minutes * 60)
            THEN TRUE 
            ELSE FALSE 
        END AS limit_exceeded
    FROM allowed_websites aw
    LEFT JOIN daily_usage_stats dus 
        ON  dus.allowed_website_id = aw.id 
        AND dus.child_id           = p_child_id
        AND dus.usage_date         = CURDATE()
    WHERE aw.child_id  = p_child_id
      AND aw.is_active = TRUE
    ORDER BY aw.domain;
END //
DELIMITER ;
```

> ✅ Sau khi chạy đủ 3 SQL → note "SQL Guide 9 đã xong: temp_expires_at, internet_paused, SP updated" → API sẽ hết lỗi.

---

## TÍNH NĂNG 1 — Hiển thị thời gian gia hạn rõ ràng trên WebsiteCard

### Mục tiêu UI

**Mode "Giới hạn phút" khi có bonus:**
```
SỬ DỤNG   2 phút / 10 phút  [progress bar 20%]
⏰ Gia hạn: +30 phút  →  Còn lại: 38 phút
```

**Mode "Khung giờ" khi có bonus:**
```
KHUNG GIỜ  07:00 → 08:00
⏰ Gia hạn: +30 phút  →  Kết thúc mới: 08:30
```

---

### BACKEND — Kiểm tra DTO trả về

#### Bước 1.1 — Mở `AllowedWebsiteService.cs`, xem method `GetChildAllowedWebsitesAsync`

Xác định DTO được project ra. Tìm class response (có thể là `WebsiteDto`, `ChildWebsiteResponse`, ...).

Đảm bảo DTO có các field:
```csharp
public int TodaySeconds { get; set; }
public int BonusSeconds { get; set; }
public int EffectiveSeconds { get; set; }
public int? TimeLimitMinutes { get; set; }
public TimeOnly? AllowedStartTime { get; set; }
public TimeOnly? AllowedEndTime { get; set; }
public bool LimitExceeded { get; set; }
public int TodayRequests { get; set; }
```

Nếu `BonusSeconds` và `EffectiveSeconds` chưa có → thêm vào (từ Guide 7 + 8).

---

### FRONTEND — Cập nhật `WebsiteCard.tsx`

#### Bước 1.2 — Đọc toàn bộ `WebsiteCard.tsx` để xác định cấu trúc props

Xác nhận props interface có `bonusSeconds?: number` và `effectiveSeconds?: number`.

#### Bước 1.3 — Thêm phần hiển thị bonus vào card

Tìm chỗ render phần "SỬ DỤNG / KHUNG GIỜ" trong card. Thêm block hiển thị bonus bên dưới progress bar (KHÔNG thay đổi logic progress bar hiện có).

**Cho mode "Giới hạn phút" (`timeLimitMinutes != null`):**

```tsx
{/* Bonus display — chỉ hiện khi có bonus */}
{(bonusSeconds ?? 0) > 0 && timeLimitMinutes != null && (
  <div className="mt-2 flex items-center gap-2 rounded-lg border border-green-500/20 bg-green-500/8 px-3 py-2">
    <span className="text-xs text-green-600 dark:text-green-400">⏰ Gia hạn</span>
    <span className="text-xs font-semibold text-green-600 dark:text-green-400">
      +{Math.floor((bonusSeconds ?? 0) / 60)} phút
    </span>
    <span className="ml-auto text-xs text-tx-secondary">
      Còn lại:{' '}
      <span className="font-semibold text-tx-primary">
        {Math.max(0, Math.floor(
          (timeLimitMinutes * 60 - effectiveSeconds + (bonusSeconds ?? 0)) / 60
        ))} phút
      </span>
    </span>
  </div>
)}
```

> ⚠️ `effectiveSeconds = max(0, todaySeconds - bonusSeconds)` — đã tính từ backend
> Còn lại thực tế = `(timeLimitMinutes * 60) - effectiveSeconds + bonusSeconds`
> Công thức đơn giản hơn: `còn lại = (timeLimitMinutes * 60 + bonusSeconds) - todaySeconds`

```tsx
// Công thức đúng — còn lại tính từ total time đã cấp:
const totalGrantedSeconds = (timeLimitMinutes ?? 0) * 60 + (bonusSeconds ?? 0);
const remainingSeconds = Math.max(0, totalGrantedSeconds - todaySeconds);
const remainingMinutes = Math.floor(remainingSeconds / 60);
```

**Cho mode "Khung giờ" (`allowedStartTime != null`):**

```tsx
{(bonusSeconds ?? 0) > 0 && allowedStartTime != null && allowedEndTime != null && (
  <div className="mt-2 flex items-center gap-2 rounded-lg border border-green-500/20 bg-green-500/8 px-3 py-2">
    <span className="text-xs text-green-600 dark:text-green-400">⏰ Gia hạn</span>
    <span className="text-xs font-semibold text-green-600 dark:text-green-400">
      +{Math.floor((bonusSeconds ?? 0) / 60)} phút
    </span>
    <span className="ml-auto text-xs text-tx-secondary">
      Kết thúc mới:{' '}
      <span className="font-semibold text-tx-primary">
        {formatExtendedEndTime(allowedEndTime, bonusSeconds ?? 0)}
      </span>
    </span>
  </div>
)}
```

Thêm helper function vào `lib/formatters.ts` (KHÔNG thay đổi hàm đang có):

```typescript
// Tính giờ kết thúc mới khi có bonus phút cho khung giờ
export function formatExtendedEndTime(endTimeStr: string, bonusSeconds: number): string {
  // endTimeStr format: "HH:MM:SS" hoặc "HH:MM"
  const [h, m] = endTimeStr.split(':').map(Number);
  const totalMinutes = h * 60 + m + Math.floor(bonusSeconds / 60);
  const newH = Math.floor(totalMinutes / 60) % 24;
  const newM = totalMinutes % 60;
  return `${String(newH).padStart(2, '0')}:${String(newM).padStart(2, '0')}`;
}
```

#### Bước 1.4 — Cập nhật progress bar text hiển thị tổng

Tìm chỗ hiện "X phút đã dùng" hoặc phần trăm. Thêm tổng thời gian được cấp:

```tsx
{timeLimitMinutes != null && (
  <div className="flex justify-between text-xs text-tx-secondary">
    <span>
      {Math.floor(effectiveSeconds / 60)} / {timeLimitMinutes} phút
      {(bonusSeconds ?? 0) > 0 && (
        <span className="ml-1 text-green-600 dark:text-green-400">
          (+{Math.floor((bonusSeconds ?? 0) / 60)} gia hạn)
        </span>
      )}
    </span>
    <span>{Math.round(percent)}%</span>
  </div>
)}
```

---

## TÍNH NĂNG 2 — `blocked.html` phát hiện đổi mode và reload

### Mục tiêu
- Đang block do "Giới hạn phút" → Guardian đổi sang "Khung giờ" → trang reload và hiện đúng mode
- Đang block do ngoài khung giờ → hiện đúng lý do "Ngoài khung giờ cho phép"
- **KHÔNG thay đổi logic block trong `background.js`**

---

### BACKEND — Thêm `blockMode` vào response `/check`

#### Bước 2.1 — Mở `ExtensionController.cs`, tìm action GET `/check`

Xem response object hiện tại trả ra gì (có `allowed`, `reason`, ...).

#### Bước 2.2 — Thêm field `blockMode` vào response

Mở `CheckAccessResult.cs` (hoặc class tương đương). Thêm:

```csharp
// Thêm property mới (KHÔNG sửa property hiện có):
public string? BlockMode { get; set; }  // "time_limit" | "time_window" | null
```

#### Bước 2.3 — Set `blockMode` trong `CheckAccessAsync` của `ExtensionService.cs`

Tìm chỗ build kết quả trả về. Thêm logic set `BlockMode`:

```csharp
// Sau khi xác định kết quả (allowed hay blocked):
if (!result.Allowed)
{
    // Xác định mode từ website config
    if (website?.TimeLimitMinutes != null)
        result.BlockMode = "time_limit";
    else if (website?.AllowedStartTime != null)
        result.BlockMode = "time_window";
    else
        result.BlockMode = null;
}
// Nếu allowed → BlockMode không cần set (null)
```

> ⚠️ Chỉ thêm `BlockMode`. KHÔNG thay đổi logic `Allowed`, `Reason`, hay bất kỳ field nào khác.

---

### EXTENSION — Cập nhật `blocked.js` trong `blocked.html`

> ⚠️ Chỉ THÊM logic phát hiện mode change và cập nhật UI. KHÔNG thay đổi polling interval, auth flow, hay redirect logic hiện có.

#### Bước 2.4 — Mở `blocked.html`, tìm inline script hoặc `blocked.js`

Đọc toàn bộ. Xác định:
- Hàm poll `/check` tên gì?
- Kết quả được xử lý như thế nào?
- UI hiển thị lý do block ở đâu (element ID nào)?

#### Bước 2.5 — Thêm biến theo dõi `currentBlockMode`

Thêm biến ở đầu script (SAU các biến hiện có, KHÔNG thay đổi biến cũ):

```javascript
// ── THÊM — theo dõi mode để phát hiện thay đổi ──
let currentBlockMode = null;  // 'time_limit' | 'time_window' | null
```

#### Bước 2.6 — Thêm `updateBlockUI` function

```javascript
// ── THÊM — cập nhật UI theo mode block ──
function updateBlockUI(blockMode) {
  const reasonEl = document.getElementById('block-reason');   // xem HTML để xác định đúng ID
  const titleEl  = document.getElementById('block-title');    // xem HTML để xác định đúng ID

  if (!reasonEl && !titleEl) return;  // HTML chưa có element → không làm gì

  if (blockMode === 'time_window') {
    if (titleEl)  titleEl.textContent  = 'Ngoài khung giờ cho phép';
    if (reasonEl) reasonEl.textContent = 'Website này chỉ được phép truy cập trong khung giờ nhất định. Vui lòng quay lại đúng giờ.';
  } else {
    // time_limit (default)
    if (titleEl)  titleEl.textContent  = 'Đã hết thời gian sử dụng';
    if (reasonEl) reasonEl.textContent = 'Bạn đã dùng hết thời gian được phép cho website này hôm nay.';
  }
}
```

> ⚠️ ID `block-reason` và `block-title` là ví dụ. Đọc HTML thực tế để xác định đúng ID rồi thay.

#### Bước 2.7 — Sửa hàm xử lý kết quả poll

Tìm đoạn code xử lý response từ `/check` trong hàm poll. Thêm logic **sau** phần kiểm tra `allowed` (KHÔNG thay đổi logic redirect):

```javascript
// Trong hàm poll, SAU đoạn: if (data.allowed) { redirect... }
// THÊM đoạn sau (chỉ chạy khi vẫn bị block):
if (!data.allowed) {
  const newMode = data.blockMode || 'time_limit';

  if (currentBlockMode === null) {
    // Lần đầu load: set mode và cập nhật UI
    currentBlockMode = newMode;
    updateBlockUI(newMode);
  } else if (currentBlockMode !== newMode) {
    // Mode đã thay đổi → reload trang để load lại UI
    // (VD: từ time_limit → time_window khi guardian đổi config)
    window.location.reload();
    return;
  }
}
```

#### Bước 2.8 — Cập nhật `blocked.html` — thêm element hiển thị mode (nếu chưa có)

Mở file HTML. Tìm phần hiển thị lý do bị chặn. Đảm bảo có element với ID để JS cập nhật:

```html
<!-- Nếu chưa có → thêm vào phần body, phù hợp với design hiện tại -->
<h2 id="block-title" class="...">Đã hết thời gian sử dụng</h2>
<p id="block-reason" class="...">Bạn đã dùng hết thời gian được phép cho website này hôm nay.</p>
```

> Dùng đúng class CSS phù hợp với thiết kế hiện tại của `blocked.html`. KHÔNG thay đổi layout.

---

## Kiểm tra Extension `background.js` — Verify không cần sửa

#### Bước 3.1 — Mở `background.js`, xác nhận block flow

```javascript
// Phải có dạng này (KHÔNG thay đổi):
if (data.limitExceeded) {
  activeTab = null;
  chrome.tabs.update(tabId, { url: blockedUrl });
  return;
}
```

`blockedUrl` hiện tại không cần truyền thêm `blockMode` vì `blocked.js` sẽ tự lấy từ poll `/check`. → Không cần sửa `background.js`.

---

## Kiểm tra Dark Mode — `WebsiteCard.tsx`

### Bonus badge phải tương thích dark mode:

```tsx
// ✅ ĐÚNG — CSS variables + dark variant:
className="border border-green-500/20 bg-green-500/8 text-green-600 dark:text-green-400"

// ❌ SAI — hardcode:
className="border border-green-200 bg-green-50 text-green-700"
```

### Kiểm tra các class mới thêm vào:

- [ ] Bonus badge container: `border-green-500/20 bg-green-500/8` — hiện đúng ở cả light/dark
- [ ] Text "+X phút gia hạn": `text-green-600 dark:text-green-400`
- [ ] Text "Còn lại / Kết thúc mới": `text-tx-secondary` + `text-tx-primary` (font-semibold)
- [ ] Không có `bg-white`, `bg-gray-*`, `text-gray-*` nào mới thêm

---

## Tóm tắt thứ tự làm việc

```
BƯỚC 1 — Chạy SQL 1: ALTER TABLE allowed_websites ADD temp_expires_at
BƯỚC 2 — Chạy SQL 2: ALTER TABLE users ADD internet_paused
BƯỚC 3 — Chạy SQL 3: DROP + CREATE sp_GetChildAllowedWebsites (bổ sung temp_expires_at)
BƯỚC 4 — Khởi động lại API → verify không còn lỗi temp_expires_at và internet_paused
BƯỚC 5 — Backend: thêm BlockMode vào CheckAccessResult
BƯỚC 6 — Backend: set BlockMode trong CheckAccessAsync
BƯỚC 7 — Frontend WebsiteCard: thêm bonus display block (giới hạn phút + khung giờ)
BƯỚC 8 — Frontend formatters.ts: thêm formatExtendedEndTime helper
BƯỚC 9 — Extension blocked.html: thêm element ID cho title và reason
BƯỚC 10 — Extension blocked.js: thêm currentBlockMode + updateBlockUI + mode change detection
BƯỚC 11 — Test: bonus hiện đúng trên card cả 2 mode
BƯỚC 12 — Test: đổi mode từ giới hạn phút → khung giờ → blocked.html reload và hiện đúng lý do
```

---

## Test nhanh

### Test API fix:
```
Khởi động API → KHÔNG còn lỗi "Unknown column 'temp_expires_at'" và "Unknown column 'internet_paused'"
```

### Test Bonus Display:
```sql
-- Verify data có bonus:
SELECT domain, total_seconds, bonus_seconds, time_limit_minutes 
FROM allowed_websites aw
JOIN daily_usage_stats dus ON dus.allowed_website_id = aw.id
WHERE aw.child_id = 2 AND dus.usage_date = CURDATE();

-- WebsiteCard phải hiện: "+30 phút gia hạn" và "Còn lại: X phút"
```

### Test Mode Change:
1. Website đang block do hết giờ (Giới hạn phút) → `blocked.html` hiện "Đã hết thời gian"
2. Guardian vào sửa website → đổi sang Khung giờ 07:00-23:00
3. Trong vòng polling interval → `blocked.html` tự reload
4. Sau reload: nếu đang trong khung giờ → redirect về website; nếu ngoài khung giờ → hiện "Ngoài khung giờ cho phép"
