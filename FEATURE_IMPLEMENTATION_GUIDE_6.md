# Family Guardian — Hướng dẫn Fix extend_time (Deep Fix — Phần 6)

> **Ngày tạo:** 2026-05-16  
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE_5.md (Phần 5)

---

## ⚠️ Quy tắc bất di bất dịch

| Quy tắc | Chi tiết |
|---------|---------|
| `sp_ExtensionCheckAccess` | Tuyệt đối KHÔNG sửa stored procedure này |
| Extension background.js | KHÔNG thay đổi logic đang chạy — chỉ THÊM nếu cần |
| `DateTime.Now` | KHÔNG đổi sang `UtcNow` |
| Notification query | Luôn query theo `guardian_id` |

---

## 🔍 Phân tích ROOT CAUSE — Tại sao extend_time không có tác dụng

Đây là vấn đề mà **Guide 4 và Guide 5 đều bỏ sót** 1 điểm then chốt.

### Luồng khi con bị block:

```
Con hết giờ (10 phút)
  → background.js phát hiện limitExceeded: true từ heartbeat
  → chrome.tabs.update(tabId, { url: blocked.html?domain=youtube.com })
  → blocked.html hiện ra
  → blocked.js bắt đầu poll: GET /api/extension/check?domain=youtube.com
```

### Khi guardian gia hạn:

```
Guardian click "Gia hạn 30 phút"
  → PATCH /api/access-requests/{id}/respond
  → Backend RespondToRequestAsync: bonus_seconds += 1800
  → SaveChanges ✓
  → SignalR AccessApproved ✓
```

### Vấn đề xảy ra sau đó:

```
blocked.js poll → GET /api/extension/check?domain=youtube.com
  → Backend gọi sp_ExtensionCheckAccess(google_id, 'youtube.com')
  → SP tính: total_seconds=600, limit=600 → 600 >= 600 → BLOCKED
  → SP KHÔNG biết bonus_seconds=1800 (không được sửa SP → không bao giờ biết)
  → Trả về { allowed: false }
  → blocked.js vẫn ở trang blocked ← LỖI CHÍNH Ở ĐÂY
```

### Vấn đề thứ 2 — heartbeat sau khi tab được reload:

```
Nếu blocked.js redirect về youtube.com sau khi /check trả allowed
  → background.js gửi heartbeat mỗi 30s
  → UpdateHeartbeatAsync tính: limitExceeded = totalSeconds(600) >= limit(600) → TRUE
  → background.js lại block tab ← LỖI THỨ 2
```

### Tóm tắt: có 3 nơi cần fix

| # | Nơi cần fix | Tại sao |
|---|------------|---------|
| 1 | `CheckAccessAsync` (C#, KHÔNG phải SP) | SP không biết bonus → phải override trong C# sau khi SP trả blocked |
| 2 | `UpdateHeartbeatAsync` (C#) | Đang dùng `totalSeconds` không trừ bonus → vẫn báo limitExceeded |
| 3 | `RespondToRequestAsync` (C#) | Verify bonus được lưu đúng chỗ |

---

## 📦 SQL — ✅ ĐÃ HOÀN THÀNH — Không cần chạy thêm gì

> Đã kiểm tra ngày 2026-05-16. Kết quả:

| Kiểm tra | Kết quả |
|---------|---------|
| Cột `bonus_seconds` trong `daily_usage_stats` | ✅ Đã tồn tại |
| Bảng `access_requests` | ✅ Đã tồn tại (13 cột) |
| Dữ liệu bonus_seconds | ✅ Đang được lưu đúng |

### 🔑 Phát hiện quan trọng từ data thực tế

```
child_id=2, domain=google.com,   total_seconds=120,  bonus_seconds=1800
child_id=2, domain=youtube.com,  total_seconds=240,  bonus_seconds=120
```

**Kết luận:** `RespondToRequestAsync` ✅ hoạt động đúng — bonus_seconds đang được lưu vào DB.

**Vấn đề 100% nằm ở C# backend:**
- `google.com`: effective = max(0, 120 - 1800) = **0 giây** → đáng lẽ phải `allowed`
- Nhưng `sp_ExtensionCheckAccess` tính `120 >= limit → blocked` (không biết bonus)
- C# chưa có override sau khi SP trả `blocked`

**Chỉ cần sửa C# — không cần thêm SQL.**

---

## PHẦN 1 — Kiểm tra `DailyUsageStat.cs` (Entity) — Khả năng cao đã đúng

> Vì bonus_seconds đang được lưu vào DB đúng → EF entity đã map được. Nhưng vẫn cần verify để chắc chắn.

### Bước 1.1 — Mở file `DailyUsageStat.cs`

Đọc class. Xác nhận có property `BonusSeconds` và nó map đúng column:

```csharp
// Phải có một trong các dạng sau:
[Column("bonus_seconds")]
public int BonusSeconds { get; set; } = 0;

// Hoặc (nếu project dùng UseSnakeCaseNamingConvention):
public int BonusSeconds { get; set; } = 0;  // EF tự map sang bonus_seconds

// Hoặc nullable (cũng chấp nhận được):
public int? BonusSeconds { get; set; }
```

> Nếu property tồn tại và data trong DB đúng → bỏ qua, sang Phần 2.

---

## PHẦN 2 — `RespondToRequestAsync` — ✅ Đang hoạt động đúng, chỉ verify thứ tự SaveChanges

> Vì `bonus_seconds` trong DB đã có giá trị đúng → method này đang chạy tốt.
> Chỉ cần verify **1 điểm**: `SaveChangesAsync()` phải gọi TRƯỚC `SendAsync` SignalR.

Mở `AccessRequestService.cs`, tìm case `extend_time`, kiểm tra thứ tự:

```csharp
// ✅ ĐÚNG — SaveChanges TRƯỚC SignalR
await _context.SaveChangesAsync();
await _hub.Clients.Group($"child_{...}").SendAsync("AccessApproved", ...);

// ❌ SAI — SignalR trước thì extension poll /check lúc DB chưa có bonus
await _hub.Clients.Group($"child_{...}").SendAsync("AccessApproved", ...);
await _context.SaveChangesAsync();
```

> Nếu thứ tự đúng rồi → bỏ qua Phần 2, **Phần 3 mới là fix quan trọng nhất**.

---

## PHẦN 3 — Fix `CheckAccessAsync` — FIX QUAN TRỌNG NHẤT

> ⚠️ Đây là fix then chốt mà Guide 4 và 5 chưa làm rõ đủ.
> SP `sp_ExtensionCheckAccess` KHÔNG được sửa, nhưng C# code phải override kết quả sau khi SP trả về.

### Bước 3.1 — Mở `ExtensionService.cs`, tìm method `CheckAccessAsync`

Đọc toàn bộ method. Xác định:
- Method gọi SP bằng cách nào? (SqlQueryRaw, FromSqlRaw, stored procedure call, ...)
- Kết quả SP được map vào class nào? (SpCheckResult, SpAccessResult, anonymous type, ...)
- Class đó có những property nào? (AccessResult, Reason, AllowedWebsiteId, ...)

**Ví dụ về cách SP được gọi (đọc code thực tế để xác định):**
```csharp
// Có thể là:
var spResult = await _context.Database
    .SqlQueryRaw<SpCheckResult>("CALL sp_ExtensionCheckAccess({0}, {1})", googleId, domain)
    .FirstOrDefaultAsync();

// Hoặc:
var spResult = await _context.SpCheckResults
    .FromSqlInterpolated($"CALL sp_ExtensionCheckAccess({googleId}, {domain})")
    .FirstOrDefaultAsync();
```

### Bước 3.2 — Thêm bonus override logic

Sau khi gọi SP và lấy được `spResult`, tìm đoạn code trả về kết quả `blocked`. Thêm đoạn sau vào **SAU khi có spResult, TRƯỚC khi return**:

```csharp
// ─── BONUS OVERRIDE — Sau khi SP trả về, trước khi return ───
// Nếu SP báo blocked VÀ website có AllowedWebsiteId → kiểm tra bonus
if (spResult != null
    && spResult.AccessResult != "allowed"   // ← tên property tùy theo class thực tế
    && spResult.AllowedWebsiteId.HasValue)  // ← tên property tùy theo class thực tế
{
    var today = DateOnly.FromDateTime(DateTime.Now);

    var stat = await _context.DailyUsageStats
        .AsNoTracking()
        .FirstOrDefaultAsync(s =>
            s.AllowedWebsiteId == spResult.AllowedWebsiteId.Value
            && s.UsageDate == today);

    if (stat != null && stat.BonusSeconds > 0)
    {
        var website = await _context.AllowedWebsites
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == spResult.AllowedWebsiteId.Value);

        if (website?.TimeLimitMinutes != null)
        {
            var limitSeconds = website.TimeLimitMinutes.Value * 60;
            var effectiveUsed = Math.Max(0, stat.TotalSeconds - stat.BonusSeconds);

            if (effectiveUsed < limitSeconds)
            {
                // Bonus đủ bù → override thành allowed
                // Tạo lại return object với Allowed = true
                // ⚠️ Tên và cấu trúc return object tùy theo code thực tế
                return new CheckAccessResult
                {
                    Allowed = true,
                    Reason = null,
                    Domain = domain,
                    AllowedWebsiteId = spResult.AllowedWebsiteId
                };
                // Hoặc nếu method trả về anonymous object / tuple:
                // return (allowed: true, reason: (string?)null, allowedWebsiteId: spResult.AllowedWebsiteId);
            }
        }
    }
}
// ─── Kết thúc BONUS OVERRIDE ───
```

> **Lưu ý về tên property:** Tên `AccessResult`, `AllowedWebsiteId`, `CheckAccessResult` là ví dụ.
> Đọc code thực tế để xác định tên đúng rồi thay thế cho phù hợp.

### Bước 3.3 — Verify response format của `/check` endpoint

Mở `ExtensionController.cs`. Tìm action GET `/check`. Xem response trả về client:

```json
// Dựa theo full doc:
{ "allowed": false, "reason": "...", "domain": "youtube.com", "allowedWebsiteId": 11 }
```

Đảm bảo sau khi fix, khi bonus đủ → response là:
```json
{ "allowed": true, "reason": null, "domain": "youtube.com", "allowedWebsiteId": 11 }
```

---

## PHẦN 4 — Fix `UpdateHeartbeatAsync` — Dùng effective_seconds

> Sau khi blocked.js redirect về, background.js sẽ gửi heartbeat. Nếu heartbeat vẫn tính theo total_seconds → tab bị block lại ngay.

### Bước 4.1 — Mở `ExtensionService.cs`, tìm `UpdateHeartbeatAsync`

Đọc toàn bộ method. Dựa theo full doc, method có bước:
> "5. Kiểm tra `limitExceeded = totalSeconds >= limitSeconds`"

Tìm đoạn này. Có thể là:
```csharp
// Cách 1: Dùng SP result (sp_GetChildAllowedWebsites hoặc query riêng)
var limitExceeded = stat.TotalSeconds >= limitSeconds;
// Cách 2: Tính trực tiếp
var limitExceeded = totalUsedSeconds >= website.TimeLimitMinutes * 60;
```

### Bước 4.2 — Sửa tất cả chỗ tính thời gian

Thêm biến `effectiveUsed` và dùng xuyên suốt:

```csharp
// Lấy bonus từ stat
var bonusSeconds = stat?.BonusSeconds ?? 0;

// Tính effective (không âm)
var effectiveUsed = Math.Max(0, (stat?.TotalSeconds ?? 0) - bonusSeconds);

// ── Tất cả chỗ sau đây dùng effectiveUsed thay vì totalSeconds ──

// limitExceeded
var limitSeconds = (website.TimeLimitMinutes ?? 0) * 60;
var limitExceeded = effectiveUsed >= limitSeconds;   // ← ĐÚNG

// remainingSeconds
var remainingSeconds = Math.Max(0, limitSeconds - effectiveUsed);  // ← ĐÚNG

// SecondsUntilBlock
result.SecondsUntilBlock = limitExceeded ? 0 : remainingSeconds;

// Warning thresholds (KHÔNG thay đổi công thức % — chỉ đổi biến đầu vào)
var usedPercent = limitSeconds > 0 ? (effectiveUsed * 100.0 / limitSeconds) : 0;
// Công thức tính % giữ nguyên, chỉ thay totalSeconds → effectiveUsed

// SecondsUntilWarning1/2 (giữ nguyên công thức, thay biến)
// Ví dụ: seconds until warning1 = limitSeconds * threshold1 / 100 - effectiveUsed
var secondsUntilW1 = (int)(limitSeconds * config.Threshold1Percent / 100.0) - effectiveUsed;
```

> ⚠️ Chỉ đổi `totalSeconds` → `effectiveUsed` cho các tính toán limit.
> KHÔNG thay đổi cách cộng +30s vào DB (vẫn cộng vào `total_seconds`, không phải `effective`).
> KHÔNG thay đổi cách gửi cảnh báo SignalR.

---

## PHẦN 5 — Extension: Kiểm tra `blocked.js`

> ⚠️ CHỈ đọc và xác nhận. KHÔNG thay đổi logic block/allow.

### Bước 5.1 — Mở `blocked.html` hoặc `blocked.js`

Tìm hàm polling (từ Guide 4 đã thêm `startPassivePolling()`).

**Kiểm tra:**
```javascript
// 1. startPassivePolling() phải gọi ngay khi trang load (không chờ nút bấm)
startPassivePolling(); // ← phải có ở top level hoặc trong DOMContentLoaded

// 2. Poll endpoint phải là /api/extension/check
const response = await fetch(`${API_BASE}/api/extension/check?domain=${domain}`, {
  headers: { 'Authorization': `Bearer ${token}` }
});

// 3. Nếu allowed = true → redirect về domain
const data = await response.json();
if (data.allowed) {
  window.location.href = `https://${domain}`;
}

// 4. Polling interval — bao nhiêu giây?
// Nếu > 10s và không có SignalR → user phải đợi lâu
// Khuyến nghị: 5-8s cho trải nghiệm tốt hơn
```

### Bước 5.2 — Kiểm tra auth token

```javascript
// blocked.js lấy token như thế nào để gọi /check?
// Phải dùng Google Token (không phải JWT) vì /api/extension dùng Google Token auth
// Verify: có dùng chrome.storage.local.get('googleToken') hoặc tương đương không?
chrome.storage.local.get(['googleToken'], (result) => {
  const token = result.googleToken;
  // dùng token này cho Authorization header
});
```

### Bước 5.3 — Kiểm tra SignalR `AccessApproved` listener

Tìm xem `blocked.js` / `blocked.html` có lắng nghe SignalR event `AccessApproved` không:

```javascript
// Nếu CÓ → khi guardian approve → tức thì redirect (không đợi polling)
connection.on('AccessApproved', (data) => {
  if (data.domain === currentDomain) {
    window.location.href = `https://${currentDomain}`;
  }
});

// Nếu KHÔNG CÓ → chỉ dựa vào polling → giảm interval xuống 5s cho UX tốt hơn
// KHÔNG thêm SignalR vào blocked.js nếu extension chưa có nó (sẽ thay đổi logic)
// Chỉ điều chỉnh polling interval nếu cần
```

> **Kết luận:** Nếu không có SignalR trong blocked.js → polling interval 5-8s là hợp lý.
> Nếu interval hiện tại là 30s → đổi xuống 5-8s (chỉ đổi con số, không đổi logic).

---

## PHẦN 6 — Kiểm tra giao diện Dark Mode (Không thay đổi logic)

> ⚠️ Chỉ sửa class CSS. KHÔNG thay đổi state, handler, query nào.

### CSS Variables cần nhớ

| Sai (hardcode) | Đúng (CSS variable) |
|---------------|---------------------|
| `bg-white` | `bg-bg-surface` |
| `bg-gray-50` | `bg-bg-subtle` |
| `bg-gray-100` | `bg-bg-muted` |
| `text-gray-900` | `text-tx-primary` |
| `text-gray-500` | `text-tx-secondary` |
| `border-gray-200` | `border-border-base` |
| `bg-white` (dropdown) | `bg-bg-elevated` |

### Kiểm tra các component mới (từ Guide 4)

**`AccessRequestCard.tsx`** — mở file, tìm các class sau:
- [ ] Wrapper card: KHÔNG có `bg-white`, `bg-gray-*`, `text-gray-*`
- [ ] Nếu có `bg-white` → đổi thành `bg-bg-surface`
- [ ] Nếu có `text-gray-600` → đổi thành `text-tx-secondary`
- [ ] Các input trong `MinutesExtendForm` và `WindowExtendForm`: `bg-bg-subtle border-border-base text-tx-primary`
- [ ] Input time picker: `bg-bg-subtle border-border-base text-tx-primary focus:border-brand-DEFAULT/60`

**`FilterDropdown.tsx`** — mở file, kiểm tra:
- [ ] Dropdown container: `bg-bg-elevated border-border-base` — KHÔNG `bg-white`
- [ ] Option hover: `hover:bg-bg-subtle` — KHÔNG `hover:bg-gray-100`

**`NotificationsPage.tsx`** — mở file:
- [ ] Tab container: `bg-bg-subtle border-border-base`
- [ ] Sliding indicator: `bg-bg-surface border-border-base/80`
- [ ] Empty state text: `text-tx-secondary`

---

## PHẦN 7 — Test flow end-to-end

Sau khi thực hiện tất cả fix trên, test theo thứ tự:

### Test 1 — Verify bonus_seconds được lưu vào DB

```sql
-- Chạy sau khi guardian gia hạn:
SELECT id, child_id, allowed_website_id, domain, usage_date, 
       total_seconds, bonus_seconds
FROM daily_usage_stats
WHERE usage_date = CURDATE()
ORDER BY id DESC
LIMIT 5;
-- Kết quả: bonus_seconds phải > 0 sau khi gia hạn
```

### Test 2 — Verify /check trả allowed sau khi gia hạn

Dùng Postman hoặc browser:
```
GET /api/extension/check?domain=youtube.com
Header: Authorization: Bearer <google_token_của_con>

Kết quả mong đợi (sau khi guardian gia hạn 30p):
{ "allowed": true, ... }
```

### Test 3 — Full flow thực tế

1. Con vào website đã set 10 phút/ngày
2. Dùng đủ 10 phút → bị redirect về `blocked.html`
3. Con nhấn "Xin thêm thời gian"
4. Guardian thấy card yêu cầu → nhấn "Gia hạn 30 phút"
5. Kết quả mong đợi:
   - Toast success hiện ở guardian
   - Trong vòng tối đa polling_interval giây → `blocked.html` tự redirect về website
   - Con dùng được thêm 30 phút
   - Sau 30 phút dùng thêm → lại bị block

---

## Tóm tắt thứ tự làm việc

```
BƯỚC 1 — Chạy SQL kiểm tra bonus_seconds column tồn tại
BƯỚC 2 — Mở DailyUsageStat.cs → verify/thêm BonusSeconds property
BƯỚC 3 — Mở AccessRequestService.cs → verify RespondToRequestAsync
          (SaveChanges TRƯỚC SignalR, dùng += không phải =)
BƯỚC 4 — Mở ExtensionService.cs → thêm bonus override vào CheckAccessAsync
          (SAU khi SP trả blocked, TRƯỚC return)
BƯỚC 5 — Mở ExtensionService.cs → UpdateHeartbeatAsync dùng effectiveUsed
          (total - bonus cho limitExceeded và remainingSeconds)
BƯỚC 6 — Mở blocked.js → verify polling interval ≤ 10s
BƯỚC 7 — Chạy SQL Test 1 → verify bonus_seconds được lưu
BƯỚC 8 — Test /check bằng Postman → verify trả allowed sau gia hạn
BƯỚC 9 — Full flow test thực tế
```

---

## Lưu ý cuối

- **Không bỏ qua Bước 4** (bonus override trong CheckAccessAsync) — đây là fix then chốt nhất
- Nếu sau Bước 4 blocked.js vẫn nhận `allowed: false` → log kết quả SP ra console (tạm thời trong dev) để xác định tên property thực tế của SP result
- Nếu bonus_seconds trong DB vẫn là 0 sau gia hạn → vấn đề ở Bước 3 (SaveChanges không chạy hoặc sai AllowedWebsiteId)
- Dark mode: chỉ sửa class CSS, không thay đổi gì khác
