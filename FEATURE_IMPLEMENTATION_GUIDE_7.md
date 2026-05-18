# Family Guardian — Fix 3 Bug (Phần 7)

> **Ngày tạo:** 2026-05-16  
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE_6.md (Phần 6)

---

## ⚠️ Quy tắc bất di bất dịch

| Quy tắc | Chi tiết |
|---------|---------|
| `sp_ExtensionCheckAccess` | Tuyệt đối KHÔNG sửa |
| `background.js` | KHÔNG thay đổi logic đang chạy, chỉ verify |
| `DateTime.Now` | KHÔNG đổi sang `UtcNow` |
| Block logic | KHÔNG await `showBannerAsync` trước khi block |
| Dark mode | Dùng CSS variables, KHÔNG hardcode màu |

---

## Tổng quan 3 vấn đề cần fix

| # | Vấn đề | Root cause | Nơi fix |
|---|--------|-----------|---------|
| 1 | Bonus phút không hiện trong UI (progress bar vẫn 100%) | SP và DTO không trả `bonus_seconds`, WebsiteCard dùng `today_seconds` thô | SQL + Backend DTO + Frontend |
| 2 | "Khung giờ" block chậm hơn "Giới hạn phút" | `UpdateHeartbeatAsync` không set `LimitExceeded=true` khi ngoài khung giờ → extension dùng alarm (~60s) thay vì heartbeat (~30s) | Backend |
| 3 | Vercel lỗi `rolldown-binding.linux-x64-gnu.node` | `package-lock.json` lock Windows binary, Vercel (Linux) không có binary phù hợp | Frontend (local) |

---

## 📦 SQL —  Đã làm xong, tiến đến làm phần dưới.

### Kiểm tra SP hiện tại trả về gì:
```sql
SHOW CREATE PROCEDURE sp_GetChildAllowedWebsites;
```

### Cập nhật SP để trả thêm `bonus_seconds` và `effective_seconds`:

```sql
DROP PROCEDURE IF EXISTS sp_GetChildAllowedWebsites;

DELIMITER //
CREATE PROCEDURE sp_GetChildAllowedWebsites(IN p_child_id INT)
BEGIN
    SELECT 
        aw.id,
        aw.child_id,
        aw.domain,
        aw.display_name,
        aw.favicon_url,
        aw.is_active,
        aw.time_limit_minutes,
        aw.allowed_start_time,
        aw.allowed_end_time,
        COALESCE(dus.total_seconds, 0)  AS today_seconds,
        COALESCE(dus.bonus_seconds, 0)  AS bonus_seconds,
        GREATEST(0, COALESCE(dus.total_seconds, 0) - COALESCE(dus.bonus_seconds, 0)) AS effective_seconds,
        COALESCE(dus.request_count, 0)  AS today_requests,
        CASE 
            WHEN aw.time_limit_minutes IS NOT NULL 
                 AND GREATEST(0, COALESCE(dus.total_seconds, 0) - COALESCE(dus.bonus_seconds, 0))
                     >= (aw.time_limit_minutes * 60)
            THEN TRUE 
            ELSE FALSE 
        END AS limit_exceeded
    FROM allowed_websites aw
    LEFT JOIN daily_usage_stats dus 
        ON dus.allowed_website_id = aw.id 
        AND dus.child_id = p_child_id
        AND dus.usage_date = CURDATE()
    WHERE aw.child_id = p_child_id
      AND aw.is_active = TRUE
    ORDER BY aw.domain;
END //
DELIMITER ;
```

> ✅ Sau khi chạy xong → note "SQL sp_GetChildAllowedWebsites đã cập nhật" và làm tiếp Backend bên dưới.
> ✅ Đã làm xong,  làm tiếp Backend bên dưới.
---

## BUG 1 — Bonus phút không hiện trong UI

### BACKEND — Cập nhật DTO và mapping

#### Bước 1.1 — Tìm DTO của `GET /api/children/{childId}/websites`

Mở `ChildrenController.cs`. Tìm action GET `/{childId}/websites`. Xem nó gọi service/query gì và trả DTO nào.

Thông thường sẽ có 1 trong 2 dạng:
```csharp
// Dạng A: Gọi trực tiếp SP
var result = await _context.ChildWebsiteDtos
    .FromSqlInterpolated($"CALL sp_GetChildAllowedWebsites({childId})")
    .ToListAsync();

// Dạng B: Gọi qua service
var result = await _childService.GetWebsitesAsync(childId);
```

#### Bước 1.2 — Tìm class DTO/model được dùng cho SP result

Tìm class tương ứng (có thể tên là `ChildWebsiteDto`, `WebsiteViewModel`, `AllowedWebsiteDto`, ...).

Mở class đó. Thêm 2 property nếu chưa có:

```csharp
// Thêm vào class DTO (đặt cạnh TodaySeconds):
public int BonusSeconds { get; set; } = 0;
public int EffectiveSeconds { get; set; } = 0;
```

> ⚠️ Nếu class dùng `[Column("...")]` attribute thì thêm đúng tên:
> ```csharp
> [Column("bonus_seconds")]
> public int BonusSeconds { get; set; } = 0;
>
> [Column("effective_seconds")]
> public int EffectiveSeconds { get; set; } = 0;
> ```

#### Bước 1.3 — Verify không có projection nào lọc mất field mới

Nếu controller/service có đoạn `.Select(w => new { ... })` để project kết quả → đảm bảo thêm `BonusSeconds` và `EffectiveSeconds` vào đó.

---

### FRONTEND — Cập nhật `WebsiteCard.tsx`

#### Bước 1.4 — Mở `WebsiteCard.tsx`, đọc toàn bộ

Xác định:
- Props interface có những field nào? (có `todaySeconds`, `timeLimitMinutes`, ...)
- Progress bar tính thế nào? (`todaySeconds / (timeLimitMinutes * 60)` ?)
- Text hiển thị "X phút" tính từ đâu?

#### Bước 1.5 — Thêm `bonusSeconds` và `effectiveSeconds` vào props interface

```typescript
// Tìm interface props (WebsiteCardProps hoặc tương đương):
interface WebsiteCardProps {
  // ... các field hiện có ...
  todaySeconds: number;
  timeLimitMinutes?: number | null;
  // Thêm 2 field mới:
  bonusSeconds?: number;       // ← thêm
  effectiveSeconds?: number;   // ← thêm
  // ...
}
```

#### Bước 1.6 — Sửa tính toán progress bar và hiển thị thời gian

```typescript
// Trong component body, thêm biến:
const effectiveUsed = effectiveSeconds ?? Math.max(0, todaySeconds - (bonusSeconds ?? 0));
const limitSecs = (timeLimitMinutes ?? 0) * 60;

// ❌ SAI — dùng todaySeconds thô:
// const percent = Math.min(100, (todaySeconds / limitSecs) * 100);
// const usedMinutes = Math.floor(todaySeconds / 60);

// ✅ ĐÚNG — dùng effectiveUsed:
const percent = limitSecs > 0 ? Math.min(100, (effectiveUsed / limitSecs) * 100) : 0;
const usedMinutes = Math.floor(effectiveUsed / 60);
const remainingMinutes = Math.max(0, Math.floor((limitSecs - effectiveUsed) / 60));
```

#### Bước 1.7 — Sửa màu progress bar

Trong hệ thống hiện tại:
- Dưới 80%: màu `brand-DEFAULT` (tím)
- 80–99%: màu vàng cam
- 100%: màu đỏ

Đảm bảo logic màu dùng `percent` mới (đã tính theo `effectiveUsed`):

```typescript
const barColor =
  percent >= 100 ? 'bg-red-500' :
  percent >= 80  ? 'bg-amber-500' :
  'bg-brand-DEFAULT';
```

#### Bước 1.8 — Kiểm tra text hiển thị "X% ĐÃ DÙNG" hoặc "X PHÚT"

Đảm bảo text dùng `usedMinutes` và `percent` từ `effectiveUsed`, không phải `todaySeconds` raw.

Nếu có bonus đang active → thêm badge nhỏ hiển thị bonus (tùy chọn, không bắt buộc):
```tsx
{(bonusSeconds ?? 0) > 0 && (
  <span className="text-xs text-green-500 dark:text-green-400">
    +{Math.floor((bonusSeconds ?? 0) / 60)}p gia hạn
  </span>
)}
```

#### Bước 1.9 — Cập nhật nơi gọi `WebsiteCard` (trong `ChildDetailPage.tsx`)

Tìm chỗ render `<WebsiteCard ... />`. Truyền thêm props:

```tsx
<WebsiteCard
  // ... props hiện có ...
  bonusSeconds={website.bonusSeconds ?? 0}       // ← thêm
  effectiveSeconds={website.effectiveSeconds ?? 0}  // ← thêm
/>
```

---

## BUG 2 — "Khung giờ cho phép" block chậm hơn "Giới hạn phút"

### Phân tích root cause

Theo doc, `UpdateHeartbeatAsync` step 5:
> "Kiểm tra `limitExceeded = totalSeconds >= limitSeconds`"

Step này CHỈ áp dụng cho **Giới hạn phút** (`time_limit_minutes != null`). Với **Khung giờ** (`allowed_start_time != null`), khi ngoài khung giờ, `LimitExceeded` có thể KHÔNG được set thành `true`.

Kết quả: extension nhận `limitExceeded: false` → không block ngay → phụ thuộc vào alarm (Chrome alarm min ~60s) → chậm hơn.

**Fix:** Trong `UpdateHeartbeatAsync`, khi ngoài khung giờ → set `result.LimitExceeded = true` → extension block trong vòng 30s (heartbeat tiếp theo).

### BACKEND — Sửa `UpdateHeartbeatAsync` trong `ExtensionService.cs`

#### Bước 2.1 — Mở `ExtensionService.cs`, tìm `UpdateHeartbeatAsync`

Đọc toàn bộ method. Tìm phần xử lý khung giờ (`allowedStartTime != null`).

#### Bước 2.2 — Xác định vị trí set `LimitExceeded`

Hiện tại method có đoạn kiểm tra limit kiểu:
```csharp
// Cho Giới hạn phút:
result.LimitExceeded = totalSeconds >= limitSeconds;

// Cho Khung giờ: 
// Có thể KHÔNG set LimitExceeded hoặc set khác
```

#### Bước 2.3 — Thêm `LimitExceeded = true` khi ngoài khung giờ

Tìm đoạn xử lý khung giờ. Thêm logic:

```csharp
// Trong phần xử lý time_window (allowedStartTime != null):
var now = TimeOnly.FromDateTime(DateTime.Now);

// Xác định isInsideWindow
bool isInsideWindow;
if (website.AllowedStartTime <= website.AllowedEndTime)
{
    // Khung giờ thông thường: vd 07:00 → 21:00
    isInsideWindow = now >= website.AllowedStartTime && now <= website.AllowedEndTime;
}
else
{
    // Khung giờ qua đêm: vd 22:00 → 06:00 (xuyên đêm)
    isInsideWindow = now >= website.AllowedStartTime || now <= website.AllowedEndTime;
}

if (!isInsideWindow)
{
    // ← THÊM DÒNG NÀY: ngoài khung giờ → block ngay qua heartbeat
    result.LimitExceeded = true;
}

// Phần còn lại (tính minutesUntilWindowEnd, cảnh báo, ...) GIỮ NGUYÊN
```

> ⚠️ KHÔNG thay đổi phần tính `minutesUntilWindowEnd`, `TimeWindowDisplay`, hay cảnh báo.
> Chỉ THÊM `result.LimitExceeded = true` khi ngoài khung giờ.

#### Bước 2.4 — Verify thứ tự trong method

Đảm bảo thứ tự logic sau (KHÔNG thay đổi logic hiện có, chỉ xác nhận):

```
1. Upsert daily_usage_stats (+30s)          ← GIỮ NGUYÊN
2. Lấy AllowedWebsite                        ← GIỮ NGUYÊN
3. Kiểm tra cảnh báo giới hạn phút           ← GIỮ NGUYÊN
4. Kiểm tra cảnh báo khung giờ + minutesUntilEnd ← GIỮ NGUYÊN
   └── THÊM: nếu !isInsideWindow → result.LimitExceeded = true
5. Kiểm tra limitExceeded cho giới hạn phút  ← GIỮ NGUYÊN
6. Return result                             ← GIỮ NGUYÊN
```

### EXTENSION — Kiểm tra `background.js` (chỉ verify, không sửa logic)

#### Bước 2.5 — Mở `background.js`, tìm handler heartbeat response

Verify đoạn xử lý kết quả heartbeat:

```javascript
// Phải có dạng này (ĐÚNG):
if (data.limitExceeded) {
  // Set activeTab = null rồi block NGAY
  activeTab = null;
  chrome.tabs.update(tabId, { url: blockedUrl });
  return;  // ← PHẢI return ngay, không làm gì thêm
}
```

> Nếu đoạn này đúng → khi backend set `LimitExceeded = true` cho khung giờ → extension sẽ block trong vòng 30s (heartbeat tiếp theo).

#### Bước 2.6 — Kiểm tra `scheduleWarningAlarms`

Tìm hàm `scheduleWarningAlarms` (hoặc tương đương). Verify nó CHỈ tạo alarm cho **cảnh báo**, KHÔNG tạo alarm để block:

```javascript
// ✅ ĐÚNG — alarm chỉ cho warning:
function scheduleWarningAlarms(data) {
  if (data.schedule?.secondsUntilWarning1 > 0) {
    chrome.alarms.create('warning1_...', { delayInMinutes: ... });
  }
  // KHÔNG có alarm để block
}

// ❌ SAI — alarm dùng để block (gây chậm ~60s):
// chrome.alarms.create('block_at_window_end', { when: windowEndTime });
```

> Nếu có alarm để block → **xóa alarm đó** (đây là lý do "khung giờ" chậm hơn).
> Bước xóa alarm này KHÔNG vi phạm logic vì đây là lỗi, không phải tính năng.

---

## BUG 3 — Vercel: `rolldown-binding.linux-x64-gnu.node` not found

### Phân tích root cause

`package-lock.json` được generate trên **Windows** → npm lock binary Windows của cả `@tailwindcss/oxide` lẫn `rolldown`. Vercel (Linux) chạy `npm install` theo lock file → thiếu Linux binary → crash khi build.

Lỗi trước: `EBADPLATFORM @tailwindcss/oxide-win32-x64-msvc` (Guide 5 đã fix bằng `--omit=optional`)  
Lỗi hiện tại: `MODULE_NOT_FOUND rolldown-binding.linux-x64-gnu.node` (rolldown Linux binary bị missing/omit)

**Root cause chung:** lock file Windows. Fix bền vững nhất là không commit lock file.

### Fix — Local only (bạn tự commit sau)

#### Bước 3.1 — Xóa `package-lock.json`

```bash
# Trong thư mục frontend (nơi có package.json):
del package-lock.json   # Windows CMD
# hoặc
rm package-lock.json    # PowerShell / Git Bash
```

#### Bước 3.2 — Xóa `.npmrc` nếu đã thêm từ Guide 5

```bash
# Nếu có file .npmrc với omit=optional → xóa đi:
del .npmrc
```

> Lý do xóa: `omit=optional` đã bỏ cả rolldown Linux binary (vì nó là optional dep) → gây lỗi hiện tại.

#### Bước 3.3 — Thêm `package-lock.json` vào `.gitignore`

Mở `.gitignore` (ở root frontend project). Thêm dòng:

```
# Lock file gây conflict giữa Windows và Linux (Vercel)
package-lock.json
```

#### Bước 3.4 — Chạy lại `npm install` để có node_modules đầy đủ trên local

```bash
npm install
```

Lần này npm sẽ tạo `package-lock.json` mới (Windows binary) nhưng file này sẽ không được commit (đã add vào `.gitignore`).

#### Bước 3.5 — Kiểm tra local vẫn chạy bình thường

```bash
npm run dev
```

Phải chạy bình thường. Nếu không → xem lỗi gì và báo lại.

#### Bước 3.6 — Khi push lên Git (bạn tự làm)

Khi push, Vercel sẽ:
1. Chạy `npm install` **không có** lock file
2. npm tự resolve và install đúng binary cho Linux
3. Build thành công

> ⚠️ Lần deploy đầu sẽ chậm hơn (npm cần download lại tất cả). Các lần sau Vercel cache lại.

#### Bước 3.7 — Nếu vẫn lỗi sau khi xóa lock file

Trong Vercel Dashboard → Settings → Build & Development Settings → Install Command:
```
npm install --no-optional
```

Nhưng nếu dùng cách này, cần kiểm tra build có pass không (rolldown có thể bị coi là optional).

**Alternative tốt hơn:** Vercel Install Command:
```
npm install
```
(giữ default, không thêm flag) → vì đã xóa lock file, npm sẽ tự resolve đúng.

---

## Kiểm tra giao diện Dark Mode (Không thay đổi logic)

### WebsiteCard.tsx sau khi sửa

Khi thêm badge "+X phút gia hạn", đảm bảo dark mode:

```tsx
// ✅ ĐÚNG — dùng CSS variables:
<span className="text-xs font-medium text-green-600 dark:text-green-400">
  +{Math.floor((bonusSeconds ?? 0) / 60)}p gia hạn
</span>

// ❌ SAI — hardcode màu:
<span className="text-xs text-green-600">...</span>
```

Progress bar khi bonus active (% giảm xuống dưới 100%):
```tsx
// Màu bar phải responsive với % mới (effectiveUsed):
const barClass =
  percent >= 100 ? 'bg-red-500' :
  percent >= 80  ? 'bg-amber-500' :
  'bg-brand-DEFAULT';  // ← dùng CSS variable, không hardcode purple-500
```

---

## Thứ tự làm việc

```
BƯỚC 1 — Chạy SQL cập nhật sp_GetChildAllowedWebsites → note đã xong
BƯỚC 2 — Backend: thêm BonusSeconds + EffectiveSeconds vào DTO
BƯỚC 3 — Backend: thêm LimitExceeded = true khi ngoài khung giờ (UpdateHeartbeatAsync)
BƯỚC 4 — Frontend: thêm bonusSeconds + effectiveSeconds vào WebsiteCard props
BƯỚC 5 — Frontend: sửa tính toán progress bar dùng effectiveUsed
BƯỚC 6 — Frontend: truyền props mới từ ChildDetailPage → WebsiteCard
BƯỚC 7 — Extension: verify background.js — scheduleWarningAlarms không có alarm block
BƯỚC 8 — Frontend: xóa package-lock.json + .npmrc, thêm vào .gitignore, npm install lại
BƯỚC 9 — Test: bonus hiện đúng trên WebsiteCard
BƯỚC 10 — Test: khung giờ block trong ≤30s (bằng giới hạn phút)
```

---

## Test nhanh sau khi fix

### Test Bug 1:
```sql
-- Verify data trước khi test:
SELECT domain, total_seconds, bonus_seconds, 
       GREATEST(0, total_seconds - bonus_seconds) AS effective_seconds
FROM daily_usage_stats 
WHERE child_id = 2 AND usage_date = CURDATE();
-- google.com: bonus=1800, effective=0 → WebsiteCard phải hiện 0% (hoặc "0 phút")
-- youtube.com: bonus=120, effective=max(0,240-120)=120 → hiện 2 phút đã dùng
```

### Test Bug 2:
1. Set website khung giờ sắp hết (ví dụ: end time = NOW + 1 phút)
2. Đợi hết khung giờ
3. Trong vòng 30s → extension phải block tab
4. (Trước khi fix: phải đợi tới ~60s)

### Test Bug 3:
Push code lên, Vercel build logs không còn lỗi rolldown.
