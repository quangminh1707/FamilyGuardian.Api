# Family Guardian — Fix Bugs + Screenshot Modal (Phần 12)

> **Ngày tạo:** 2026-05-21
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE_11.md (Phần 11)

---

## ⚠️ Quy tắc bất di bất dịch

| Quy tắc | Chi tiết |
|---------|---------|
| `sp_ExtensionCheckAccess` | Tuyệt đối KHÔNG sửa |
| Block/heartbeat logic trong background.js | KHÔNG thay đổi |
| Logic hiện tại backend/frontend | KHÔNG thay đổi, chỉ bổ sung/sửa bug |
| Dark mode | CSS variables: `bg-bg-surface`, `text-tx-primary`, `border-border-base`, `brand-DEFAULT` |

---

## SQL cần chạy

Không có SQL mới trong phần này.

---

## PHẦN A — Fix lỗi `reason` bị truncate khi con gửi yêu cầu

### A.1 — Nguyên nhân gốc rễ (đã tìm ra)

`background.js` line 193:
```javascript
chrome.runtime.getURL("blocked.html")
  + `?domain=...&reason=${encodeURIComponent(result.reason)}&url=...`
```

`result.reason` = `data.reason` từ `/api/extension/check` endpoint → là string mà SP trả về, có thể không khớp ENUM.

`background.js` line 249 (heartbeat block): gửi chuẩn `"time_limit_exceeded"` → KHÔNG lỗi.

`background.js` line 309: gửi `message.reason` as-is từ URL param → có thể là bất kỳ string nào SP trả về.

**Fix đúng chỗ:** Normalize trong `background.js` trước khi gửi lên API, VÀ cũng normalize trong `AccessRequestService.cs` làm safety net.

### A.2 — Kiểm tra trước khi sửa

Mở `background.js`. Tìm đoạn `REQUEST_ACCESS` handler (khoảng line 293-327). Đây là chỗ duy nhất cần sửa trong extension.

Mở `Services/AccessRequestService.cs`. Tìm dòng 33. Giữ nguyên toàn bộ logic bên dưới dòng đó.

### A.3 — Sửa `background.js` — thêm normalization trong REQUEST_ACCESS handler

Tìm đoạn này trong handler `REQUEST_ACCESS` (KHÔNG thay đổi bất kỳ logic nào khác):

```javascript
// TRƯỚC (line ~306-313):
body: JSON.stringify({
  domain: message.domain,
  fullUrl: message.fullUrl,
  reason: message.reason || "not_in_whitelist",
  requestedDurationMinutes: message.requestedDurationMinutes ?? null,
  requestedStartTime: message.requestedStartTime ?? null,
  requestedEndTime: message.requestedEndTime ?? null
})
```

Sửa thành:

```javascript
// SAU: chỉ thêm normalizeAccessReason(), không thay đổi gì khác
body: JSON.stringify({
  domain: message.domain,
  fullUrl: message.fullUrl,
  reason: normalizeAccessReason(message.reason),
  requestedDurationMinutes: message.requestedDurationMinutes ?? null,
  requestedStartTime: message.requestedStartTime ?? null,
  requestedEndTime: message.requestedEndTime ?? null
})
```

Thêm hàm `normalizeAccessReason` vào cuối file (trước dòng `console.log("Family Guardian Extension initialized")`):

```javascript
// ── Normalize reason về đúng ENUM value ──
function normalizeAccessReason(reason) {
  if (!reason) return "not_in_whitelist";
  const r = reason.toLowerCase().trim();
  if (r === "time_limit_exceeded"
    || r.includes("time_limit")
    || r.includes("timelimit")
    || r.includes("exceeded")) {
    return "time_limit_exceeded";
  }
  if (r === "internet_paused"
    || r.includes("internet_paused")
    || r.includes("internetpaused")
    || r.includes("paused")) {
    return "internet_paused";
  }
  if (r === "outside_time_window"
    || r.includes("outside_time")
    || r.includes("time_window")
    || r.includes("timewindow")
    || r.includes("outside_window")) {
    return "outside_time_window";
  }
  return "not_in_whitelist";
}
```

### A.4 — Sửa `AccessRequestService.cs` — safety net (giữ nguyên TOÀN BỘ logic cũ)

Mở file. Tìm dòng 33:
```csharp
reason = string.IsNullOrWhiteSpace(reason) ? "not_in_whitelist" : reason.Trim().ToLowerInvariant();
```

Thêm NGAY SAU dòng đó (KHÔNG xóa dòng trên, KHÔNG thay đổi bất kỳ dòng nào khác):

```csharp
// Safety net: map về valid ENUM value
reason = reason switch
{
    "time_limit_exceeded"                          => "time_limit_exceeded",
    var r when r.Contains("time_limit")
            || r.Contains("exceeded")
            || r.Contains("timelimit")             => "time_limit_exceeded",
    "internet_paused"                              => "internet_paused",
    var r when r.Contains("internet_paused")
            || r.Contains("paused")                => "internet_paused",
    "outside_time_window"                          => "outside_time_window",
    var r when r.Contains("outside_time")
            || r.Contains("time_window")
            || r.Contains("timewindow")            => "outside_time_window",
    _                                              => "not_in_whitelist"
};
```

> ⚠️ Toàn bộ logic bên dưới (title switch, message switch, notification, SignalR...) giữ nguyên 100%.

### A.5 — SQL cần chạy

```sql
-- Đảm bảo ENUM đúng (chạy nếu chưa chạy, bỏ qua nếu đã chạy rồi)
ALTER TABLE access_requests
MODIFY COLUMN reason ENUM('not_in_whitelist','time_limit_exceeded','internet_paused','outside_time_window')
NOT NULL DEFAULT 'not_in_whitelist';
```

---

## PHẦN B — Fix Screenshot mãi "Đang chụp..." + Đổi sang Modal

### B.1 — Nguyên nhân "Đang chụp..." không hết

`background.js` lines 331-344 hiện dùng **mock SignalR** (`connection.on = () => console.warn(...)`).
Extension là Service Worker — không duy trì được WebSocket liên tục.
→ Event `CaptureScreenshot` không bao giờ được nhận → ảnh mãi `pending`.

**Giải pháp:** Đổi Extension sang **polling** (hỏi backend mỗi 5 giây có pending screenshot không). Giữ nguyên toàn bộ logic block/heartbeat/ping hiện tại.

### B.2 — Sửa `background.js` — thêm polling thay mock SignalR

#### B.2.1 — Xóa đoạn mock (lines 329-344 hiện tại)

Tìm và XÓA đoạn sau (đây là code mock không hoạt động, đã được thêm sai trong guide 11):

```javascript
// ── THÊM MỚI: Screenshot ──
// Vì background.js hiện tại chưa khởi tạo SignalR connection, chúng ta thêm biến mock để tránh crash.
if (typeof connection === 'undefined') {
  globalThis.connection = { on: () => console.warn("[FamilyGuardian] SignalR connection is not defined in background.js yet.") };
}

connection.on("CaptureScreenshot", async (payload) => {
  const { screenshotId, domain } = payload;
  console.log("[FamilyGuardian] CaptureScreenshot:", { screenshotId, domain });
  try {
    await captureScreenshotForDomain(screenshotId, domain);
  } catch (err) {
    console.error("[FamilyGuardian] Screenshot error:", err);
    reportScreenshotResult(screenshotId, "failed", String(err.message || err)).catch(() => {});
  }
});
```

#### B.2.2 — Thêm alarm polling (CHỈ thêm, KHÔNG đụng alarm heartbeat/ping)

Tìm đoạn khai báo alarms hiện tại:
```javascript
chrome.alarms.create("heartbeat", { periodInMinutes: ... });
chrome.alarms.create("ping",      { periodInMinutes: 1/6 });
```

Thêm alarm mới ngay bên dưới:
```javascript
chrome.alarms.create("screenshot_poll", { periodInMinutes: 1/12 }); // ~5 giây
```

#### B.2.3 — Thêm xử lý alarm screenshot_poll

Tìm cuối `chrome.alarms.onAlarm.addListener` handler. Thêm case mới (KHÔNG thay đổi case heartbeat và ping):

```javascript
  // ── Screenshot Poll (5s) ─────────────────────────────────
  if (alarm.name === "screenshot_poll") {
    const token = await getGoogleToken();
    if (!token) return;
    try {
      const res = await fetch(`${CONFIG.API_BASE}/pending-screenshots`, {
        headers: { Authorization: `Bearer ${token}` }
      });
      if (!res.ok) return;
      const list = await res.json(); // [{screenshotId, domain}]
      for (const item of list) {
        // fire-and-forget từng cái — không block
        captureScreenshotForDomain(item.screenshotId, item.domain)
          .catch(err => {
            reportScreenshotResult(item.screenshotId, "failed", String(err)).catch(() => {});
          });
      }
    } catch (e) {
      console.error("[FamilyGuardian] screenshot_poll error:", e);
    }
  }
```

> ⚠️ 3 hàm helper `captureScreenshotForDomain`, `uploadScreenshot`, `reportScreenshotResult` đã có sẵn từ guide 11 — KHÔNG thêm lại.

### B.3 — Backend: Thêm endpoint `GET /api/extension/pending-screenshots`

Mở `ExtensionController.cs`. Thêm endpoint mới (KHÔNG thay đổi gì cũ):

> ⚠️ Kiểm tra cách lấy child từ Google token hiện tại — dùng đúng pattern đó.

```csharp
// GET /api/extension/pending-screenshots
// Extension polling mỗi 5s để nhận lệnh chụp ảnh
[HttpGet("pending-screenshots")]
public async Task<IActionResult> GetPendingScreenshots()
{
    var child = await GetCurrentChildAsync(); // thay bằng cách project đang dùng
    if (child == null) return Unauthorized();

    var pending = await _context.WebsiteScreenshots
        .AsNoTracking()
        .Where(s => s.ChildId == child.Id && s.Status == "pending")
        // Chỉ lấy pending trong vòng 2 phút (tránh cũ)
        .Where(s => s.CapturedAt >= DateTime.Now.AddMinutes(-2))
        .Select(s => new { screenshotId = s.Id, domain = s.Domain })
        .ToListAsync();

    return Ok(pending);
}
```

---

## PHẦN C — Giao diện Screenshot: Bỏ inline → Dùng Modal

### C.1 — Mục tiêu

| Bỏ | Thêm |
|----|------|
| Phần hiển thị ảnh inline bên trong WebsiteCard | Modal xem chi tiết ảnh + timestamp |
| Poll 5s trong WebsiteCard query | Nút 📷 mở modal |
| ScreenshotItem component inline | Filter theo thời gian trong modal |
| | Auto-load khi nhận SignalR ScreenshotReady |

### C.2 — Kiểm tra code hiện tại

Mở `WebsiteCard.tsx`. Xác định:
- Đoạn `showScreenshots` state
- Đoạn `useQuery(['screenshots', ...])`
- Đoạn `requestScreenshotMutation`
- Phần JSX `{showScreenshots && (...)}` — sẽ XÓA
- Phần JSX modal xem full size `{selectedImageUrl && (...)}` — sẽ CHUYỂN vào modal mới
- Component `ScreenshotItem` — sẽ DÙNG LẠI trong modal

### C.3 — Sửa `WebsiteCard.tsx`

#### C.3.1 — Giữ nguyên, chỉ thay đổi liên quan screenshot

State giữ lại:
```typescript
// GIỮ:
const requestScreenshotMutation = useMutation({...}); // giữ nguyên

// XÓA:
// const [showScreenshots, setShowScreenshots] = useState(false);
// const [selectedImageUrl, setSelectedImageUrl] = useState<string | null>(null);
// const { data: screenshots, isLoading: screenshotsLoading } = useQuery({...});

// THÊM:
const [screenshotModalOpen, setScreenshotModalOpen] = useState(false);
```

#### C.3.2 — Sửa nút 📷 trong action buttons

Giữ nút chụp ảnh (KHÔNG thay đổi):
```tsx
{/* Nút chụp ảnh — giữ nguyên */}
<button
  onClick={() => requestScreenshotMutation.mutate()}
  disabled={requestScreenshotMutation.isPending}
  title="Chụp ảnh màn hình"
  className="p-1.5 rounded-md text-tx-secondary hover:text-brand-DEFAULT
             hover:bg-brand-DEFAULT/10 transition-colors disabled:opacity-50"
>
  {/* icon giữ nguyên */}
</button>
```

Đổi nút xem ảnh — thay `setShowScreenshots` thành mở modal:
```tsx
{/* Nút xem ảnh đã chụp */}
<button
  onClick={() => setScreenshotModalOpen(true)}
  title="Xem ảnh đã chụp"
  className="p-1.5 rounded-md text-tx-secondary hover:text-brand-DEFAULT
             hover:bg-brand-DEFAULT/10 transition-colors"
>
  <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
      d="M4 16l4.586-4.586a2 2 0 012.828 0L16 16m-2-2l1.586-1.586a2 2 0 012.828 0L20 14m-6-6h.01M6 20h12a2 2 0 002-2V6a2 2 0 00-2-2H6a2 2 0 00-2 2v12a2 2 0 002 2z"/>
  </svg>
</button>
```

#### C.3.3 — XÓA phần hiển thị inline

Tìm và XÓA toàn bộ đoạn JSX sau (bên trong return của component):
```tsx
{/* XÓA TOÀN BỘ ĐOẠN NÀY */}
{showScreenshots && (
  <div className="mt-3 pt-3 border-t border-border-base">
    ...
  </div>
)}

{/* XÓA: modal selectedImageUrl cũ */}
{selectedImageUrl && (
  <div className="fixed inset-0 z-50 ...">
    ...
  </div>
)}
```

#### C.3.4 — Thêm modal mới

Thêm vào cuối return (trước dấu đóng fragment), TRƯỚC khi đóng component:

```tsx
{/* Screenshot Modal */}
{screenshotModalOpen && (
  <ScreenshotModal
    childId={childId}
    domain={website.domain}
    websiteName={website.displayName || website.domain}
    onClose={() => setScreenshotModalOpen(false)}
  />
)}
```

### C.4 — Tạo `components/ScreenshotModal.tsx`

> ⚠️ Kiểm tra import paths đang dùng trong project (alias `@/`, relative path...)
> ⚠️ Kiểm tra `toast` import từ đâu — dùng đúng cách project đang dùng
> ⚠️ Kiểm tra `api` axios instance tên gì

```tsx
import { useState, useEffect } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { requestScreenshot, getScreenshots, type ScreenshotDto } from '@/api/childrenApi';
// import toast từ đúng chỗ project đang dùng

interface ScreenshotModalProps {
  childId: number;
  domain: string;
  websiteName: string;
  onClose: () => void;
}

type TimeFilter = 'all' | 'today' | 'week' | 'month';

export default function ScreenshotModal({
  childId, domain, websiteName, onClose
}: ScreenshotModalProps) {
  const queryClient = useQueryClient();
  const [selectedImage, setSelectedImage] = useState<ScreenshotDto | null>(null);
  const [timeFilter, setTimeFilter] = useState<TimeFilter>('all');

  const { data: screenshots = [], isLoading, refetch } = useQuery({
    queryKey: ['screenshots', childId, domain, 'modal'],
    queryFn: () => getScreenshots(childId, domain, 50), // lấy nhiều hơn trong modal
    refetchInterval: 5000, // auto-refresh 5s
  });

  // Filter theo thời gian
  const filtered = screenshots.filter(s => {
    if (timeFilter === 'all') return true;
    const d = new Date(s.capturedAt);
    const now = new Date();
    if (timeFilter === 'today') {
      return d.toDateString() === now.toDateString();
    }
    if (timeFilter === 'week') {
      const weekAgo = new Date(now.getTime() - 7 * 24 * 60 * 60 * 1000);
      return d >= weekAgo;
    }
    if (timeFilter === 'month') {
      const monthAgo = new Date(now.getTime() - 30 * 24 * 60 * 60 * 1000);
      return d >= monthAgo;
    }
    return true;
  });

  const capturedList = filtered.filter(s => s.status === 'captured');
  const pendingList  = filtered.filter(s => s.status === 'pending');
  const failedList   = filtered.filter(s => s.status !== 'captured' && s.status !== 'pending');

  const requestMutation = useMutation({
    mutationFn: () => requestScreenshot(childId, domain),
    onSuccess: () => {
      toast.success('Đã gửi yêu cầu chụp ảnh');
      setTimeout(() => refetch(), 6000); // refetch sau 6s
    },
    onError: () => toast.error('Không thể gửi yêu cầu'),
  });

  // Đóng modal khi nhấn Escape
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        if (selectedImage) setSelectedImage(null);
        else onClose();
      }
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [selectedImage, onClose]);

  return (
    <>
      {/* Overlay */}
      <div
        className="fixed inset-0 z-40 bg-black/60 backdrop-blur-sm"
        onClick={onClose}
      />

      {/* Modal */}
      <div className="fixed inset-0 z-50 flex items-center justify-center p-4 pointer-events-none">
        <div
          className="relative w-full max-w-3xl max-h-[90vh] rounded-2xl overflow-hidden
                     bg-bg-surface border border-border-base shadow-2xl
                     flex flex-col pointer-events-auto"
          onClick={e => e.stopPropagation()}
        >
          {/* Header */}
          <div className="flex items-center justify-between px-6 py-4
                          border-b border-border-base shrink-0">
            <div>
              <h2 className="text-base font-semibold text-tx-primary">
                📷 Ảnh chụp màn hình
              </h2>
              <p className="text-xs text-tx-secondary mt-0.5">{websiteName}</p>
            </div>
            <div className="flex items-center gap-2">
              {/* Nút chụp ngay */}
              <button
                onClick={() => requestMutation.mutate()}
                disabled={requestMutation.isPending}
                className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-medium
                           bg-brand-DEFAULT/10 text-brand-DEFAULT border border-brand-DEFAULT/20
                           hover:bg-brand-DEFAULT/20 transition-colors disabled:opacity-50"
              >
                {requestMutation.isPending ? (
                  <svg className="w-3.5 h-3.5 animate-spin" fill="none" viewBox="0 0 24 24">
                    <circle className="opacity-25" cx="12" cy="12" r="10"
                            stroke="currentColor" strokeWidth="4"/>
                    <path className="opacity-75" fill="currentColor"
                          d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"/>
                  </svg>
                ) : (
                  <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                      d="M3 9a2 2 0 012-2h.93a2 2 0 001.664-.89l.812-1.22A2 2 0 0110.07 4h3.86a2 2 0 011.664.89l.812 1.22A2 2 0 0018.07 7H19a2 2 0 012 2v9a2 2 0 01-2 2H5a2 2 0 01-2-2V9z"/>
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                          d="M15 13a3 3 0 11-6 0 3 3 0 016 0z"/>
                  </svg>
                )}
                Yêu cầu chụp ngay
              </button>
              {/* Nút đóng */}
              <button
                onClick={onClose}
                className="p-1.5 rounded-lg text-tx-secondary hover:text-tx-primary
                           hover:bg-bg-subtle transition-colors"
              >
                <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                        d="M6 18L18 6M6 6l12 12"/>
                </svg>
              </button>
            </div>
          </div>

          {/* Filter bar */}
          <div className="flex items-center gap-2 px-6 py-3 border-b border-border-base shrink-0">
            <svg className="w-4 h-4 text-tx-secondary shrink-0" fill="none"
                 viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                d="M3 4a1 1 0 011-1h16a1 1 0 011 1v2a1 1 0 01-.293.707L13 13.414V19a1 1 0 01-.553.894l-4 2A1 1 0 017 21v-7.586L3.293 6.707A1 1 0 013 6V4z"/>
            </svg>
            <span className="text-xs text-tx-secondary">Lọc:</span>
            {(['all', 'today', 'week', 'month'] as TimeFilter[]).map(f => (
              <button
                key={f}
                onClick={() => setTimeFilter(f)}
                className={`px-3 py-1 rounded-full text-xs font-medium transition-colors
                  ${timeFilter === f
                    ? 'bg-brand-DEFAULT text-white'
                    : 'bg-bg-subtle text-tx-secondary hover:text-tx-primary hover:bg-bg-muted'
                  }`}
              >
                {{ all: 'Tất cả', today: 'Hôm nay', week: '7 ngày', month: '30 ngày' }[f]}
              </button>
            ))}
            <span className="ml-auto text-xs text-tx-secondary">
              {capturedList.length} ảnh
              {pendingList.length > 0 && (
                <span className="ml-1 text-yellow-500">· {pendingList.length} đang chụp</span>
              )}
            </span>
          </div>

          {/* Nội dung */}
          <div className="flex-1 overflow-y-auto p-6">
            {isLoading && (
              <div className="flex items-center justify-center py-12">
                <svg className="w-6 h-6 animate-spin text-brand-DEFAULT" fill="none" viewBox="0 0 24 24">
                  <circle className="opacity-25" cx="12" cy="12" r="10"
                          stroke="currentColor" strokeWidth="4"/>
                  <path className="opacity-75" fill="currentColor"
                        d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"/>
                </svg>
              </div>
            )}

            {!isLoading && filtered.length === 0 && (
              <div className="text-center py-12">
                <div className="text-4xl mb-3">📷</div>
                <p className="text-sm text-tx-secondary">Chưa có ảnh nào trong khoảng thời gian này</p>
                <p className="text-xs text-tx-secondary mt-1">Nhấn "Yêu cầu chụp ngay" để chụp ảnh màn hình của con</p>
              </div>
            )}

            {/* Pending */}
            {pendingList.length > 0 && (
              <div className="mb-4">
                <p className="text-xs font-medium text-tx-secondary uppercase tracking-wide mb-2">
                  Đang xử lý
                </p>
                <div className="space-y-2">
                  {pendingList.map(s => (
                    <div key={s.id}
                         className="flex items-center gap-3 p-3 rounded-lg
                                    bg-bg-subtle border border-border-base">
                      <svg className="w-4 h-4 animate-spin text-brand-DEFAULT shrink-0"
                           fill="none" viewBox="0 0 24 24">
                        <circle className="opacity-25" cx="12" cy="12" r="10"
                                stroke="currentColor" strokeWidth="4"/>
                        <path className="opacity-75" fill="currentColor"
                              d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"/>
                      </svg>
                      <div>
                        <p className="text-xs text-tx-primary">Đang chụp màn hình...</p>
                        <p className="text-xs text-tx-secondary">
                          {new Date(s.capturedAt).toLocaleString('vi-VN')}
                        </p>
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            )}

            {/* Failed / tab not found */}
            {failedList.length > 0 && (
              <div className="mb-4">
                <p className="text-xs font-medium text-tx-secondary uppercase tracking-wide mb-2">
                  Không thành công
                </p>
                <div className="space-y-2">
                  {failedList.map(s => (
                    <div key={s.id}
                         className={`flex items-center gap-3 p-3 rounded-lg border
                           ${s.status === 'tab_not_found'
                             ? 'bg-yellow-500/8 border-yellow-500/20'
                             : 'bg-red-500/8 border-red-500/20'
                           }`}>
                      <span className="text-lg shrink-0">
                        {s.status === 'tab_not_found' ? '⚠️' : '❌'}
                      </span>
                      <div>
                        <p className={`text-xs font-medium
                          ${s.status === 'tab_not_found'
                            ? 'text-yellow-600 dark:text-yellow-400'
                            : 'text-red-600 dark:text-red-400'
                          }`}>
                          {s.status === 'tab_not_found'
                            ? 'Con chưa mở website này'
                            : 'Chụp thất bại'}
                        </p>
                        <p className="text-xs text-tx-secondary">
                          {new Date(s.capturedAt).toLocaleString('vi-VN')}
                        </p>
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            )}

            {/* Captured — lưới ảnh */}
            {capturedList.length > 0 && (
              <div>
                <p className="text-xs font-medium text-tx-secondary uppercase tracking-wide mb-3">
                  Ảnh đã chụp ({capturedList.length})
                </p>
                <div className="grid grid-cols-2 sm:grid-cols-3 gap-3">
                  {capturedList.map(s => (
                    <div
                      key={s.id}
                      onClick={() => setSelectedImage(s)}
                      className="relative rounded-xl overflow-hidden border border-border-base
                                 cursor-pointer group hover:border-brand-DEFAULT/50
                                 transition-all hover:shadow-lg aspect-video"
                    >
                      <img
                        src={s.imageUrl!}
                        alt="Screenshot"
                        className="w-full h-full object-cover object-top
                                   group-hover:scale-105 transition-transform duration-300"
                      />
                      {/* Gradient + timestamp */}
                      <div className="absolute inset-0 bg-gradient-to-t from-black/70
                                      via-transparent to-transparent opacity-0
                                      group-hover:opacity-100 transition-opacity
                                      flex items-end p-2">
                        <p className="text-xs text-white font-medium">
                          {new Date(s.capturedAt).toLocaleString('vi-VN')}
                        </p>
                      </div>
                      {/* Always-visible timestamp bar */}
                      <div className="absolute bottom-0 left-0 right-0
                                      bg-gradient-to-t from-black/60 to-transparent
                                      px-2 py-1.5">
                        <p className="text-xs text-white/90">
                          {new Date(s.capturedAt).toLocaleString('vi-VN', {
                            hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit'
                          })}
                        </p>
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            )}
          </div>
        </div>
      </div>

      {/* Lightbox xem ảnh full size */}
      {selectedImage && (
        <div
          className="fixed inset-0 z-[60] flex items-center justify-center
                     bg-black/90 backdrop-blur-sm p-4"
          onClick={() => setSelectedImage(null)}
        >
          <div
            className="relative max-w-5xl max-h-[95vh] rounded-2xl overflow-hidden shadow-2xl"
            onClick={e => e.stopPropagation()}
          >
            <img
              src={selectedImage.imageUrl!}
              alt="Screenshot"
              className="max-w-full max-h-[95vh] object-contain"
            />
            {/* Info bar */}
            <div className="absolute bottom-0 left-0 right-0
                            bg-gradient-to-t from-black/80 to-transparent
                            px-4 py-3 flex items-center justify-between">
              <p className="text-sm text-white font-medium">
                📷 {new Date(selectedImage.capturedAt).toLocaleString('vi-VN')}
              </p>
              <a
                href={selectedImage.imageUrl!}
                download={`screenshot_${selectedImage.id}.jpg`}
                onClick={e => e.stopPropagation()}
                className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg
                           bg-white/10 hover:bg-white/20 text-white text-xs
                           font-medium transition-colors border border-white/20"
              >
                <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                    d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4"/>
                </svg>
                Tải xuống
              </a>
            </div>
            {/* Nút đóng */}
            <button
              onClick={() => setSelectedImage(null)}
              className="absolute top-3 right-3 p-2 rounded-full
                         bg-black/60 text-white hover:bg-black/80 transition-colors"
            >
              <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                      d="M6 18L18 6M6 6l12 12"/>
              </svg>
            </button>
          </div>
        </div>
      )}
    </>
  );
}
```

### C.5 — Sửa SignalR hook — cập nhật invalidate key

Mở hook SignalR Guardian (`useSignalR.ts` hoặc `useExtensionMonitor.ts`).
Tìm listener `ScreenshotReady`. Cập nhật queryKey cho khớp key modal mới:

```typescript
connection.on("ScreenshotReady", (data: {...}) => {
  // Invalidate cả 2 key (card cũ + modal mới)
  queryClient.invalidateQueries({
    queryKey: ['screenshots', data.childId, data.domain]
  });
  // ...toast logic giữ nguyên
});
```

---

## Thứ tự làm việc

```
A1  — Sửa background.js: thêm normalizeAccessReason() + sửa REQUEST_ACCESS handler
A2  — Sửa AccessRequestService.cs: thêm safety net mapping sau dòng 33
A3  — Chạy SQL ENUM (nếu chưa chạy)
A4  — Test: gửi yêu cầu từ blocked page → không crash → reason đúng

B1  — Sửa background.js: XÓA mock SignalR (lines 331-344)
B2  — Sửa background.js: thêm alarm screenshot_poll + xử lý trong onAlarm
B3  — Backend: thêm endpoint GET /api/extension/pending-screenshots vào ExtensionController
B4  — node build-config.js → reload extension
B5  — Test: guardian chụp → extension poll 5s → ảnh xuất hiện

C1  — Frontend: Sửa WebsiteCard.tsx (xóa inline, thêm modal state)
C2  — Frontend: Tạo components/ScreenshotModal.tsx
C3  — Frontend: Cập nhật SignalR hook (queryKey)
C4  — Test: click 📷 → modal mở → filter hoạt động → click ảnh → lightbox
```

---

## Checklist kiểm tra trước khi sửa

### Extension
- [ ] Đọc toàn bộ background.js trước khi sửa
- [ ] Xác định chính xác dòng của REQUEST_ACCESS handler body JSON
- [ ] Xác định chính xác lines 331-344 (đoạn mock cần xóa)
- [ ] Xác định dòng cuối của `chrome.alarms.onAlarm.addListener` handler (thêm case trước dấu đóng `}`)
- [ ] Sau khi sửa: chạy `node build-config.js` → reload extension trong chrome://extensions

### Backend
- [ ] Kiểm tra cách lấy child từ Google token trong ExtensionController hiện tại
- [ ] `WebsiteScreenshots` DbSet đã có trong AppDbContext chưa (từ Guide 11)
- [ ] Endpoint `/pending-screenshots` dùng đúng pattern auth như các endpoint extension khác

### Frontend
- [ ] Import paths đúng (alias `@/` hay relative)
- [ ] `toast` import từ đúng store của project
- [ ] `api` axios instance tên đúng trong `childrenApi.ts`
- [ ] `WebsiteCard` bỏ đúng state/query/JSX cũ, không bỏ nhầm các logic khác
- [ ] `ScreenshotModal` được export default và import đúng trong `WebsiteCard`
- [ ] Dark mode: tất cả class dùng CSS variables, không hardcode màu

---

## Test

```
TEST A — Fix reason
1. Con vào trang ngoài whitelist → bị chặn → nhấn "Gửi yêu cầu"
2. Guardian nhận thông báo với reason đúng (không phải mặc định not_in_whitelist)
3. Con vào trang đã hết giờ → bị chặn → gửi yêu cầu → guardian nhận reason "time_limit_exceeded"
4. Con vào trang ngoài khung giờ → gửi yêu cầu → guardian nhận reason "outside_time_window"

TEST B — Screenshot polling
1. Guardian nhấn 📷 → backend tạo pending record
2. Chờ ≤5 giây → extension poll → tìm thấy pending → chụp → upload
3. Modal hiện ảnh mới, không còn "Đang chụp..." mãi

TEST C — Modal
1. Click icon ảnh trên WebsiteCard → modal mở
2. Filter "Hôm nay" → chỉ hiện ảnh hôm nay
3. Click thumbnail → lightbox full size với timestamp
4. Nút "Tải xuống" → download ảnh
5. Nhấn Escape → đóng lightbox → nhấn Escape lần nữa → đóng modal
6. Dark mode: modal hiện đúng màu CSS variables
```
