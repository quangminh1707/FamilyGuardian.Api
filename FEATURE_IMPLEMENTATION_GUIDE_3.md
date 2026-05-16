# Family Guardian — Hướng dẫn triển khai Fix & Feature (Phần 3)

> **Ngày tạo:** 2026-05-11  
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE_2.md (Phần 2)  
> **SQL cần chạy:** Không có

---

## ⚠️ Quy tắc bất di bất dịch

| Quy tắc | Chi tiết |
|---------|---------|
| `sp_ExtensionCheckAccess` | Tuyệt đối KHÔNG sửa |
| Extension background.js | KHÔNG thay đổi logic đang chạy. Chỉ được THÊM mới |
| DateTime | Backend dùng `DateTime.Now` (local UTC+7) |
| Logic desktop | Giao diện PC/laptop KHÔNG được thay đổi |
| Logic hiện tại | KHÔNG thay đổi bất kỳ logic nào đang hoạt động |

---

## Fix 1: Timezone — Hiển thị sai giờ (11:20 → 18:20)

### Nguyên nhân gốc rễ

Backend dùng `DateTime.Now` (local UTC+7) → lưu `"2026-05-11T11:20:36"` (không có suffix).

Guide Phần 2 đã hướng dẫn sai: append `'Z'` vào string → JavaScript parse thành UTC → convert sang local UTC+7 → cộng thêm 7h → hiển thị **18:20** thay vì **11:20**.

```
"2026-05-11T11:20:36" + "Z" = UTC 11:20 = local 18:20  ← SAI
"2026-05-11T11:20:36" (no suffix) → JS parse = local 11:20  ← ĐÚNG
```

**FIX: KHÔNG append 'Z'.** Vì backend đã lưu local time, JS `new Date()` tự parse đúng.

### Phạm vi
**Chỉ Frontend.** Không cần sửa backend, không cần sửa extension.

### Bước 1: Kiểm tra trước

- Đọc `src/lib/formatters.ts` — tìm hàm `normalizeBackendDate` đã thêm ở Phần 2
- Đọc tất cả file đang import và dùng `normalizeBackendDate` hoặc `formatDateTimeVN`
- Xác nhận hàm đang append `'Z'` — đây là nguyên nhân bug

### Bước 2: Sửa `src/lib/formatters.ts`

Tìm hàm `normalizeBackendDate` đã thêm ở Phần 2, **thay thế hoàn toàn** bằng:

```typescript
/**
 * Parse datetime string từ backend.
 * Backend dùng DateTime.Now (local UTC+7) → string không có suffix
 * → JS new Date() tự hiểu là local time → ĐÚNG, KHÔNG cần append 'Z'
 * Nếu string đã có suffix (Z hoặc +07:00) → giữ nguyên
 */
export function normalizeBackendDate(dateStr: string): Date {
  if (!dateStr) return new Date();
  // Nếu đã có timezone suffix → parse bình thường
  if (dateStr.endsWith('Z') || /[+-]\d{2}:\d{2}$/.test(dateStr)) {
    return new Date(dateStr);
  }
  // KHÔNG có suffix → DateTime.Now local → parse trực tiếp (không thêm 'Z')
  return new Date(dateStr);
}

/**
 * Format giờ 24h: "16:27", "08:05"
 */
export function formatTimeVN(dateStr: string): string {
  const date = normalizeBackendDate(dateStr);
  const h = date.getHours().toString().padStart(2, '0');
  const m = date.getMinutes().toString().padStart(2, '0');
  return `${h}:${m}`;
}

/**
 * Format thời gian tương đối: "vừa xong", "5 phút trước", "2 giờ trước"
 * Dùng Date.now() để tính diff — không phụ thuộc timezone server
 */
export function formatRelativeTime(dateStr: string): string {
  const date = normalizeBackendDate(dateStr);
  const diffMs = Date.now() - date.getTime();
  const diffSec = Math.floor(diffMs / 1000);
  if (diffSec < 60) return 'vừa xong';
  const diffMin = Math.floor(diffSec / 60);
  if (diffMin < 60) return `${diffMin} phút trước`;
  const diffH = Math.floor(diffMin / 60);
  if (diffH < 24) return `${diffH} giờ trước`;
  const diffD = Math.floor(diffH / 24);
  if (diffD === 1) return 'hôm qua';
  return `${diffD} ngày trước`;
}

/**
 * Format ngày + giờ đầy đủ cho card yêu cầu
 * Kết quả: "Hôm nay 16:27", "Hôm qua 08:05", "10/05 14:30"
 */
export function formatDateTimeVN(dateStr: string): string {
  const date = normalizeBackendDate(dateStr);
  const now = new Date();

  const isToday =
    date.getDate() === now.getDate() &&
    date.getMonth() === now.getMonth() &&
    date.getFullYear() === now.getFullYear();
  if (isToday) return `Hôm nay ${formatTimeVN(dateStr)}`;

  const yesterday = new Date(now);
  yesterday.setDate(now.getDate() - 1);
  const isYesterday =
    date.getDate() === yesterday.getDate() &&
    date.getMonth() === yesterday.getMonth() &&
    date.getFullYear() === yesterday.getFullYear();
  if (isYesterday) return `Hôm qua ${formatTimeVN(dateStr)}`;

  const d = date.getDate().toString().padStart(2, '0');
  const mo = (date.getMonth() + 1).toString().padStart(2, '0');
  return `${d}/${mo} ${formatTimeVN(dateStr)}`;
}
```

### Bước 3: Áp dụng relative time trong AccessRequestCard

Tìm chỗ hiển thị `requestedAt` trong `AccessRequestCard.tsx`, sửa thành:

```tsx
// Hiện relative time thay vì giờ tuyệt đối
<span className="text-xs text-tx-secondary flex-shrink-0" title={formatDateTimeVN(request.requestedAt)}>
  {formatRelativeTime(request.requestedAt)}
</span>
```

Tooltip (`title`) vẫn hiện giờ đầy đủ khi hover.

### Bước 4: Kiểm tra notification page

Tìm tất cả chỗ hiển thị `createdAt` trong notification list, áp dụng `formatRelativeTime()`.

---

## Fix 2: Filter theo Reason trong tab Yêu cầu

### Phạm vi
**Chỉ Frontend.** Thêm filter chips cho reason bên trong mỗi status filter.

### Kiểm tra trước
- Đọc `NotificationsPage.tsx` — xem state `requestFilter` và filter chips đã thêm ở Phần 2
- Xác nhận `AccessRequestDto` có field `reason` (đã thêm ở Phần 2)

### Sửa `NotificationsPage.tsx`

Thêm state reason filter (KHÔNG xóa state cũ):
```tsx
const [reasonFilter, setReasonFilter] = useState<'all' | 'internet_paused' | 'time_limit_exceeded' | 'not_in_whitelist'>('all');
```

Áp dụng reason filter vào danh sách:
```tsx
// Lọc requests theo reason (client-side, không cần API mới)
const filteredRequests = (requests ?? []).filter(req =>
  reasonFilter === 'all' ? true : req.reason === reasonFilter
);
```

Thêm reason filter chips (đặt DƯỚI status filter chips, TRÊN danh sách):
```tsx
{activeTab === 'requests' && (
  <div className="flex flex-wrap gap-2 mb-4">
    {([
      { value: 'all', label: 'Tất cả loại' },
      { value: 'internet_paused', label: '⏸ Tạm dừng Internet' },
      { value: 'time_limit_exceeded', label: '⏱ Hết giờ sử dụng' },
      { value: 'not_in_whitelist', label: '🌐 Web mới' },
    ] as const).map(f => (
      <button
        key={f.value}
        onClick={() => setReasonFilter(f.value)}
        className={`px-3 py-1.5 text-xs font-medium rounded-full border transition-colors ${
          reasonFilter === f.value
            ? 'bg-bg-surface border-tx-secondary text-tx-primary'
            : 'bg-bg-subtle border-border-base text-tx-secondary hover:border-tx-secondary/50'
        }`}
      >
        {f.label}
      </button>
    ))}
  </div>
)}
```

Dùng `filteredRequests` thay vì `requests` trong render list.

> **Reset reason filter khi đổi status filter:**
```tsx
const handleStatusFilterChange = (f: typeof requestFilter) => {
  setRequestFilter(f);
  setReasonFilter('all'); // reset reason khi đổi status
};
```

---

## Fix 3: Tab Thông Báo — Thu gọn / Xem thêm

### Phạm vi
**Chỉ Frontend.** Thêm pagination đơn giản vào danh sách thông báo.

### Kiểm tra trước
- Đọc phần render notification list trong `NotificationsPage.tsx`
- Xác nhận danh sách notifications đang render như thế nào (map toàn bộ)

### Sửa `NotificationsPage.tsx`

Thêm state:
```tsx
const NOTIF_PAGE_SIZE = 5; // hiển thị 5 thông báo đầu
const [notifShowAll, setNotifShowAll] = useState(false);
```

Áp dụng khi render (thêm vào logic filter đang có):
```tsx
// Sau khi đã filter theo notifFilter (read/unread/all)
const visibleNotifications = notifShowAll
  ? filteredNotifications
  : filteredNotifications.slice(0, NOTIF_PAGE_SIZE);

const hasMore = filteredNotifications.length > NOTIF_PAGE_SIZE;
```

Thêm nút Xem thêm / Thu gọn sau danh sách:
```tsx
{/* Sau vòng lặp render notifications */}
{hasMore && (
  <button
    onClick={() => setNotifShowAll(prev => !prev)}
    className="w-full mt-3 py-2.5 text-sm font-medium text-tx-secondary
               hover:text-tx-primary border border-border-base rounded-xl
               bg-bg-subtle hover:bg-bg-surface transition-colors"
  >
    {notifShowAll
      ? '↑ Thu gọn'
      : `↓ Xem thêm ${filteredNotifications.length - NOTIF_PAGE_SIZE} thông báo`}
  </button>
)}
```

> Reset `notifShowAll = false` khi đổi notifFilter:
```tsx
const handleNotifFilterChange = (f: typeof notifFilter) => {
  setNotifFilter(f);
  setNotifShowAll(false);
};
```

---

## Fix 4 & 5: Extension blocked.html — Đơn giản hóa UI

### Tổng quan thay đổi
- **Bỏ hoàn toàn:** phần "Giới hạn phút mỗi ngày" và "Khung giờ cho phép" (guardian mới được config, không phải con)
- **Bỏ hoàn toàn:** nút 15 phút / 30 phút / 60 phút trong phần hết giờ
- **Giữ lại:** context message theo reason + 1 nút "Gửi yêu cầu" đơn giản
- Con chỉ gửi yêu cầu, KHÔNG config thời gian — guardian mới là người quyết định

### Kiểm tra Extension trước

Đọc kỹ các file sau, KHÔNG thay đổi bất kỳ logic nào đang chạy:

**`blocked.html`:**
- Xác nhận cấu trúc HTML hiện tại
- Tìm `id="request-section"` (thêm ở Phần 1 & 2)
- Tìm các element time config (`id="time-config-section"`, `id="extra-time-section"`)

**`blocked.js`:**
- Xác nhận URL params đang đọc: tên param `domain`, `reason`, `url`
- Xác nhận token key trong `chrome.storage.local` (đọc `background.js`)
- Xác nhận `CONFIG.API_BASE` đang dùng

**`background.js`:**
- Tìm chỗ redirect sang blocked.html: `chrome.tabs.update(tabId, { url: blockedUrl })`
- Xem `blockedUrl` được build như thế nào — note tên URL params (`domain=`, `reason=`, `url=`)
- **KHÔNG thay đổi gì trong background.js**

### Sửa `blocked.html`

Tìm và **thay thế toàn bộ** block `id="request-section"` bằng phiên bản đơn giản:

```html
<!-- Thay thế toàn bộ #request-section đang có -->
<div id="request-section" style="margin-top: 24px; padding-top: 20px; border-top: 1px solid rgba(255,255,255,0.08);">

  <!-- Loading -->
  <div id="block-reason-msg" style="
    padding: 10px 14px; border-radius: 8px; font-size: 13px;
    margin-bottom: 14px; line-height: 1.6;
    background: rgba(255,255,255,0.04);
    border: 1px solid rgba(255,255,255,0.08);
    color: rgba(255,255,255,0.65);
    min-height: 40px;
  ">
    Đang kiểm tra...
  </div>

  <!-- Nút gửi yêu cầu duy nhất -->
  <button id="btn-request-access" style="
    width: 100%; padding: 12px 20px;
    background: rgba(124,58,237,0.2); color: #c4b5fd;
    border: 1px solid rgba(124,58,237,0.4);
    border-radius: 10px; font-size: 14px; font-weight: 500;
    cursor: pointer; transition: all 0.2s;
    display: flex; align-items: center; justify-content: center; gap: 8px;
  ">
    <span>📨</span>
    <span id="btn-request-text">Gửi yêu cầu cho bố/mẹ</span>
  </button>

  <!-- Status message -->
  <div id="request-status" style="
    display: none; margin-top: 10px; padding: 10px 14px;
    border-radius: 8px; font-size: 13px; text-align: center;
  "></div>

  <!-- Polling indicator (ẩn, hiện khi đang chờ) -->
  <div id="polling-indicator" style="
    display: none; margin-top: 10px;
    font-size: 12px; color: rgba(255,255,255,0.35);
    text-align: center;
  ">
    ⟳ Đang kiểm tra trạng thái... trang sẽ tự mở khi được cho phép
  </div>
</div>
```

### Sửa `blocked.js`

Tìm và **thay thế toàn bộ** block request access cũ (từ Phần 1 & 2). Thêm code mới hoàn toàn, gọn hơn, không có time config:

> ⚠️ **Trước khi thay:** đọc background.js, xác nhận:
> 1. Tên URL param chứa domain: `domain` hay tên khác?
> 2. Key token trong chrome.storage.local: `googleToken` hay tên khác?
> 3. URL blocked.html được build như thế nào — xem `reason` được pass thế nào

```javascript
// ============================================================
// REQUEST ACCESS v3 — Thay thế hoàn toàn các version cũ
// KHÔNG sửa bất kỳ code nào khác trong file này
// ============================================================
(function initRequestAccessV3() {
  const reasonMsgEl = document.getElementById('block-reason-msg');
  const btnRequest = document.getElementById('btn-request-access');
  const btnText = document.getElementById('btn-request-text');
  const statusDiv = document.getElementById('request-status');
  const pollingEl = document.getElementById('polling-indicator');

  if (!btnRequest || !statusDiv) return;

  // ─── Config ────────────────────────────────────────────────
  // ⚠️ Đọc background.js để lấy đúng:
  const TOKEN_KEY = 'googleToken';  // SỬA theo key trong background.js
  const getApiBase = () =>
    typeof CONFIG !== 'undefined' ? CONFIG.API_BASE : '/api/extension';

  // ─── URL params ─────────────────────────────────────────────
  const urlParams = new URLSearchParams(window.location.search);
  // ⚠️ Đọc background.js để xác nhận tên params đang truyền vào blocked.html
  const blockedDomain = urlParams.get('domain') || '';
  // reason từ URL (background.js có thể truyền sẵn)
  const rawReason = urlParams.get('reason') || '';

  // Map raw reason string sang enum key nội bộ
  function detectReason(raw) {
    if (!raw) return 'not_in_whitelist';
    const lower = raw.toLowerCase();
    if (lower.includes('tạm dừng') || lower.includes('paused') || lower.includes('internet')) {
      return 'internet_paused';
    }
    if (lower.includes('hết') || lower.includes('giờ') || lower.includes('time') || lower.includes('limit')) {
      return 'time_limit_exceeded';
    }
    return 'not_in_whitelist';
  }

  let currentReason = detectReason(rawReason);
  let pollTimer = null;
  let pollCount = 0;
  const MAX_POLL = 40; // ~20 phút ở 30s/lần

  // ─── Helper: hiện status ────────────────────────────────────
  function showStatus(msg, type) {
    statusDiv.style.display = 'block';
    statusDiv.textContent = msg;
    const map = {
      success: { bg: 'rgba(34,197,94,0.12)', color: '#4ade80', border: 'rgba(34,197,94,0.25)' },
      error:   { bg: 'rgba(239,68,68,0.12)',  color: '#f87171', border: 'rgba(239,68,68,0.25)' },
      info:    { bg: 'rgba(124,58,237,0.12)', color: '#c4b5fd', border: 'rgba(124,58,237,0.25)' },
    };
    const c = map[type] || map.info;
    statusDiv.style.cssText += `
      background:${c.bg}; color:${c.color}; border:1px solid ${c.border};
    `;
  }

  // ─── Hiện context message theo reason ───────────────────────
  function renderReasonMessage() {
    if (!reasonMsgEl || !blockedDomain) return;
    if (currentReason === 'internet_paused') {
      reasonMsgEl.innerHTML = `⏸ <strong>Internet đang bị tạm dừng</strong> bởi phụ huynh.<br>
        Gửi yêu cầu để bố/mẹ bật lại.`;
      reasonMsgEl.style.borderColor = 'rgba(239,68,68,0.25)';
      reasonMsgEl.style.background = 'rgba(239,68,68,0.06)';
      if (btnText) btnText.textContent = 'Yêu cầu bật lại Internet';
    } else if (currentReason === 'time_limit_exceeded') {
      reasonMsgEl.innerHTML = `⏱ Bạn đã <strong>hết thời gian</strong> cho <strong>${blockedDomain}</strong> hôm nay.<br>
        Gửi yêu cầu để bố/mẹ gia hạn thêm.`;
      reasonMsgEl.style.borderColor = 'rgba(251,191,36,0.25)';
      reasonMsgEl.style.background = 'rgba(251,191,36,0.06)';
      if (btnText) btnText.textContent = 'Xin thêm thời gian';
    } else {
      reasonMsgEl.innerHTML = `🌐 Trang <strong>${blockedDomain}</strong> chưa được bố/mẹ cho phép.<br>
        Gửi yêu cầu để được duyệt truy cập.`;
      if (btnText) btnText.textContent = 'Gửi yêu cầu truy cập';
    }
  }

  // ─── Nếu không có reason từ URL, gọi block-info API ─────────
  async function loadReasonFromApi() {
    if (rawReason || !blockedDomain) {
      renderReasonMessage();
      return;
    }
    try {
      const stored = await chrome.storage.local.get([TOKEN_KEY]);
      const token = stored[TOKEN_KEY];
      if (!token) { renderReasonMessage(); return; }

      const res = await fetch(
        `${getApiBase()}/block-info?domain=${encodeURIComponent(blockedDomain)}`,
        { headers: { Authorization: `Bearer ${token}` } }
      );
      if (res.ok) {
        const info = await res.json();
        currentReason = info.reason || 'not_in_whitelist';
      }
    } catch { /* dùng default */ }
    renderReasonMessage();
  }

  // ─── Gửi request ────────────────────────────────────────────
  btnRequest.addEventListener('click', async () => {
    btnRequest.disabled = true;
    btnRequest.style.opacity = '0.5';
    if (btnText) btnText.textContent = 'Đang gửi...';

    try {
      const stored = await chrome.storage.local.get([TOKEN_KEY]);
      const token = stored[TOKEN_KEY];
      if (!token) throw new Error('no_token');

      const res = await fetch(`${getApiBase()}/request-access`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
        body: JSON.stringify({
          domain: blockedDomain,
          reason: currentReason,
          // KHÔNG gửi time config — đó là việc của phụ huynh
        }),
      });

      if (res.ok) {
        showStatus('✅ Đã gửi! Trang sẽ tự mở khi bố/mẹ duyệt.', 'success');
        if (btnText) btnText.textContent = 'Đã gửi yêu cầu';
        // Tăng tần suất poll sau khi gửi request
        startActivePolling();
      } else {
        const data = await res.json().catch(() => ({}));
        const msg = data.message || 'Gửi thất bại. Thử lại sau.';
        showStatus(msg, 'error');
        btnRequest.disabled = false;
        btnRequest.style.opacity = '1';
        renderReasonMessage();
      }
    } catch (e) {
      if (e.message === 'no_token') {
        showStatus('Không tìm thấy phiên đăng nhập. Mở extension và đăng nhập lại.', 'error');
      } else {
        showStatus('Lỗi kết nối. Kiểm tra mạng và thử lại.', 'error');
      }
      btnRequest.disabled = false;
      btnRequest.style.opacity = '1';
      renderReasonMessage();
    }
  });

  // ─── Passive polling: chạy ngay từ đầu, 30s/lần ────────────
  // Tự động redirect nếu guardian mở khóa mà không cần con gửi request
  async function checkAndRedirect() {
    if (!blockedDomain) return false;
    try {
      const stored = await chrome.storage.local.get([TOKEN_KEY]);
      const token = stored[TOKEN_KEY];
      if (!token) return false;

      // ⚠️ QUAN TRỌNG: Đọc ExtensionController.cs → /check endpoint
      // Xác nhận field name trong response (allowed, isAllowed, result...)
      const res = await fetch(
        `${getApiBase()}/check?domain=${encodeURIComponent(blockedDomain)}`,
        { headers: { Authorization: `Bearer ${token}` } }
      );
      if (!res.ok) return false;
      const data = await res.json();

      // ⚠️ Thay 'allowed' bằng field name thực tế trong response
      const isAllowed = data.allowed === true || data.isAllowed === true || data.result === true;
      if (isAllowed) {
        // Redirect về trang gốc
        const targetUrl = `https://${blockedDomain}`;
        window.location.href = targetUrl;
        return true;
      }
    } catch { /* bỏ qua lỗi mạng */ }
    return false;
  }

  function startPassivePolling() {
    // Poll ngay lần đầu sau 5 giây
    setTimeout(checkAndRedirect, 5000);

    // Sau đó mỗi 30 giây
    pollTimer = setInterval(async () => {
      pollCount++;
      if (pollCount > MAX_POLL) {
        clearInterval(pollTimer);
        if (pollingEl) pollingEl.style.display = 'none';
        return;
      }
      await checkAndRedirect();
    }, 30_000);
  }

  function startActivePolling() {
    // Sau khi gửi request, poll nhanh hơn: mỗi 10 giây
    if (pollTimer) clearInterval(pollTimer);
    pollCount = 0;
    if (pollingEl) pollingEl.style.display = 'block';

    // Check ngay sau 3 giây
    setTimeout(checkAndRedirect, 3000);

    pollTimer = setInterval(async () => {
      pollCount++;
      if (pollCount > MAX_POLL) {
        clearInterval(pollTimer);
        if (pollingEl) pollingEl.style.display = 'none';
        return;
      }
      const redirected = await checkAndRedirect();
      if (redirected) clearInterval(pollTimer);
    }, 10_000);
  }

  // ─── Khởi động ──────────────────────────────────────────────
  loadReasonFromApi();
  startPassivePolling(); // Bắt đầu passive poll ngay khi trang load
})();
```

### Bước quan trọng sau khi implement Extension

> ⚠️ Đọc `ExtensionController.cs` — tìm endpoint `GET /check` và xem object response:
> - Nếu trả `{ allowed: bool }` → code đã đúng
> - Nếu trả tên field khác → sửa dòng `const isAllowed = data.allowed === true || ...`

---

## Fix 6: Auto-reload blocked.html (tiếp theo Fix 4)

Fix 6 đã được tích hợp vào Fix 4 & 5 ở trên: `startPassivePolling()` chạy ngay khi trang load, không cần con phải nhấn "Gửi yêu cầu". Điều này giải quyết trường hợp guardian thêm website trực tiếp mà không có request từ con.

**Tóm tắt luồng:**
```
blocked.html load → startPassivePolling() chạy ngay
  → Poll /check mỗi 30s
  → Nếu allowed=true → window.location.href = targetUrl → tab tự chuyển về trang gốc

Con nhấn "Gửi yêu cầu" → startActivePolling() thay thế
  → Poll /check mỗi 10s (nhanh hơn)
  → Nếu allowed=true → redirect
```

**Backend không cần sửa gì cho fix này.** Chỉ cần xác nhận field name của `/check` response.

---

## Fix 7: Mobile Responsive — Sidebar hiện khi nhấn hamburger

### Phạm vi
**Chỉ Frontend — Chỉ mobile layout.** Desktop/PC layout KHÔNG thay đổi.

### Kiểm tra trước

**Đọc `src/layouts/AppLayout.tsx`:**
- Tìm button hamburger menu (thường là icon ☰ hoặc 3 dòng ngang)
- Xác nhận sidebar desktop đang dùng class nào để ẩn trên mobile: `hidden md:flex`, `hidden md:block`, v.v.
- Xem có component `Sidebar.tsx` riêng không hay inline trong AppLayout
- Tìm xem đã có `useState` nào cho mobile menu chưa

**Đọc tất cả page components:**
- Kiểm tra xem có page nào có layout bị vỡ trên mobile không (overflow, text bị cắt...)

**KHÔNG thay đổi:**
- Sidebar desktop (class `md:flex`, `md:block`)
- Main content layout trên desktop
- Bất kỳ logic nào đang chạy

### Sửa `AppLayout.tsx`

> ⚠️ Đọc file hiện tại TRƯỚC. Chỉ thêm mobile overlay, KHÔNG đụng desktop layout.

**Bước 1:** Thêm state (nếu chưa có):
```tsx
const [isMobileMenuOpen, setIsMobileMenuOpen] = useState(false);
```

**Bước 2:** Tìm button hamburger (icon ≡), thêm onClick:
```tsx
// Tìm button hiện có, thêm handler
<button onClick={() => setIsMobileMenuOpen(true)}>
  {/* icon đang có */}
</button>
```

**Bước 3:** Thêm mobile sidebar overlay. Đặt TRƯỚC closing `</div>` của wrapper ngoài cùng:
```tsx
{/* Mobile sidebar overlay — CHỈ render trên mobile */}
{isMobileMenuOpen && (
  <>
    {/* Backdrop */}
    <div
      className="fixed inset-0 bg-black/60 z-40 md:hidden"
      onClick={() => setIsMobileMenuOpen(false)}
    />

    {/* Sidebar panel — slide từ trái */}
    <div className="fixed top-0 left-0 h-full w-64 z-50 md:hidden
                    bg-[#0f0f13] border-r border-border-base
                    transform transition-transform duration-200
                    shadow-2xl overflow-y-auto">
      {/* Nút đóng */}
      <div className="flex items-center justify-between p-4 border-b border-border-base">
        <span className="text-sm font-semibold text-tx-primary">Menu</span>
        <button
          onClick={() => setIsMobileMenuOpen(false)}
          className="p-1.5 rounded-lg text-tx-secondary hover:text-tx-primary
                     hover:bg-bg-subtle transition-colors"
        >
          ✕
        </button>
      </div>

      {/* Render nội dung sidebar giống desktop */}
      {/* ⚠️ Copy CHÍNH XÁC nội dung sidebar desktop vào đây */}
      {/* Nếu Sidebar là component riêng: <Sidebar onNavClick={() => setIsMobileMenuOpen(false)} /> */}
      {/* Nếu inline: copy phần nav links, user info từ sidebar desktop */}
    </div>
  </>
)}
```

> **Nếu Sidebar là component riêng (`Sidebar.tsx`):**
> - Truyền prop `onLinkClick?: () => void`
> - Trong Sidebar, gọi `onLinkClick?.()` khi click nav link → đóng mobile menu
> - KHÔNG thay đổi render logic của Sidebar, chỉ thêm prop

**Bước 4:** Đóng mobile menu khi route thay đổi (nếu dùng react-router):
```tsx
// Import useLocation nếu dùng react-router
const location = useLocation();

useEffect(() => {
  setIsMobileMenuOpen(false);
}, [location.pathname]);
```

### Kiểm tra mobile các trang khác

Sau khi fix sidebar, kiểm tra từng trang trên viewport 390px (iPhone size):

**Trang Tổng quan:**
- Các card thống kê có overflow không? → Thêm `overflow-hidden` hoặc giảm padding
- Grid layout: đảm bảo `grid-cols-1` trên mobile, `grid-cols-2` trên tablet

**Trang Thông báo:**
- Tab buttons có bị overflow không? → Đảm bảo `flex-wrap`
- Filter chips có bị tràn? → `flex-wrap gap-2`

**Trang chi tiết con:**
- Header (avatar + tên) có bị cắt? → Stack thành cột trên mobile
- Hai card toggle (Bộ lọc + Kill switch) có hiện đủ?
- Website grid: `grid-cols-1` trên mobile
- Tabs: scroll ngang nếu nhiều tab → `overflow-x-auto`

**General fixes nếu phát hiện vấn đề:**
```tsx
// Grid responsive pattern đang dùng — kiểm tra đúng với pattern hệ thống
className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4"

// Text overflow
className="truncate" hoặc "break-words"

// Flex wrap
className="flex flex-wrap gap-2"
```

---

## Checklist cuối

### Fix 1 — Timezone
- [ ] `normalizeBackendDate` đã sửa: KHÔNG append 'Z'
- [ ] Tất cả chỗ hiển thị thời gian dùng đúng hàm mới
- [ ] `AccessRequestCard` dùng `formatRelativeTime` + tooltip `formatDateTimeVN`
- [ ] Notification list dùng `formatRelativeTime`
- [ ] Test: tạo request mới → hiển thị "vừa xong" → sau 5 phút hiện "5 phút trước"
- [ ] Test: thời gian 11:20 hiển thị đúng 11:20, KHÔNG phải 18:20

### Fix 2 — Reason filter
- [ ] Có 4 chip: Tất cả loại / Tạm dừng Internet / Hết giờ / Web mới
- [ ] Filter hoạt động client-side (không cần API mới)
- [ ] Reset reason filter khi đổi status filter
- [ ] Đặt dưới status filter chips, trên danh sách

### Fix 3 — Collapsible notifications
- [ ] Mặc định hiện 5 thông báo
- [ ] Nút "Xem thêm X thông báo" khi có nhiều hơn 5
- [ ] Nút "Thu gọn" khi đã mở rộng
- [ ] Reset về thu gọn khi đổi filter

### Fix 4 & 5 — blocked.html đơn giản
- [ ] KHÔNG có section giới hạn phút / khung giờ
- [ ] KHÔNG có nút 15p / 30p / 60p
- [ ] Chỉ có context message + 1 nút "Gửi yêu cầu"
- [ ] Context message khác nhau theo reason (internet_paused / time_limit / new_web)
- [ ] Nút text thay đổi theo reason
- [ ] `blocked.js` gửi request với `reason` nhưng KHÔNG gửi time config

### Fix 6 — Auto-reload
- [ ] `startPassivePolling()` chạy ngay khi `blocked.html` load
- [ ] Poll `/check` mỗi 30s (passive) hoặc 10s (sau khi gửi request)
- [ ] Khi `allowed=true` → `window.location.href = "https://${blockedDomain}"`
- [ ] Poll tự dừng sau 40 lần
- [ ] Field name trong `/check` response đã verify từ `ExtensionController.cs`
- [ ] Test end-to-end: bị block → guardian approve → blocked.html tự redirect sau ≤30s

### Fix 7 — Mobile responsive
- [ ] Hamburger button có `onClick={() => setIsMobileMenuOpen(true)}`
- [ ] Mobile sidebar overlay hiện khi click hamburger
- [ ] Backdrop click → đóng sidebar
- [ ] Route change → đóng sidebar
- [ ] Desktop layout KHÔNG thay đổi (sidebar `md:flex` vẫn nguyên)
- [ ] Trang Thông báo: tabs không overflow trên mobile
- [ ] Trang chi tiết con: website grid `grid-cols-1` trên mobile
- [ ] Không có horizontal scroll ẩn trên bất kỳ trang nào

---

## Lưu ý quan trọng

### Verify field name `/check` endpoint
Đây là điều quan trọng nhất cho Fix 6. Đọc `ExtensionController.cs` → `GET /check`:
```csharp
// Tìm return statement — ví dụ:
return Ok(new { allowed = result.IsAllowed }); // → field name: "allowed"
// hoặc
return Ok(new CheckAccessResponse { Allowed = result }); // → field name: "allowed" (camelCase)
```
Sau đó sửa trong `blocked.js`:
```javascript
const isAllowed = data.FIELD_NAME === true; // thay FIELD_NAME bằng thực tế
```

### Sidebar mobile: giữ màu tối
Mobile sidebar dùng `bg-[#0f0f13]` (giống sidebar desktop đang tối). Nếu desktop sidebar dùng class khác, copy đúng class đó.

### blocked.js: TOKEN_KEY
Trước khi deploy, xác nhận key bằng cách đọc background.js:
```javascript
// Tìm dòng như: chrome.storage.local.set({ googleToken: token })
// hoặc: chrome.storage.local.set({ token: token })
// → copy đúng key name vào TOKEN_KEY
```
