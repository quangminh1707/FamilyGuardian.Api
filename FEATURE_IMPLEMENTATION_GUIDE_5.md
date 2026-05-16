# Family Guardian — Hướng dẫn Fix Bug (Phần 5)

> **Ngày tạo:** 2026-05-16
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE_4.md (Phần 4)

---

## ⚠️ Quy tắc bất di bất dịch — NHẮC LẠI

| Quy tắc | Chi tiết |
|---------|---------|
| `sp_ExtensionCheckAccess` | Tuyệt đối KHÔNG sửa stored procedure này |
| Extension background.js | KHÔNG thay đổi logic đang chạy — chỉ THÊM nếu cần |
| Logic hiện tại | KHÔNG thay đổi bất kỳ flow nào đang hoạt động |
| `DateTime.Now` | KHÔNG đổi sang `UtcNow` bất kỳ chỗ nào |
| Notification query | Luôn query theo `guardian_id` |
| Dark mode | Dùng CSS variables: `bg-bg-surface`, `text-tx-primary`, `border-border-base`, `brand-DEFAULT` |

---

## 📦 SQL — Đã làm xong từ Guide 4

> ✅ SP `sp_GetChildAllowedWebsites` đã được tạo lại với `bonus_seconds` và `effective_seconds`.
> ✅ Không cần chạy thêm SQL nào cho 2 bug dưới đây.
> ✅ `bonus_seconds` column đã có trong bảng `daily_usage_stats`.

---

## Bug 1: `extend_time` — Gia hạn không có tác dụng (tiếp Guide 4)

### 🔍 Phân tích root cause thực sự

Guide 4 đã hướng dẫn sửa `sp_GetChildAllowedWebsites` và `UpdateHeartbeatAsync`.
**Nhưng vẫn không work vì thiếu 1 điểm quan trọng:**

Khi con bị block → `blocked.html` load → `blocked.js` poll **`/api/extension/check?domain=x`**
→ Backend gọi **`sp_ExtensionCheckAccess`** (KHÔNG được sửa)
→ SP này tính `limit_exceeded = total_seconds >= limit_seconds` (KHÔNG trừ `bonus_seconds`)
→ Kết quả luôn là `blocked` dù guardian đã gia hạn

**Giải pháp:** Sau khi SP trả về `blocked` với `reason = time_limit_exceeded`, C# code check thêm `bonus_seconds` và override kết quả nếu đủ điều kiện. **Không sửa SP, sửa C# layer.**

---

### BƯỚC 1 — Đọc file `ExtensionService.cs` → method `CheckAccessAsync`

Đọc toàn bộ method này. Tìm đoạn gọi `sp_ExtensionCheckAccess`. Xem cấu trúc kết quả trả về từ SP.

**Cần tìm hiểu:**
- SP trả về những cột gì? (access_result, reason, allowed_website_id, …)
- Sau khi gọi SP, code C# có làm gì thêm không?
- Kết quả cuối được map sang response object như thế nào?

---

### BƯỚC 2 — Thêm bonus_seconds override vào `CheckAccessAsync`

> ⚠️ Thêm logic này **SAU** khi gọi SP, TRƯỚC khi return. KHÔNG thay đổi code gọi SP.

Tìm đoạn sau khi lấy kết quả từ SP trong `CheckAccessAsync`. Thêm đoạn sau:

```csharp
// Sau khi gọi sp_ExtensionCheckAccess và lấy được spResult:
// Nếu SP báo blocked vì hết giờ → check thêm bonus_seconds
if (spResult.AccessResult == "blocked"
    && spResult.Reason != null
    && spResult.Reason.Contains("hết") // hoặc check theo reason thực tế SP trả về
    && spResult.AllowedWebsiteId.HasValue)
{
    // Lấy bonus_seconds hôm nay
    var today = DateOnly.FromDateTime(DateTime.Now);
    var stat = await _context.DailyUsageStats
        .AsNoTracking()
        .FirstOrDefaultAsync(s =>
            s.AllowedWebsiteId == spResult.AllowedWebsiteId.Value
            && s.UsageDate == today);

    if (stat != null && stat.BonusSeconds > 0)
    {
        // Lấy time_limit_minutes của website
        var website = await _context.AllowedWebsites
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == spResult.AllowedWebsiteId.Value);

        if (website?.TimeLimitMinutes != null)
        {
            var limitSeconds = website.TimeLimitMinutes.Value * 60;
            var effectiveUsed = Math.Max(0, stat.TotalSeconds - stat.BonusSeconds);
            if (effectiveUsed < limitSeconds)
            {
                // Đủ bonus → override kết quả thành allowed
                spResult.AccessResult = "allowed";
                spResult.Reason = null;
            }
        }
    }
}
```

> **Lưu ý:** Tên property của SP result có thể khác (đọc code thực tế để xác định).
> Ví dụ: `spResult.AccessResult` có thể là `spResult.access_result` hoặc `spResult.Result`.
> `spResult.Reason` có thể là string chứa "hết giờ" hoặc "time_limit_exceeded" tùy SP trả về gì.
> Đọc kết quả SP thực tế trước khi viết condition.

---

### BƯỚC 3 — Kiểm tra `RespondToRequestAsync` case `extend_time`

> ⚠️ Đọc toàn bộ method và case `extend_time` hiện tại. Verify từng bước sau:

**Kiểm tra entity `DailyUsageStats`:**

Mở file `DailyUsageStat.cs` (hoặc `DailyUsageStats.cs`). Đảm bảo có property:
```csharp
public int BonusSeconds { get; set; } = 0;
// Hoặc nullable:
// public int? BonusSeconds { get; set; }
```

Nếu KHÔNG có → thêm vào. Nếu nullable thì cần xử lý null khi dùng.

**Kiểm tra AppDbContext:** Đảm bảo EF Core map đúng column `bonus_seconds` → property `BonusSeconds`.
```csharp
// Trong AppDbContext hoặc entity config:
// [Column("bonus_seconds")]
// public int BonusSeconds { get; set; }
```

**Verify flow `extend_time` đúng thứ tự:**

```csharp
// 1. Tìm website — verify dùng ChildId + Domain + IsActive = true
var website = await _context.AllowedWebsites
    .FirstOrDefaultAsync(w => w.ChildId == request.ChildId
                           && w.Domain == request.Domain
                           && w.IsActive);
// Nếu null → return error

// 2. Tìm DailyUsageStat HÔM NAY — verify dùng website.Id (không phải domain/childId alone)
var today = DateOnly.FromDateTime(DateTime.Now);
var stat = await _context.DailyUsageStats
    .FirstOrDefaultAsync(s => s.ChildId == request.ChildId
                           && s.AllowedWebsiteId == website.Id  // ← PHẢI là website.Id
                           && s.UsageDate == today);

// 3. Cộng bonus
if (stat != null)
{
    var bonusMinutes = dto.DurationMinutes ?? 30;
    // Nếu BonusSeconds là nullable:
    stat.BonusSeconds = (stat.BonusSeconds ?? 0) + (bonusMinutes * 60);
    // Nếu không nullable:
    // stat.BonusSeconds += bonusMinutes * 60;

    stat.Warning1Sent = false;
    stat.Warning2Sent = false;
}
// Nếu stat == null: con chưa vào web đó hôm nay → bonus không cần thiết

// 4. SaveChanges — PHẢI gọi trước SignalR
await _context.SaveChangesAsync();

// 5. Gửi SignalR để blocked.js nhận tín hiệu auto-redirect
await _hub.Clients
    .Group($"child_{request.ChildId}")
    .SendAsync("AccessApproved", new { childId = request.ChildId, domain = request.Domain });
```

> **Lỗi hay gặp:** `SaveChangesAsync()` được gọi SAU SignalR → extension nhận tín hiệu rồi poll `/check` → check DB chưa cập nhật → vẫn blocked.
> **Fix:** `SaveChangesAsync()` TRƯỚC `SendAsync`.

---

### BƯỚC 4 — Kiểm tra `UpdateHeartbeatAsync` — dùng effective_seconds

> ⚠️ Đọc method này. Tìm chỗ tính `remainingSeconds`, `limitExceeded`, `SecondsUntilBlock`, `SecondsUntilWarning1/2`.

Nếu method đang dùng `today_seconds` / `TotalSeconds` từ SP hoặc từ DB → đổi sang `effective`:

```csharp
// Nếu đang dùng kết quả từ sp_GetChildAllowedWebsites:
// var usedSeconds = spWebsite.TodaySeconds;  // ← SAI (không trừ bonus)
var usedSeconds = spWebsite.EffectiveSeconds; // ← ĐÚNG (đã trừ bonus trong SP)

// Nếu tự query DailyUsageStats:
var stat = await _context.DailyUsageStats...;
// var usedSeconds = stat.TotalSeconds;          // ← SAI
var usedSeconds = Math.Max(0, stat.TotalSeconds - (stat.BonusSeconds ?? 0)); // ← ĐÚNG

// Mọi tính toán sau đều dùng usedSeconds này:
var limitSeconds = website.TimeLimitMinutes * 60;
var remainingSeconds = limitSeconds - usedSeconds;
var limitExceeded = usedSeconds >= limitSeconds;
// ... SecondsUntilWarning1/2, SecondsUntilBlock đều dùng usedSeconds — KHÔNG thay đổi công thức
```

---

### BƯỚC 5 — Kiểm tra Extension `blocked.js`

> ⚠️ CHỈ ĐỌC, không thay đổi logic polling.

Mở `blocked.js` (hoặc `blocked.html` inline script). Xem:
1. Polling có gọi `/api/extension/check?domain=...` không? → Đúng rồi, không sửa.
2. Có lắng nghe SignalR event `AccessApproved` không? Nếu CÓ → sẽ tự redirect khi guardian approve.
3. Nếu KHÔNG có SignalR → chỉ dựa vào polling → polling interval là bao nhiêu?

**Nếu blocked.js KHÔNG có SignalR listener và polling interval > 30s:**
- Thêm polling chủ động sau khi guardian approve (qua SignalR `AccessApproved` → trigger poll ngay)
- Hoặc giảm polling interval xuống 5-10s

**Nếu blocked.js ĐÃ CÓ SignalR `AccessApproved` listener:** Không cần sửa gì.

---

### Checklist Bug 1

- [ ] Đọc `CheckAccessAsync` — xác định SP trả về cột gì, tên property C# là gì
- [ ] Thêm override logic: nếu blocked vì time_limit + có bonus đủ → đổi thành allowed
- [ ] Đọc `DailyUsageStat.cs` — xác nhận có property `BonusSeconds`
- [ ] Đọc `RespondToRequestAsync` case `extend_time` — verify `SaveChangesAsync` gọi TRƯỚC SignalR
- [ ] Verify `UpdateHeartbeatAsync` dùng `effectiveSeconds` (total - bonus) cho mọi tính toán
- [ ] Test flow: hết 10p → bị block → guardian gia hạn 5p → `blocked.js` poll → `/check` trả `allowed` → tự redirect

---

## Bug 2: Vercel Deploy Fail — EBADPLATFORM `@tailwindcss/oxide-win32-x64-msvc`

### 🔍 Nguyên nhân

`package-lock.json` được generate trên **Windows** → npm lock `@tailwindcss/oxide-win32-x64-msvc` vào dependencies.
Vercel chạy trên **Linux** → không tương thích → `npm install` fail với `EBADPLATFORM`.

### Các bước fix — Làm theo thứ tự

---

#### BƯỚC 1 — Thêm `.npmrc` vào thư mục frontend (root của React project)

Tạo file `.npmrc` (nếu chưa có) ngay cạnh `package.json` của frontend:

```
# .npmrc
# Bỏ qua platform-specific optional packages khi deploy
omit=optional
```

> ⚠️ **Lưu ý:** `omit=optional` sẽ skip optional dependencies khi install. Tailwind CSS v4 sẽ dùng fallback. Nếu local Windows bị ảnh hưởng (Tailwind không build được), xem thêm Bước 3B.

---

#### BƯỚC 2 — Xóa `package-lock.json` và regenerate

```bash
# Trong thư mục frontend (nơi có package.json)
rm package-lock.json    # Windows: del package-lock.json

npm install             # Tạo lại lock file mới
```

> Sau bước này, `package-lock.json` mới sẽ không hard-lock `win32` binary vào required dependencies.

---

#### BƯỚC 3 — Nếu local Windows bị ảnh hưởng sau khi thêm `.npmrc`

Nếu `omit=optional` làm Tailwind CSS không build được trên Windows local:

Xóa `.npmrc`. Thay vào đó, trong Vercel Dashboard → Settings → Build & Development Settings → Install Command: đổi thành:
```
npm install --omit=optional
```

Vercel đọc Install Command này thay vì `.npmrc` → local Windows không bị ảnh hưởng.

---

#### BƯỚC 4 — Verify deploy thành công

Sau khi push, vào Vercel Dashboard → xem build logs. Đảm bảo:
- `npm install` chạy xong không lỗi
- `npm run build` / `vite build` chạy thành công
- Deploy URL hoạt động

---

#### Phương án dự phòng nếu tất cả trên vẫn fail

Trong `package.json` của frontend, thêm section `overrides`:

```json
{
  "overrides": {
    "@tailwindcss/oxide-win32-x64-msvc": {
      "optional": true
    }
  }
}
```

Hoặc nếu package.json có `tailwindcss` là direct dependency, thêm:
```json
{
  "optionalDependencies": {
    "@tailwindcss/oxide-win32-x64-msvc": "4.3.0"
  }
}
```
Sau đó xóa nó khỏi `dependencies` nếu có.

---

### Checklist Bug 2

- [ ] Tạo file `.npmrc` với `omit=optional` trong thư mục frontend
- [ ] Xóa `package-lock.json` cũ
- [ ] Chạy `npm install` lại để tạo lock file mới
- [ ] Kiểm tra local Windows: `npm run dev` vẫn chạy bình thường
- [ ] Nếu local bị ảnh hưởng: xóa `.npmrc`, đổi Install Command trong Vercel Dashboard thay thế
- [ ] Vercel build logs không còn `EBADPLATFORM` error
- [ ] Site deploy thành công và chạy đúng

---

## Kiểm tra Giao diện Dark Mode — Toàn bộ hệ thống

> ⚠️ Phần này là rà soát tổng thể. KHÔNG thay đổi bất kỳ logic nào. Chỉ sửa class CSS nếu sai màu.

### Nguyên tắc Dark Mode

- `darkMode: 'class'` trong Tailwind config → toggle class `dark` trên `<html>`
- CSS variables trong `light.css` và `dark.css` (thư mục `src/styles/themes/`)
- Sidebar LUÔN tối ở cả 2 mode (không đổi theo dark/light toggle)
- KHÔNG dùng hardcode màu như `bg-white`, `text-gray-900`, `bg-gray-100` — dùng CSS variables

### CSS Variables chuẩn của hệ thống

| Variable | Dùng cho |
|----------|---------|
| `bg-bg-surface` | Background card, panel |
| `bg-bg-subtle` | Background input, chip nhạt |
| `bg-bg-elevated` | Dropdown, modal overlay |
| `bg-bg-muted` | Badge, placeholder |
| `text-tx-primary` | Text chính |
| `text-tx-secondary` | Text phụ, label |
| `border-border-base` | Border thông thường |
| `brand-DEFAULT` | Màu chủ đạo (violet) |

### Rà soát từng component — checklist

Đọc từng file và kiểm tra class theo bảng sau:

#### `AccessRequestCard.tsx` (mới tạo ở Guide 4)
- [ ] Card wrapper: `bg-bg-surface border border-border-base` — KHÔNG dùng `bg-white`
- [ ] Badge trạng thái pending: `bg-amber-500/10 text-amber-600 dark:text-amber-400` — có dark variant
- [ ] Badge trạng thái handled: `bg-bg-muted text-tx-secondary`
- [ ] Text thông tin: `text-tx-primary` / `text-tx-secondary`
- [ ] Input số phút (`MinutesExtendForm`): `bg-bg-subtle border-border-base text-tx-primary`
- [ ] Input time (`WindowExtendForm`): `bg-bg-subtle border-border-base text-tx-primary`
- [ ] Nút gia hạn: `bg-amber-500/10 text-amber-600 dark:text-amber-400 border-amber-500/30`
- [ ] Nút reject: `bg-red-500/10 text-red-600 dark:text-red-400 border-red-500/30`
- [ ] Info box restriction type: `bg-bg-elevated border-border-base/50`

#### `FilterDropdown.tsx` (mới tạo ở Guide 4)
- [ ] Trigger button (không filter): `bg-bg-subtle border-border-base text-tx-secondary`
- [ ] Trigger button (đang filter): `bg-brand-DEFAULT/10 border-brand-DEFAULT/50 text-brand-DEFAULT`
- [ ] Dropdown container: `bg-bg-elevated border-border-base shadow-xl shadow-black/20`
- [ ] Option active: `bg-brand-DEFAULT/10 text-brand-DEFAULT`
- [ ] Option hover: `hover:bg-bg-subtle hover:text-tx-primary`

#### `NotificationsPage.tsx`
- [ ] Tab bar container: `bg-bg-subtle border-border-base`
- [ ] Tab indicator (sliding): `bg-bg-surface border-border-base/80`
- [ ] Tab text active: `text-tx-primary`
- [ ] Tab text inactive: `text-tx-secondary`
- [ ] "Đánh dấu tất cả đọc" button: `text-tx-secondary hover:text-tx-primary border-border-base bg-bg-subtle hover:bg-bg-surface`
- [ ] Badge unread count: có màu đúng dark/light

#### `WarningConfigModal.tsx`
- [ ] Modal background: `bg-bg-surface`
- [ ] Input fields: `bg-bg-subtle border-border-base text-tx-primary`
- [ ] Tab trong modal: dùng pattern giống tab bar chính

#### `WebsiteCard.tsx`
- [ ] Card: `bg-bg-surface border-border-base`
- [ ] Badge `limit_exceeded`: màu đỏ với dark variant
- [ ] Badge active/inactive: dùng CSS variables

#### `ChildDetailPage.tsx` (Kill switch card)
- [ ] Kill switch card: `bg-bg-surface border-border-base`
- [ ] Toggle đang bật (paused): `bg-red-500`
- [ ] Toggle đang tắt: `bg-border-base` hoặc `bg-bg-muted`
- [ ] Warning text khi paused: `text-red-500 dark:text-red-400`

### Cách sửa nếu phát hiện hardcode màu

```tsx
// ❌ SAI — hardcode màu, sẽ sai trong dark mode
className="bg-white text-gray-900 border-gray-200"
className="bg-gray-50 text-gray-600"

// ✅ ĐÚNG — dùng CSS variables
className="bg-bg-surface text-tx-primary border-border-base"
className="bg-bg-subtle text-tx-secondary"
```

> **Lưu ý:** Chỉ sửa class CSS, KHÔNG thay đổi logic, state, hay handlers.

---

## Extension — Kiểm tra (Chỉ đọc, không sửa logic)

> ⚠️ Phần này là kiểm tra. KHÔNG thay đổi logic nào trong extension đang chạy.

### Kiểm tra `background.js`

Đọc file và verify các điểm sau (chỉ confirm, không sửa nếu đang đúng):

1. **Heartbeat flow:** Mỗi 30s gọi `/heartbeat` → kiểm tra `limitExceeded` → nếu true thì `chrome.tabs.update` ngay (không await gì trước đó)
2. **Warning flow:** Dùng `chrome.alarms` (precise alarm) cho cảnh báo, KHÔNG dùng alarm để block
3. **`showBannerAsync` là fire-and-forget:** KHÔNG có `await showBannerAsync(...)` trước block logic
4. **`activeTab = null`** được set trước `chrome.tabs.update` khi block

**Nếu extension có lắng nghe SignalR `AccessApproved`:** Verify nó trigger poll `/check` hoặc redirect ngay.
**Nếu KHÔNG có:** Đây là lý do blocked.js cần polling interval ngắn (5-10s) hoặc cần thêm listener.

### Kiểm tra `blocked.js` / `blocked.html`

Đọc script trong `blocked.html`. Tìm:
- Có `startPassivePolling()` chạy ngay khi trang load không? (từ Guide 4)
- Polling gọi endpoint nào? → Phải là `/api/extension/check?domain=...`
- Polling interval là bao nhiêu giây?

**Nếu polling interval > 10s và KHÔNG có SignalR:** Giảm xuống 5-10s để UX tốt hơn sau khi guardian approve.

> ⚠️ Chỉ sửa polling interval nếu cần. KHÔNG sửa bất kỳ logic block/allow nào khác.

---

## Tóm tắt thứ tự làm việc

```
1. [Backend] Đọc CheckAccessAsync → thêm bonus override logic
2. [Backend] Đọc DailyUsageStat.cs → verify BonusSeconds property
3. [Backend] Đọc RespondToRequestAsync extend_time → verify SaveChanges trước SignalR
4. [Backend] Đọc UpdateHeartbeatAsync → verify dùng effectiveSeconds
5. [Frontend] Fix Vercel: tạo .npmrc + xóa + regenerate package-lock.json
6. [Frontend] Rà soát dark mode các component mới từ Guide 4
7. [Extension] Đọc blocked.js → verify polling interval (sửa nếu cần)
8. Test end-to-end: hết giờ → bị block → guardian gia hạn → con tự vào được
```

---

## Lưu ý cuối

- **Không commit** `node_modules/` lên git
- Nếu sau khi thêm `.npmrc` mà local Windows vẫn build được → commit cả 2 file lên
- Nếu sau Bug 1 fix vẫn không work: log kết quả SP trả về trong `CheckAccessAsync` để xác định chính xác tên field và giá trị `reason` mà SP trả về khi hết giờ
- Dark mode toggle phải hoạt động tức thì, không cần reload trang
