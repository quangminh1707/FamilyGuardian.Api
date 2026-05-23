# Family Guardian — Fix blocked.html hiển thị sai lý do chặn (Phần 13)

> **Ngày tạo:** 2026-05-21
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE_12.md (Phần 12)

---

## ⚠️ Quy tắc bất di bất dịch

| Quy tắc | Chi tiết |
|---------|---------|
| `sp_ExtensionCheckAccess` | Tuyệt đối KHÔNG sửa |
| Block/heartbeat logic trong `background.js` | KHÔNG thay đổi |
| Logic hiện tại | KHÔNG thay đổi, chỉ sửa bug |
| Dark mode | CSS variables, không hardcode màu |

---

## SQL cần chạy

Không có SQL mới. Các SQL từ Guide 9 đã đủ.

---

## Phân tích nguyên nhân gốc rễ

### Nguyên nhân 1 — `#block-reason` không bao giờ được cập nhật

`blocked.html` có element `#block-reason` với text mặc định:
```html
<p id="block-reason">Không có trong danh sách được phép</p>
```

`updateBlockedUI` từ Guide 10 chỉ update `#block-reason-text` và `#block-info-detail`.
`#block-reason` **không bao giờ bị xóa** → luôn hiện "Không có trong danh sách được phép" dù bị chặn vì bất kỳ lý do gì.

### Nguyên nhân 2 — Backend `/check` thiếu case `internet_paused`

Khi `users.internet_paused = true`, SP trả về `allowed = false` nhưng backend không set `reason = "internet_paused"` trong response → `blocked.js` nhận `reason = null/""` → hiện text sai.

### Nguyên nhân 3 — `blocked.js` không đọc URL params khi khởi động

Page load → `blocked.js` init → không đọc `?reason=...` từ URL → đợi slow poll 20s → trong 20s đầu UI hiện text mặc định sai.

### Nguyên nhân 4 — `#block-reason-msg` (trong request section) cũng hiện sai

Element này dùng để hiển thị lý do trong phần "Gửi yêu cầu". Hiện tại nó không được cập nhật theo `reason`.

---

## PHẦN A — Fix Backend `/check` endpoint

### A.1 — Kiểm tra trước

Mở `ExtensionService.cs` (hoặc service xử lý `/check`). Tìm method `CheckAccessAsync` (hoặc tương đương).

Đọc toàn bộ method. Xác định:
- Chỗ nào trả về `allowed = false`?
- `Reason` đã được set chưa? Set ở đâu?
- Có check `user.InternetPaused` không?

Mở `CheckAccessResult.cs` (hoặc class response của `/check`). Xác nhận có các fields:
```csharp
public bool Allowed { get; set; }
public string? Reason { get; set; }
public string? BlockMode { get; set; }
public int? LimitMinutes { get; set; }
public int? UsedSeconds { get; set; }
public string? TimeWindowStart { get; set; }
public string? TimeWindowEnd { get; set; }
```

### A.2 — Thêm case `internet_paused` vào `CheckAccessAsync`

Tìm trong method phần kiểm tra `internet_paused`. Thường có dạng:
```csharp
if (user.InternetPaused) {
    return new CheckAccessResult { Allowed = false, ... };
}
```

Thêm `Reason = "internet_paused"` vào đây (KHÔNG thay đổi logic allow/deny):

```csharp
// Tìm đoạn check internet_paused — thêm Reason:
if (user.InternetPaused)
{
    return new CheckAccessResult
    {
        Allowed = false,
        Reason = "internet_paused",    // ← THÊM DÒNG NÀY
        // Giữ nguyên các field khác đang có
    };
}
```

### A.3 — Đảm bảo case `not_in_whitelist` cũng set Reason

Tìm đoạn trả về `allowed = false` khi domain không có trong whitelist:

```csharp
// Tìm đoạn này — thêm Reason nếu chưa có:
if (website == null || !website.IsActive)
{
    return new CheckAccessResult
    {
        Allowed = false,
        Reason = "not_in_whitelist",    // ← THÊM nếu chưa có
    };
}
```

### A.4 — Verify các case đã có từ Guide 9/10

Mở phần populate sau khi xác định blocked. Đảm bảo ĐỦ 4 case:

```csharp
// Case 1: internet_paused → đã thêm ở A.2
// Case 2: not_in_whitelist → đã thêm ở A.3
// Case 3: time_limit_exceeded → từ Guide 9/10
if (website.TimeLimitMinutes != null && limitExceeded)
{
    result.Reason       = "time_limit_exceeded";
    result.LimitMinutes = website.TimeLimitMinutes;
    result.UsedSeconds  = effectiveSeconds;
}
// Case 4: outside_time_window → từ Guide 9/10
else if (website.AllowedStartTime != null && outsideWindow)
{
    result.Reason          = "outside_time_window";
    result.TimeWindowStart = website.AllowedStartTime?.ToString(@"HH\:mm");
    result.TimeWindowEnd   = website.AllowedEndTime?.ToString(@"HH\:mm");
}
```

> ⚠️ KHÔNG thay đổi logic xác định blocked/allowed. Chỉ đảm bảo `Reason` được set đúng cho mỗi case.

---

## PHẦN B — Fix `blocked.js`

### B.1 — Kiểm tra trước

Upload `blocked.js` nếu chưa upload. Đọc toàn bộ file. Xác định:
- Cách đọc URL params (domain, reason, url)
- Hàm `startPassivePolling` / `slowPollStatus` / `fastPollCheck`
- Hàm `updateBlockedUI` (thêm từ Guide 10)
- Event listener của nút "Gửi yêu cầu"

### B.2 — Sửa hàm `initBlockedPage` (hoặc code chạy khi load)

Tìm đoạn khởi tạo trang — nơi đọc URL params. Thêm logic hiển thị ngay lập tức từ URL params (KHÔNG đợi slow poll):

```javascript
// Trong hàm init (đọc URL params) — thêm sau khi đọc domain/reason/url:
const urlParams = new URLSearchParams(window.location.search);
const domain    = urlParams.get('domain') || '';
const reason    = urlParams.get('reason') || 'not_in_whitelist';
const fullUrl   = urlParams.get('url')    || '';

// ── THÊM: Hiển thị ngay từ URL params, không đợi slow poll ──
applyReasonUI(reason, null); // null vì chưa có data chi tiết từ API
// ── KẾT THÚC THÊM ──
```

### B.3 — Tách hàm hiển thị thành `applyReasonUI` (thay thế `updateBlockedUI`)

> ⚠️ Nếu đã có `updateBlockedUI` từ Guide 10 → THAY THẾ bằng hàm mới này (đổi tên + mở rộng).
> Giữ nguyên logic detect config change bên trong.

```javascript
// ── THAY THẾ updateBlockedUI bằng applyReasonUI ──
function applyReasonUI(reason, data) {
  // ── 4 element cần update ──
  const reasonEl     = document.getElementById('block-reason');        // text dưới domain
  const reasonTextEl = document.getElementById('block-reason-text');   // dòng in đậm
  const infoDetailEl = document.getElementById('block-info-detail');   // chi tiết
  const reasonMsgEl  = document.getElementById('block-reason-msg');    // trong request section

  switch (reason) {
    case 'time_limit_exceeded': {
      const limit   = data?.limitMinutes ?? '?';
      const usedMin = data?.usedSeconds != null
        ? Math.floor(data.usedSeconds / 60)
        : null;

      if (reasonEl)
        reasonEl.textContent = `Đã hết ${limit} phút cho phép hôm nay`;
      if (reasonTextEl)
        reasonTextEl.textContent = 'Đã hết thời gian sử dụng';
      if (infoDetailEl)
        infoDetailEl.textContent = usedMin != null
          ? `Đã dùng: ${usedMin} phút / ${limit} phút`
          : `Giới hạn: ${limit} phút/ngày`;
      if (reasonMsgEl)
        reasonMsgEl.textContent = `🕐 Bạn đã dùng hết ${limit} phút được cho phép hôm nay.\nGửi yêu cầu để được thêm thời gian.`;
      break;
    }

    case 'outside_time_window': {
      const start = data?.timeWindowStart ?? '--:--';
      const end   = data?.timeWindowEnd   ?? '--:--';

      if (reasonEl)
        reasonEl.textContent = `Ngoài khung giờ cho phép`;
      if (reasonTextEl)
        reasonTextEl.textContent = 'Ngoài khung giờ cho phép';
      if (infoDetailEl)
        infoDetailEl.textContent = (start !== '--:--' && end !== '--:--')
          ? `Khung giờ được phép: ${start} – ${end}`
          : 'Website này chỉ được dùng trong khung giờ nhất định';
      if (reasonMsgEl)
        reasonMsgEl.textContent = `⏰ Website này chỉ được truy cập trong khung giờ ${start} – ${end}.\nGửi yêu cầu để được truy cập ngoài giờ.`;
      break;
    }

    case 'internet_paused': {
      if (reasonEl)
        reasonEl.textContent = 'Internet đã bị tạm dừng bởi bố/mẹ';
      if (reasonTextEl)
        reasonTextEl.textContent = 'Internet đang bị tạm dừng';
      if (infoDetailEl)
        infoDetailEl.textContent = 'Tất cả kết nối web đang bị chặn hoàn toàn';
      if (reasonMsgEl)
        reasonMsgEl.textContent = `🚫 Bố/mẹ đã tạm dừng toàn bộ internet.\nGửi yêu cầu để được bật lại.`;
      break;
    }

    default: { // not_in_whitelist hoặc không xác định
      if (reasonEl)
        reasonEl.textContent = 'Không có trong danh sách được phép';
      if (reasonTextEl)
        reasonTextEl.textContent = 'Trang web bị chặn';
      if (infoDetailEl)
        infoDetailEl.textContent = 'Website này chưa được bố/mẹ cho phép';
      if (reasonMsgEl) {
        const domainDisplay = domain || 'trang web này';
        reasonMsgEl.textContent = `🌐 Trang ${domainDisplay} chưa được bố/mẹ cho phép.\nGửi yêu cầu để được duyệt truy cập.`;
      }
      break;
    }
  }
}
// ── KẾT THÚC ──
```

### B.4 — Sửa `slowPollStatus` gọi `applyReasonUI` thay `updateBlockedUI`

Tìm trong `slowPollStatus` (hoặc hàm poll chính):

```javascript
// Tìm chỗ gọi updateBlockedUI — đổi thành applyReasonUI:
// ❌ CŨ:
// updateBlockedUI(data);

// ✅ MỚI:
if (!data.allowed) {
  applyReasonUI(data.reason || 'not_in_whitelist', data);

  // Giữ nguyên logic detect config change từ Guide 10:
  const newConfig = JSON.stringify({
    limitMinutes:    data?.limitMinutes,
    timeWindowStart: data?.timeWindowStart,
    timeWindowEnd:   data?.timeWindowEnd
  });
  if (lastConfig !== null && lastConfig !== newConfig) {
    window.location.reload();
    return;
  }
  lastConfig = newConfig;
}
```

### B.5 — Thêm gọi `applyReasonUI` trong lần đầu load

Đảm bảo khi trang load, ngay lập tức gọi `applyReasonUI` với reason từ URL params (KHÔNG đợi slow poll):

```javascript
// Trong hàm init — sau khi đọc URL params domain, reason, url:

// ── THÊM: Gọi ngay để hiện đúng UI trước khi slow poll chạy ──
applyReasonUI(reason, null);

// Nếu internet_paused → KHÔNG show nút "Gửi yêu cầu" (optional)
if (reason === 'internet_paused') {
  const requestSection = document.getElementById('request-section');
  // Giữ nút nhưng đổi text
  const btnText = document.getElementById('btn-request-text');
  if (btnText) btnText.textContent = 'Gửi yêu cầu bật lại internet';
}
// ── KẾT THÚC THÊM ──
```

---

## PHẦN C — Kiểm tra `background.js` (chỉ verify)

### C.1 — Verify reason được truyền đúng vào blocked.html URL

Tìm 2 chỗ tạo URL blocked.html trong background.js:

**Chỗ 1 — checkDomain block (line ~191-193):**
```javascript
chrome.tabs.update(tabId, {
  url: chrome.runtime.getURL("blocked.html")
    + `?domain=${encodeURIComponent(domain)}&reason=${encodeURIComponent(result.reason)}&url=...`
});
```
→ `result.reason` = `data.reason` từ API `/check` → sau khi fix backend (Phần A), sẽ trả đúng reason.

**Chỗ 2 — Heartbeat block (line ~246-252):**
```javascript
chrome.tabs.update(tabId, {
  url: chrome.runtime.getURL("blocked.html")
    + `?domain=...&reason=${encodeURIComponent("time_limit_exceeded")}&url=...`
});
```
→ Hardcode `"time_limit_exceeded"` → ĐÃ ĐÚNG, không cần sửa.

> ✅ Không cần thay đổi `background.js` trong phần này.

### C.2 — Verify `normalizeAccessReason` từ Guide 12

Đảm bảo hàm này đã được thêm (từ Guide 12 Phần A.3):
```javascript
function normalizeAccessReason(reason) { ... }
```
→ Được dùng trong `REQUEST_ACCESS` handler.
→ KHÔNG thay đổi gì.

---

## PHẦN D — Kiểm tra `blocked.html`

### D.1 — Verify 4 elements tồn tại với đúng ID

Mở `blocked.html`. Đảm bảo 4 elements này đều có trong HTML:

```html
<!-- Element 1: dòng reason chính dưới tên domain -->
<p id="block-reason" class="reason">Không có trong danh sách được phép</p>

<!-- Element 2: dòng in đậm (đã có từ Guide 10) -->
<p id="block-reason-text" class="reason" style="font-weight: 700; color: #fff;">
  Trang web này đang bị chặn
</p>

<!-- Element 3: chi tiết (đã có từ Guide 10) -->
<p id="block-info-detail" class="reason" style="font-size: 13px; color: rgba(229,231,235,0.7);">
  <!-- JS sẽ điền -->
</p>

<!-- Element 4: trong request-section -->
<div id="block-reason-msg">Đang kiểm tra...</div>
```

Nếu thiếu element nào → thêm vào đúng vị trí trong HTML hiện có.

Dựa vào `blocked.html` đã upload, các elements này ĐÃ TỒN TẠI. Chỉ cần đảm bảo `blocked.js` update đúng tất cả chúng.

---

## Thứ tự làm việc

```
A1  — Mở ExtensionService.cs / CheckAccessAsync — đọc toàn bộ method
A2  — Thêm Reason = "internet_paused" khi InternetPaused = true
A3  — Thêm Reason = "not_in_whitelist" khi domain không trong whitelist
A4  — Verify 4 cases đều có Reason (internet_paused, not_in_whitelist, time_limit_exceeded, outside_time_window)

B1  — Mở blocked.js — đọc toàn bộ
B2  — Thêm / Thay thế updateBlockedUI bằng applyReasonUI (4 cases đầy đủ)
B3  — Thêm gọi applyReasonUI(reason, null) ngay trong init (đọc URL params)
B4  — Sửa slowPollStatus gọi applyReasonUI(data.reason, data) thay updateBlockedUI(data)
B5  — Giữ nguyên logic detect config change trong slowPollStatus

C1  — Verify background.js — 2 chỗ tạo URL blocked.html (không cần sửa)

D1  — Verify blocked.html — 4 elements có đúng ID

REBUILD: node build-config.js → reload extension
TEST: test cả 4 trường hợp (xem phần Test bên dưới)
```

---

## Checklist kiểm tra

### Backend
- [ ] `CheckAccessResult.cs` có đủ 7 fields (Allowed, Reason, BlockMode, LimitMinutes, UsedSeconds, TimeWindowStart, TimeWindowEnd)?
- [ ] Trường hợp `internet_paused` → `Reason = "internet_paused"` được set?
- [ ] Trường hợp domain không trong whitelist → `Reason = "not_in_whitelist"` được set?
- [ ] Trường hợp `time_limit_exceeded` → `Reason + LimitMinutes + UsedSeconds` đều được set?
- [ ] Trường hợp `outside_time_window` → `Reason + TimeWindowStart + TimeWindowEnd` đều được set?
- [ ] Tất cả logic allow/block KHÔNG bị thay đổi?

### Extension (blocked.js)
- [ ] Hàm `applyReasonUI(reason, data)` có đủ 4 cases (time_limit_exceeded, outside_time_window, internet_paused, default)?
- [ ] Hàm cập nhật đủ 4 elements (#block-reason, #block-reason-text, #block-info-detail, #block-reason-msg)?
- [ ] `applyReasonUI` được gọi ngay khi init từ URL params?
- [ ] `slowPollStatus` gọi `applyReasonUI` sau mỗi poll?
- [ ] Logic detect config change còn nguyên?
- [ ] Logic redirect khi `data.allowed = true` còn nguyên?
- [ ] Event listener nút "Gửi yêu cầu" KHÔNG bị ảnh hưởng?

### blocked.html
- [ ] Có đủ 4 elements với đúng ID?

---

## Test

```
TEST 1 — Chặn do "Không trong danh sách"
Website không có trong whitelist của con
→ blocked.html hiện: "Không có trong danh sách được phép"
→ #block-reason-text: "Trang web bị chặn"
→ Nút Gửi yêu cầu → reason gửi lên = "not_in_whitelist"

TEST 2 — Chặn do "Hết giới hạn phút"
Website có time_limit_minutes = 30, con đã dùng 30 phút
→ blocked.html hiện: "Đã hết 30 phút cho phép hôm nay"
→ #block-reason-text: "Đã hết thời gian sử dụng"
→ #block-info-detail: "Đã dùng: 30 phút / 30 phút"
→ Nút Gửi yêu cầu → reason gửi lên = "time_limit_exceeded"

TEST 3 — Chặn do "Ngoài khung giờ"
Website có allowed_start_time = 07:00, allowed_end_time = 21:00
Con truy cập lúc 22:00
→ blocked.html hiện: "Ngoài khung giờ cho phép"
→ #block-reason-text: "Ngoài khung giờ cho phép"
→ #block-info-detail: "Khung giờ được phép: 07:00 – 21:00"
→ Nút Gửi yêu cầu → reason gửi lên = "outside_time_window"

TEST 4 — Internet Pause
Guardian bật tính năng "Tạm dừng internet"
Con vào bất kỳ website nào (kể cả website trong whitelist)
→ blocked.html hiện: "Internet đã bị tạm dừng bởi bố/mẹ"
→ #block-reason-text: "Internet đang bị tạm dừng"
→ #block-info-detail: "Tất cả kết nối web đang bị chặn hoàn toàn"
→ Nút có text "Gửi yêu cầu bật lại internet"

TEST 5 — Gửi yêu cầu + reason đúng
Trong mỗi test trên, nhấn "Gửi yêu cầu"
→ Backend nhận đúng reason tương ứng (không bị "not_in_whitelist" mặc định)
→ Guardian nhận notification với reason đúng
```
