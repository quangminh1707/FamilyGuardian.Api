# Family Guardian — Hướng dẫn triển khai Fix & Feature (Phần 2)

> **Ngày tạo:** 2026-05-10  
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE.md (Phần 1)

---

## ⚠️ Quy tắc bất di bất dịch — KHÔNG ĐƯỢC VI PHẠM

| Quy tắc | Chi tiết |
|---------|---------|
| `sp_ExtensionCheckAccess` | Tuyệt đối KHÔNG sửa |
| Extension background.js | KHÔNG thay đổi logic đang chạy. Chỉ được THÊM mới |
| DateTime | Dùng `DateTime.Now`, KHÔNG `DateTime.UtcNow` |
| Notification query | Luôn theo `guardian_id`, KHÔNG `child_id` |
| Logic hiện tại | KHÔNG thay đổi logic đang hoạt động |

---

## 📦 Trạng thái SQL

-- Thêm reason + time config vào access_requests
ALTER TABLE access_requests
  ADD COLUMN reason ENUM('not_in_whitelist','time_limit_exceeded','internet_paused')
    NOT NULL DEFAULT 'not_in_whitelist' AFTER domain,
  ADD COLUMN requested_duration_minutes INT NULL AFTER reason,
  ADD COLUMN requested_start_time TIME NULL AFTER requested_duration_minutes,
  ADD COLUMN requested_end_time TIME NULL AFTER requested_start_time;

-- Thêm bonus_seconds để guardian gia hạn thời gian cho con
ALTER TABLE daily_usage_stats
  ADD COLUMN bonus_seconds INT NOT NULL DEFAULT 0 AFTER total_seconds;

> ✅ **SQL đã được chạy xong.** Các thay đổi DB đã có:
> - Cột mới trong `access_requests`: `reason ENUM(not_in_whitelist, time_limit_exceeded, internet_paused)`, `requested_duration_minutes INT NULL`, `requested_start_time TIME NULL`, `requested_end_time TIME NULL`
> - Cột mới trong `daily_usage_stats`: `bonus_seconds INT DEFAULT 0`

---

## Tổng quan 8 fix/feature

| # | Nội dung | Backend | Frontend | Extension |
|---|---------|---------|---------|---------|
| 1 | Fix timezone hiển thị giờ yêu cầu | ❌ | ✅ | ❌ |
| 2 | Redesign request card trong notification | ❌ | ✅ | ❌ |
| 3 | Trang thông báo: tab Yêu cầu / Thông báo + filter | ✅ | ✅ | ❌ |
| 4 | Kill switch: làm mờ toàn bộ child profile + blocked.html biết lý do | ✅ endpoint mới | ✅ | ✅ blocked.js |
| 5 | Request phân biệt lý do (internet_paused / time_limit / new_web) | ✅ | ✅ | ✅ blocked.js |
| 6 | Form yêu cầu có cấu hình giờ + guardian approve có time config | ✅ | ✅ | ✅ blocked.html |
| 7 | Kill switch làm mờ child profile (cùng fix 4) | ❌ | ✅ | ❌ |
| 8 | Auto-reload blocked.html sau khi guardian approve | ✅ SignalR event | ❌ | ✅ blocked.js polling |

---

## Fix 1: Timezone — Hiển thị đúng giờ con gửi yêu cầu

### Vấn đề
Backend lưu `DateTime.Now` không có 'Z' → JavaScript parse sai múi giờ → hiển thị 23:27 thay vì 16:27.

### Phạm vi
**Chỉ Frontend.** Không cần sửa backend, không cần sửa extension.

### Kiểm tra trước
- Đọc `src/lib/formatters.ts` — xem các hàm format thời gian đang có
- Xem hàm nào đang được dùng trong notification page
- Xem `AccessRequestCard.tsx` (vừa tạo ở Phần 1) — đã có đoạn normalize 'Z' chưa

### Sửa `src/lib/formatters.ts`

Thêm hàm mới vào formatters (KHÔNG sửa các hàm đang có):

```typescript
/**
 * Normalize datetime string từ backend (không có 'Z') sang UTC chuẩn
 * Backend dùng DateTime.Now → không có timezone suffix → JS hiểu sai
 */
export function normalizeBackendDate(dateStr: string): Date {
  const normalized =
    dateStr.endsWith('Z') || /[+-]\d{2}:\d{2}$/.test(dateStr)
      ? dateStr
      : dateStr + 'Z';
  return new Date(normalized);
}

/**
 * Format giờ theo định dạng 24h rõ ràng: "16:27" hoặc "08:05"
 * Dùng cho yêu cầu truy cập, thông báo
 */
export function formatTimeVN(dateStr: string): string {
  const date = normalizeBackendDate(dateStr);
  return date.toLocaleTimeString('vi-VN', {
    hour: '2-digit',
    minute: '2-digit',
    hour12: false, // 24h: 16:27 thay vì 4:27 PM
  });
}

/**
 * Format ngày + giờ đầy đủ: "10/05 16:27"
 */
export function formatDateTimeVN(dateStr: string): string {
  const date = normalizeBackendDate(dateStr);
  const now = new Date();
  const isToday =
    date.getDate() === now.getDate() &&
    date.getMonth() === now.getMonth() &&
    date.getFullYear() === now.getFullYear();

  if (isToday) {
    return `Hôm nay ${formatTimeVN(dateStr)}`;
  }

  const yesterday = new Date(now);
  yesterday.setDate(now.getDate() - 1);
  const isYesterday =
    date.getDate() === yesterday.getDate() &&
    date.getMonth() === yesterday.getMonth();

  if (isYesterday) {
    return `Hôm qua ${formatTimeVN(dateStr)}`;
  }

  return date.toLocaleDateString('vi-VN', {
    day: '2-digit',
    month: '2-digit',
  }) + ' ' + formatTimeVN(dateStr);
}

/**
 * Format thời gian tương đối: "vừa xong", "5 phút trước", "2 giờ trước"
 */
export function formatRelativeTime(dateStr: string): string {
  const date = normalizeBackendDate(dateStr);
  const diffMs = Date.now() - date.getTime();
  const diffMin = Math.floor(diffMs / 60000);

  if (diffMin < 1) return 'vừa xong';
  if (diffMin < 60) return `${diffMin} phút trước`;
  const diffH = Math.floor(diffMin / 60);
  if (diffH < 24) return `${diffH} giờ trước`;
  const diffD = Math.floor(diffH / 24);
  return `${diffD} ngày trước`;
}
```

### Áp dụng
Tìm TẤT CẢ chỗ trong code đang format `requestedAt`, `createdAt`, `created_at` từ backend và thay bằng `formatDateTimeVN()` hoặc `formatTimeVN()`.

---

## Fix 2 & 3: Trang Thông Báo — Redesign UI + Tabs/Filter

### Tổng quan
Trang thông báo hiện tại gộp chung tất cả. Cần chia thành 2 tab rõ ràng:
- **Tab "Yêu cầu"**: danh sách `access_requests` — filter: Chờ duyệt / Đã xử lý
- **Tab "Thông báo"**: danh sách `notifications` — filter: Chưa đọc / Đã đọc

### Kiểm tra Backend trước

#### Đọc `AccessRequestsController.cs`
- Endpoint `GET /api/access-requests` hiện chỉ trả `status=pending`
- Cần sửa để hỗ trợ query param `?status=all|pending|handled`
- KHÔNG sửa PATCH endpoint

#### Đọc `NotificationsController.cs`
- Xem endpoint `GET /api/notifications` — có hỗ trợ filter `?read=true|false` chưa?
- Xem cấu trúc response: có `isRead`, `type`, `createdAt`?
- Nếu chưa hỗ trợ filter → thêm query param

### Sửa Backend — `AccessRequestsController.cs`

Thêm `AccessRequestDto` các field mới (đọc DTO hiện tại, chỉ thêm field):
```csharp
public class AccessRequestDto
{
    // ... các field đang có ...
    public string Reason { get; set; } = "not_in_whitelist"; // thêm mới
    public int? RequestedDurationMinutes { get; set; }       // thêm mới
    public string? RequestedStartTime { get; set; }          // thêm mới "HH:mm"
    public string? RequestedEndTime { get; set; }            // thêm mới "HH:mm"
}
```

Sửa `GetPendingRequestsAsync` → đổi tên thành `GetRequestsAsync` và thêm filter:
```csharp
// Trong IAccessRequestService — thêm overload
Task<List<AccessRequestDto>> GetRequestsAsync(int guardianId, string statusFilter = "pending");
```

Implementation:
```csharp
public async Task<List<AccessRequestDto>> GetRequestsAsync(int guardianId, string statusFilter = "pending")
{
    var query = _context.AccessRequests
        .Include(r => r.Child)
        .Where(r => r.GuardianId == guardianId);

    if (statusFilter == "pending")
        query = query.Where(r => r.Status == "pending");
    else if (statusFilter == "handled")
        query = query.Where(r => r.Status != "pending");
    // "all" → không filter

    return await query
        .OrderByDescending(r => r.RequestedAt)
        .Select(r => new AccessRequestDto
        {
            Id = r.Id,
            ChildId = r.ChildId,
            ChildName = r.Child.FullName,
            ChildAvatarUrl = r.Child.AvatarUrl,
            Domain = r.Domain,
            FullUrl = r.FullUrl,
            Status = r.Status,
            Reason = r.Reason,
            RequestedDurationMinutes = r.RequestedDurationMinutes,
            RequestedStartTime = r.RequestedStartTime.HasValue
                ? r.RequestedStartTime.Value.ToString(@"HH\:mm") : null,
            RequestedEndTime = r.RequestedEndTime.HasValue
                ? r.RequestedEndTime.Value.ToString(@"HH\:mm") : null,
            RequestedAt = r.RequestedAt,
            TempExpiresAt = r.TempExpiresAt,
        })
        .ToListAsync();
}
```

Sửa endpoint GET (KHÔNG sửa PATCH):
```csharp
[HttpGet]
public async Task<IActionResult> GetRequests([FromQuery] string status = "pending")
{
    var guardianId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    var requests = await _service.GetRequestsAsync(guardianId, status);
    return Ok(requests);
}
```

### Kiểm tra Frontend trước

- Đọc trang Notifications hiện tại (tìm file trong `src/pages/` — có thể là `NotificationsPage.tsx`)
- Xem component notification card đang dùng: icon, layout, màu sắc
- Đọc `notificationStore.ts` — xem state đang có
- Đọc `src/api/` file notifications — xem functions đang có

### Sửa Frontend — `NotificationsPage.tsx`

> ⚠️ Đọc toàn bộ file hiện tại trước. Giữ nguyên logic, chỉ thêm tabs/filter và redesign card.

Thêm state quản lý tabs và filter:
```tsx
const [activeTab, setActiveTab] = useState<'requests' | 'notifications'>('requests');
const [requestFilter, setRequestFilter] = useState<'pending' | 'handled' | 'all'>('pending');
const [notifFilter, setNotifFilter] = useState<'all' | 'unread' | 'read'>('all');
```

Query access requests với filter:
```typescript
const { data: requests, isLoading: requestsLoading } = useQuery({
  queryKey: ['access-requests', requestFilter],
  queryFn: () => accessRequestsApi.getRequests(requestFilter).then(r => r.data),
  refetchInterval: 30_000,
  enabled: user?.role === 'guardian' && activeTab === 'requests',
});
```

Cập nhật `accessRequestsApi.ts`:
```typescript
getPending: () => axiosInstance.get<AccessRequestDto[]>('/access-requests?status=pending'),
getRequests: (status: 'pending' | 'handled' | 'all' = 'pending') =>
  axiosInstance.get<AccessRequestDto[]>(`/access-requests?status=${status}`),
```

Layout trang — Tab switcher:
```tsx
{/* Tab switcher */}
<div className="flex gap-1 p-1 rounded-xl bg-bg-subtle border border-border-base w-fit mb-6">
  <button
    onClick={() => setActiveTab('requests')}
    className={`px-4 py-2 text-sm font-medium rounded-lg transition-all ${
      activeTab === 'requests'
        ? 'bg-brand-DEFAULT text-white shadow-sm'
        : 'text-tx-secondary hover:text-tx-primary'
    }`}
  >
    Yêu cầu
    {pendingCount > 0 && (
      <span className="ml-2 px-1.5 py-0.5 text-[10px] font-bold bg-amber-500 text-white rounded-full">
        {pendingCount}
      </span>
    )}
  </button>
  <button
    onClick={() => setActiveTab('notifications')}
    className={`px-4 py-2 text-sm font-medium rounded-lg transition-all ${
      activeTab === 'notifications'
        ? 'bg-brand-DEFAULT text-white shadow-sm'
        : 'text-tx-secondary hover:text-tx-primary'
    }`}
  >
    Thông báo
    {unreadCount > 0 && (
      <span className="ml-2 px-1.5 py-0.5 text-[10px] font-bold bg-red-500 text-white rounded-full">
        {unreadCount}
      </span>
    )}
  </button>
</div>

{/* Filter chips */}
{activeTab === 'requests' && (
  <div className="flex gap-2 mb-4">
    {(['pending', 'handled', 'all'] as const).map(f => (
      <button
        key={f}
        onClick={() => setRequestFilter(f)}
        className={`px-3 py-1.5 text-xs font-medium rounded-full border transition-colors ${
          requestFilter === f
            ? 'bg-brand-DEFAULT/10 border-brand-DEFAULT text-brand-DEFAULT'
            : 'bg-bg-subtle border-border-base text-tx-secondary hover:border-brand-DEFAULT/50'
        }`}
      >
        {f === 'pending' ? 'Chờ duyệt' : f === 'handled' ? 'Đã xử lý' : 'Tất cả'}
      </button>
    ))}
  </div>
)}

{activeTab === 'notifications' && (
  <div className="flex gap-2 mb-4">
    {(['all', 'unread', 'read'] as const).map(f => (
      <button
        key={f}
        onClick={() => setNotifFilter(f)}
        className={`px-3 py-1.5 text-xs font-medium rounded-full border transition-colors ${
          notifFilter === f
            ? 'bg-brand-DEFAULT/10 border-brand-DEFAULT text-brand-DEFAULT'
            : 'bg-bg-subtle border-border-base text-tx-secondary hover:border-brand-DEFAULT/50'
        }`}
      >
        {f === 'all' ? 'Tất cả' : f === 'unread' ? 'Chưa đọc' : 'Đã đọc'}
      </button>
    ))}
  </div>
)}
```

---

## Fix 2 (tiếp): Redesign `AccessRequestCard.tsx`

### Thiết kế mới

Request card cần hiển thị:
1. Avatar + tên con + badge lý do (màu sắc khác nhau theo reason)
2. Domain với favicon
3. Giờ gửi **đúng** (dùng `formatDateTimeVN`)
4. Context tùy theo reason (xem bên dưới)
5. Các nút action phù hợp với reason

Thay thế toàn bộ `AccessRequestCard.tsx`:

```tsx
import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { accessRequestsApi, AccessRequestDto, RespondAccessRequestDto } from '../api/accessRequestsApi';
import { formatDateTimeVN } from '../lib/formatters';
import { toast } from './feedback';
import ConfirmModal from './feedback/ConfirmModal';

interface Props {
  request: AccessRequestDto;
}

// Badge lý do request
function ReasonBadge({ reason }: { reason: string }) {
  if (reason === 'internet_paused') {
    return (
      <span className="inline-flex items-center gap-1 px-2 py-0.5 text-[11px] font-medium
                       rounded-full bg-red-500/10 text-red-500 border border-red-500/20">
        ⏸ Tạm dừng Internet
      </span>
    );
  }
  if (reason === 'time_limit_exceeded') {
    return (
      <span className="inline-flex items-center gap-1 px-2 py-0.5 text-[11px] font-medium
                       rounded-full bg-amber-500/10 text-amber-500 border border-amber-500/20">
        ⏱ Hết giờ sử dụng
      </span>
    );
  }
  return (
    <span className="inline-flex items-center gap-1 px-2 py-0.5 text-[11px] font-medium
                     rounded-full bg-blue-500/10 text-blue-500 border border-blue-500/20">
      🌐 Web mới
    </span>
  );
}

// Status badge cho request đã xử lý
function StatusBadge({ status }: { status: string }) {
  if (status === 'approved_temp') return (
    <span className="text-[11px] font-medium text-amber-500">✓ Cho phép tạm thời</span>
  );
  if (status === 'approved_permanent') return (
    <span className="text-[11px] font-medium text-green-500">✓ Đã thêm vào DS</span>
  );
  if (status === 'rejected') return (
    <span className="text-[11px] font-medium text-red-500">✗ Đã từ chối</span>
  );
  return null;
}

export default function AccessRequestCard({ request }: Props) {
  const queryClient = useQueryClient();
  const [confirmAction, setConfirmAction] = useState<RespondAccessRequestDto | null>(null);
  // Cho approve_permanent: guardian có thể chỉnh thời gian
  const [showTimeConfig, setShowTimeConfig] = useState(false);
  const [configDuration, setConfigDuration] = useState<number | null>(null);
  const [configStart, setConfigStart] = useState('07:00');
  const [configEnd, setConfigEnd] = useState('21:00');
  const [useTimeWindow, setUseTimeWindow] = useState(false);
  const [useDuration, setUseDuration] = useState(false);

  const mutation = useMutation({
    mutationFn: (dto: RespondAccessRequestDto) => accessRequestsApi.respond(request.id, dto),
    onSuccess: (_, dto) => {
      queryClient.invalidateQueries({ queryKey: ['access-requests'] });
      queryClient.invalidateQueries({ queryKey: ['notifications'] });
      if (dto.action === 'reject') toast.delete('Đã từ chối yêu cầu');
      else if (dto.action === 'approve_temp') toast.success(`Đã cho phép truy cập ${dto.durationMinutes} phút`);
      else if (dto.action === 'approve_internet') toast.success('Đã bật lại Internet');
      else if (dto.action === 'extend_time') toast.success(`Đã gia hạn thêm ${dto.durationMinutes} phút`);
      else toast.success('Đã thêm vào danh sách cho phép');
    },
    onError: () => toast.error('Có lỗi xảy ra, thử lại sau'),
  });

  const isPending = request.status === 'pending';
  const faviconUrl = `https://www.google.com/s2/favicons?domain=${request.domain}&sz=32`;

  // Context text tùy reason
  const contextText = () => {
    if (request.reason === 'internet_paused')
      return 'muốn bạn bật lại Internet';
    if (request.reason === 'time_limit_exceeded') {
      const extra = request.requestedDurationMinutes
        ? ` — xin thêm ${request.requestedDurationMinutes} phút`
        : '';
      return `đã hết thời gian sử dụng${extra}`;
    }
    return 'muốn truy cập trang này';
  };

  // Actions tùy reason
  const renderActions = () => {
    if (!isPending) return <StatusBadge status={request.status} />;

    if (request.reason === 'internet_paused') {
      return (
        <div className="flex flex-wrap gap-2 mt-3">
          <button
            onClick={() => setConfirmAction({ action: 'approve_internet' })}
            disabled={mutation.isPending}
            className="px-3 py-1.5 text-xs rounded-lg font-medium
                       bg-green-500/10 text-green-600 dark:text-green-400
                       border border-green-500/30 hover:bg-green-500/20
                       transition-colors disabled:opacity-50"
          >
            ▶ Bật lại Internet
          </button>
          <button
            onClick={() => setConfirmAction({ action: 'reject' })}
            disabled={mutation.isPending}
            className="px-3 py-1.5 text-xs rounded-lg font-medium
                       bg-red-500/10 text-red-600 dark:text-red-400
                       border border-red-500/30 hover:bg-red-500/20
                       transition-colors disabled:opacity-50"
          >
            ✕ Từ chối
          </button>
        </div>
      );
    }

    if (request.reason === 'time_limit_exceeded') {
      return (
        <div className="flex flex-wrap gap-2 mt-3">
          <button
            onClick={() => setConfirmAction({
              action: 'extend_time',
              durationMinutes: request.requestedDurationMinutes ?? 30,
            })}
            disabled={mutation.isPending}
            className="px-3 py-1.5 text-xs rounded-lg font-medium
                       bg-amber-500/10 text-amber-600 dark:text-amber-400
                       border border-amber-500/30 hover:bg-amber-500/20
                       transition-colors disabled:opacity-50"
          >
            ⏱ Gia hạn {request.requestedDurationMinutes ?? 30} phút
          </button>
          <button
            onClick={() => setConfirmAction({ action: 'reject' })}
            disabled={mutation.isPending}
            className="px-3 py-1.5 text-xs rounded-lg font-medium
                       bg-red-500/10 text-red-600 dark:text-red-400
                       border border-red-500/30 hover:bg-red-500/20
                       transition-colors disabled:opacity-50"
          >
            ✕ Từ chối
          </button>
        </div>
      );
    }

    // not_in_whitelist
    return (
      <div className="space-y-2 mt-3">
        <div className="flex flex-wrap gap-2">
          <button
            onClick={() => setConfirmAction({ action: 'approve_temp', durationMinutes: 30 })}
            disabled={mutation.isPending}
            className="px-3 py-1.5 text-xs rounded-lg font-medium
                       bg-amber-500/10 text-amber-600 dark:text-amber-400
                       border border-amber-500/30 hover:bg-amber-500/20
                       transition-colors disabled:opacity-50"
          >
            ⏱ 30 phút
          </button>
          <button
            onClick={() => setShowTimeConfig(!showTimeConfig)}
            disabled={mutation.isPending}
            className="px-3 py-1.5 text-xs rounded-lg font-medium
                       bg-brand-DEFAULT/10 text-brand-DEFAULT
                       border border-brand-DEFAULT/30 hover:bg-brand-DEFAULT/20
                       transition-colors disabled:opacity-50"
          >
            ⚙ Thêm vào DS...
          </button>
          <button
            onClick={() => setConfirmAction({ action: 'reject' })}
            disabled={mutation.isPending}
            className="px-3 py-1.5 text-xs rounded-lg font-medium
                       bg-red-500/10 text-red-600 dark:text-red-400
                       border border-red-500/30 hover:bg-red-500/20
                       transition-colors disabled:opacity-50"
          >
            ✕ Từ chối
          </button>
        </div>

        {/* Time config (hiện khi nhấn "Thêm vào DS...") */}
        {showTimeConfig && (
          <div className="p-3 rounded-lg bg-bg-elevated border border-border-base space-y-3 mt-2">
            {/* Giới hạn phút */}
            <div className="flex items-center justify-between">
              <div>
                <p className="text-xs font-medium text-tx-primary">Giới hạn sử dụng mỗi ngày</p>
                <p className="text-[11px] text-tx-secondary">Con bị chặn khi hết thời gian</p>
              </div>
              <label className="relative inline-flex items-center cursor-pointer">
                <input
                  type="checkbox"
                  checked={useDuration}
                  onChange={e => setUseDuration(e.target.checked)}
                  className="sr-only peer"
                />
                <div className="w-9 h-5 bg-bg-muted peer-focus:ring-2 peer-focus:ring-brand-DEFAULT/30
                               rounded-full peer peer-checked:after:translate-x-full
                               peer-checked:after:border-white after:content-[''] after:absolute
                               after:top-0.5 after:left-0.5 after:bg-white after:rounded-full
                               after:h-4 after:w-4 after:transition-all peer-checked:bg-brand-DEFAULT" />
              </label>
            </div>
            {useDuration && (
              <div className="flex items-center gap-2">
                <input
                  type="number"
                  value={configDuration ?? 60}
                  onChange={e => setConfigDuration(Number(e.target.value))}
                  min={5}
                  max={720}
                  className="w-20 px-2 py-1 text-sm text-center rounded-md
                             bg-bg-subtle border border-border-base text-tx-primary
                             focus:outline-none focus:border-brand-DEFAULT"
                />
                <span className="text-xs text-tx-secondary">phút mỗi ngày</span>
              </div>
            )}

            {/* Khung giờ */}
            <div className="flex items-center justify-between">
              <div>
                <p className="text-xs font-medium text-tx-primary">Khung giờ cho phép</p>
                <p className="text-[11px] text-tx-secondary">Giới hạn giờ dùng trong ngày</p>
              </div>
              <label className="relative inline-flex items-center cursor-pointer">
                <input
                  type="checkbox"
                  checked={useTimeWindow}
                  onChange={e => setUseTimeWindow(e.target.checked)}
                  className="sr-only peer"
                />
                <div className="w-9 h-5 bg-bg-muted peer-focus:ring-2 peer-focus:ring-brand-DEFAULT/30
                               rounded-full peer peer-checked:after:translate-x-full
                               peer-checked:after:border-white after:content-[''] after:absolute
                               after:top-0.5 after:left-0.5 after:bg-white after:rounded-full
                               after:h-4 after:w-4 after:transition-all peer-checked:bg-brand-DEFAULT" />
              </label>
            </div>
            {useTimeWindow && (
              <div className="grid grid-cols-2 gap-2">
                <div>
                  <label className="text-[11px] text-tx-secondary mb-1 block">Từ lúc</label>
                  <input
                    type="time"
                    value={configStart}
                    onChange={e => setConfigStart(e.target.value)}
                    className="w-full px-2 py-1.5 text-sm rounded-md
                               bg-bg-subtle border border-border-base text-tx-primary
                               focus:outline-none focus:border-brand-DEFAULT"
                  />
                </div>
                <div>
                  <label className="text-[11px] text-tx-secondary mb-1 block">Đến lúc</label>
                  <input
                    type="time"
                    value={configEnd}
                    onChange={e => setConfigEnd(e.target.value)}
                    className="w-full px-2 py-1.5 text-sm rounded-md
                               bg-bg-subtle border border-border-base text-tx-primary
                               focus:outline-none focus:border-brand-DEFAULT"
                  />
                </div>
              </div>
            )}

            <button
              onClick={() => setConfirmAction({
                action: 'approve_permanent',
                durationMinutes: useDuration ? (configDuration ?? 60) : undefined,
                startTime: useTimeWindow ? configStart : undefined,
                endTime: useTimeWindow ? configEnd : undefined,
              })}
              disabled={mutation.isPending}
              className="w-full px-3 py-2 text-sm rounded-lg font-medium
                         bg-brand-DEFAULT text-white hover:bg-brand-DEFAULT/90
                         transition-colors disabled:opacity-50"
            >
              ✅ Thêm vào danh sách cho phép
            </button>
          </div>
        )}
      </div>
    );
  };

  return (
    <>
      <div className={`p-4 rounded-xl border transition-all ${
        isPending
          ? 'bg-bg-surface border-border-base'
          : 'bg-bg-subtle border-border-base/50 opacity-75'
      }`}>
        {/* Header */}
        <div className="flex items-start gap-3">
          <img
            src={request.childAvatarUrl || '/default-avatar.png'}
            alt={request.childName}
            className="w-10 h-10 rounded-full flex-shrink-0 object-cover"
          />
          <div className="flex-1 min-w-0">
            <div className="flex items-center gap-2 flex-wrap">
              <span className="text-sm font-semibold text-brand-DEFAULT">{request.childName}</span>
              <ReasonBadge reason={request.reason} />
            </div>
            <p className="text-xs text-tx-secondary mt-0.5">{contextText()}</p>
          </div>
          <span className="text-xs text-tx-secondary flex-shrink-0">
            {formatDateTimeVN(request.requestedAt)}
          </span>
        </div>

        {/* Domain */}
        <div className="flex items-center gap-2 mt-3 p-2 rounded-lg bg-bg-subtle border border-border-base/50">
          <img src={faviconUrl} alt="" className="w-4 h-4 flex-shrink-0" />
          <span className="text-sm text-tx-primary font-medium truncate">{request.domain}</span>
          {request.fullUrl && request.fullUrl !== request.domain && (
            <span className="text-xs text-tx-secondary truncate ml-auto">
              {request.fullUrl.substring(0, 50)}...
            </span>
          )}
        </div>

        {/* Requested time config (nếu con gửi kèm) */}
        {request.reason === 'not_in_whitelist' && (request.requestedDurationMinutes || request.requestedStartTime) && (
          <div className="mt-2 px-2 py-1.5 rounded-lg bg-bg-elevated border border-border-base/50">
            <p className="text-[11px] text-tx-secondary">
              Con đề xuất:{' '}
              {request.requestedDurationMinutes && (
                <span className="font-medium text-tx-primary">
                  {request.requestedDurationMinutes} phút/ngày
                </span>
              )}
              {request.requestedStartTime && (
                <span className="font-medium text-tx-primary ml-1">
                  khung {request.requestedStartTime}–{request.requestedEndTime}
                </span>
              )}
            </p>
          </div>
        )}

        {/* Actions */}
        {renderActions()}
      </div>

      {/* Confirm Modal */}
      {confirmAction && (
        <ConfirmModal
          isOpen={true}
          onClose={() => setConfirmAction(null)}
          onConfirm={() => { mutation.mutate(confirmAction); setConfirmAction(null); }}
          title={
            confirmAction.action === 'reject' ? 'Từ chối yêu cầu'
            : confirmAction.action === 'approve_internet' ? 'Bật lại Internet'
            : confirmAction.action === 'extend_time' ? 'Gia hạn thời gian'
            : confirmAction.action === 'approve_temp' ? 'Cho phép tạm thời'
            : 'Thêm vào danh sách'
          }
          message={
            confirmAction.action === 'reject'
              ? `Từ chối yêu cầu của ${request.childName}?`
            : confirmAction.action === 'approve_internet'
              ? `Bật lại Internet cho ${request.childName}? Bộ lọc web sẽ hoạt động trở lại.`
            : confirmAction.action === 'extend_time'
              ? `Gia hạn thêm ${confirmAction.durationMinutes} phút cho ${request.childName} dùng ${request.domain}?`
            : confirmAction.action === 'approve_temp'
              ? `Cho phép ${request.childName} truy cập ${request.domain} trong ${confirmAction.durationMinutes} phút?`
            : `Thêm ${request.domain} vào danh sách cho phép cho ${request.childName}?`
          }
          variant={confirmAction.action === 'reject' ? 'danger'
            : confirmAction.action === 'approve_internet' ? 'default'
            : 'default'}
          confirmText="Xác nhận"
        />
      )}
    </>
  );
}
```

### Cập nhật `RespondAccessRequestDto` TypeScript

```typescript
export interface RespondAccessRequestDto {
  action: 'approve_temp' | 'approve_permanent' | 'reject' | 'extend_time' | 'approve_internet';
  durationMinutes?: number;
  startTime?: string;  // "HH:mm"
  endTime?: string;    // "HH:mm"
}
```

---

## Fix 4 & 5: Block Reason — Backend endpoint mới + Extension detect lý do

### Backend — Endpoint mới `GET /api/extension/block-info`

> ⚠️ Đọc `ExtensionController.cs` và `ExtensionService.cs` trước. Thêm endpoint mới sau các endpoint đang có. KHÔNG sửa gì khác.

#### Thêm vào `IExtensionService.cs`

```csharp
Task<BlockInfoResult> GetBlockInfoAsync(string googleId, string domain);
```

#### Thêm class `BlockInfoResult`

```csharp
public class BlockInfoResult
{
    // "internet_paused" | "time_limit_exceeded" | "not_in_whitelist"
    public string Reason { get; set; } = "not_in_whitelist";
    public bool DomainExistsInWhitelist { get; set; }
    public int? CurrentLimitMinutes { get; set; }
    public int? UsedSecondsToday { get; set; }
    public int? RemainingSeconds { get; set; }
    public string? AllowedStartTime { get; set; } // "HH:mm"
    public string? AllowedEndTime { get; set; }   // "HH:mm"
}
```

#### Implement trong `ExtensionService.cs`

```csharp
public async Task<BlockInfoResult> GetBlockInfoAsync(string googleId, string domain)
{
    var user = await _context.Users
        .FirstOrDefaultAsync(u => u.GoogleId == googleId);
    if (user == null) return new BlockInfoResult { Reason = "not_in_whitelist" };

    // Lý do 1: kill switch
    if (user.InternetPaused)
        return new BlockInfoResult { Reason = "internet_paused" };

    // Lý do 2: domain có trong whitelist không?
    var website = await _context.AllowedWebsites
        .FirstOrDefaultAsync(w => w.ChildId == user.Id
                               && w.Domain == domain
                               && w.IsActive);

    if (website == null)
        return new BlockInfoResult { Reason = "not_in_whitelist", DomainExistsInWhitelist = false };

    // Lý do 3: hết giờ — kiểm tra daily usage
    var today = DateOnly.FromDateTime(DateTime.Now);
    var stat = await _context.DailyUsageStats
        .FirstOrDefaultAsync(s => s.ChildId == user.Id
                               && s.AllowedWebsiteId == website.Id
                               && s.UsageDate == today);

    if (website.TimeLimitMinutes.HasValue && stat != null)
    {
        var effectiveUsed = stat.TotalSeconds - stat.BonusSeconds;
        var limitSeconds = website.TimeLimitMinutes.Value * 60;
        if (effectiveUsed >= limitSeconds)
        {
            return new BlockInfoResult
            {
                Reason = "time_limit_exceeded",
                DomainExistsInWhitelist = true,
                CurrentLimitMinutes = website.TimeLimitMinutes,
                UsedSecondsToday = effectiveUsed,
                RemainingSeconds = 0,
            };
        }
    }

    // Lý do 4: ngoài khung giờ
    if (website.AllowedStartTime.HasValue && website.AllowedEndTime.HasValue)
    {
        var now = DateTime.Now.TimeOfDay;
        if (now < website.AllowedStartTime.Value || now > website.AllowedEndTime.Value)
        {
            return new BlockInfoResult
            {
                Reason = "time_limit_exceeded", // dùng chung enum, UI phân biệt bằng AllowedStartTime
                DomainExistsInWhitelist = true,
                AllowedStartTime = website.AllowedStartTime.Value.ToString(@"HH\:mm"),
                AllowedEndTime = website.AllowedEndTime.Value.ToString(@"HH\:mm"),
            };
        }
    }

    // Default nếu gọi sai (không bị chặn)
    return new BlockInfoResult { Reason = "not_in_whitelist" };
}
```

#### Thêm endpoint vào `ExtensionController.cs`

```csharp
// GET /api/extension/block-info?domain=youtube.com
[HttpGet("block-info")]
public async Task<IActionResult> GetBlockInfo([FromQuery] string domain)
{
    var googleId = /* lấy theo cách đang dùng trong ExtensionController */;
    if (string.IsNullOrEmpty(googleId)) return Unauthorized();

    var result = await _extensionService.GetBlockInfoAsync(googleId, domain);
    return Ok(result);
}
```

---

### Backend — Sửa `RespondToRequestAsync` — thêm action mới

> ⚠️ Đọc toàn bộ method hiện tại trước. Thêm 2 case mới vào switch/if-else, KHÔNG sửa case cũ.

Thêm vào cuối `RespondAccessRequestDto` (C#):
```csharp
// Chỉ dùng khi Action = "approve_permanent" có time config
public int? DurationMinutes { get; set; }    // null = không giới hạn
public string? StartTime { get; set; }       // "HH:mm"
public string? EndTime { get; set; }         // "HH:mm"
```

Thêm 2 case mới vào `RespondToRequestAsync`:

**Case `approve_internet`** (khi reason = internet_paused):
```csharp
else if (dto.Action == "approve_internet")
{
    request.Status = "approved_permanent"; // đánh dấu đã xử lý

    // Tìm child và tắt kill switch
    var child = await _context.Users.FindAsync(request.ChildId);
    if (child != null)
    {
        child.InternetPaused = false;
        // Gửi SignalR "InternetResumed" để extension biết và auto-redirect
        await _hub.Clients
            .Group($"child_{request.ChildId}") // xem cách group cho child đang có
            .SendAsync("InternetResumed", new { childId = request.ChildId });
    }
}
```

**Case `extend_time`** (khi reason = time_limit_exceeded):
```csharp
else if (dto.Action == "extend_time")
{
    request.Status = "approved_temp";
    var bonusMinutes = dto.DurationMinutes ?? request.RequestedDurationMinutes ?? 30;

    // Tìm website trong whitelist
    var website = await _context.AllowedWebsites
        .FirstOrDefaultAsync(w => w.ChildId == request.ChildId && w.Domain == request.Domain);
    if (website == null) return (false, "Không tìm thấy website trong danh sách");

    // Thêm bonus_seconds vào daily_usage_stats
    var today = DateOnly.FromDateTime(DateTime.Now);
    var stat = await _context.DailyUsageStats
        .FirstOrDefaultAsync(s => s.ChildId == request.ChildId
                               && s.AllowedWebsiteId == website.Id
                               && s.UsageDate == today);
    if (stat != null)
    {
        stat.BonusSeconds += bonusMinutes * 60;
        // Reset warning flags để con không bị cảnh báo ngay
        stat.Warning1Sent = false;
        stat.Warning2Sent = false;
    }

    // Gửi SignalR "AccessApproved" để extension auto-redirect
    await _hub.Clients
        .Group($"child_{request.ChildId}")
        .SendAsync("AccessApproved", new
        {
            childId = request.ChildId,
            domain = request.Domain,
        });
}
```

**Sửa case `approve_permanent`** — thêm xử lý time config:
```csharp
// Trong case approve_permanent, sau khi tạo AllowedWebsite object
// Thêm time config nếu guardian cấu hình:
if (dto.DurationMinutes.HasValue && !useTimeWindow)
{
    newWebsite.TimeLimitMinutes = dto.DurationMinutes;
}
else if (!string.IsNullOrEmpty(dto.StartTime) && !string.IsNullOrEmpty(dto.EndTime))
{
    newWebsite.AllowedStartTime = TimeSpan.Parse(dto.StartTime);
    newWebsite.AllowedEndTime = TimeSpan.Parse(dto.EndTime);
}
// Nếu không config gì → unlimited (mặc định)

// Sau khi save, gửi SignalR "AccessApproved" để extension auto-redirect
await _hub.Clients
    .Group($"child_{request.ChildId}")
    .SendAsync("AccessApproved", new
    {
        childId = request.ChildId,
        domain = request.Domain,
    });
```

> **Lưu ý:** Kiểm tra xem SignalR có group riêng cho child (`child_{childId}`) không. Nếu chưa có, cần thêm vào Hub — xem `FamilyGuardianHub.cs` cách join group hiện tại để làm tương tự cho child group. Nếu extension không kết nối SignalR thì dùng polling ở bước Fix 8.

---

## Fix 5 (tiếp): Sửa `SubmitRequestAsync` — nhận reason + time config

Cập nhật signature:
```csharp
Task<(bool Success, string Message)> SubmitRequestAsync(
    string googleId, string domain, string? fullUrl,
    string reason, int? requestedDurationMinutes,
    string? requestedStartTime, string? requestedEndTime);
```

Trong implementation, thêm fields khi tạo `AccessRequest`:
```csharp
var request = new AccessRequest
{
    // ... fields cũ ...
    Reason = reason,
    RequestedDurationMinutes = requestedDurationMinutes,
    RequestedStartTime = !string.IsNullOrEmpty(requestedStartTime)
        ? TimeSpan.Parse(requestedStartTime) : null,
    RequestedEndTime = !string.IsNullOrEmpty(requestedEndTime)
        ? TimeSpan.Parse(requestedEndTime) : null,
};
```

Cập nhật endpoint trong `ExtensionController.cs`:
```csharp
public class RequestAccessDto
{
    public string Domain { get; set; } = string.Empty;
    public string? FullUrl { get; set; }
    // Thêm mới:
    public string Reason { get; set; } = "not_in_whitelist";
    public int? RequestedDurationMinutes { get; set; }
    public string? RequestedStartTime { get; set; }
    public string? RequestedEndTime { get; set; }
}
```

---

## Fix 4 & 6: Sửa `ExtensionController.cs` — check duplicate domain

Trong `SubmitRequestAsync`, khi `reason = "not_in_whitelist"`, kiểm tra domain đã có trong whitelist chưa:

```csharp
// Kiểm tra domain đã có trong whitelist của child chưa
if (reason == "not_in_whitelist")
{
    var alreadyAllowed = await _context.AllowedWebsites
        .AnyAsync(w => w.ChildId == child.Id && w.Domain == domain && w.IsActive);
    if (alreadyAllowed)
        return (false, $"Trang {domain} đã có trong danh sách cho phép rồi!");
}
```

---

## Fix 4 & 7: Frontend — Kill switch làm mờ child profile

### Kiểm tra trước
- Tìm trang chi tiết con: `ChildDetailPage.tsx` hoặc tương tự (đọc routing)
- Xem cách `internetPaused` được lấy từ data child
- Tìm component filter toggle (`FilterToggle.tsx`) xem cách disable

### Sửa trang chi tiết con

Thêm overlay/disable effect khi `internetPaused=true`:

```tsx
const isInternetPaused = child?.internetPaused === true;

// Wrapper cho toàn bộ content bên dưới kill switch
<div className={`transition-all duration-300 ${isInternetPaused ? 'opacity-40 pointer-events-none select-none' : ''}`}>
  {/* FilterToggle + tabs + website grid + tất cả nội dung còn lại */}
</div>
```

> **Lưu ý:** Kill switch toggle phải NẰM NGOÀI wrapper `opacity-40` để vẫn click được.

Thêm banner cảnh báo khi paused:
```tsx
{isInternetPaused && (
  <div className="mb-4 px-4 py-3 rounded-xl bg-red-500/10 border border-red-500/30
                  flex items-center gap-3">
    <span className="text-xl">⏸</span>
    <div>
      <p className="text-sm font-semibold text-red-500">Internet đang bị tạm dừng</p>
      <p className="text-xs text-tx-secondary mt-0.5">
        Toàn bộ truy cập web của {child.fullName} đang bị chặn. Nhấn "Bật lại" để mở.
      </p>
    </div>
  </div>
)}
```

---

## Fix 6 (tiếp): Extension — `blocked.html` + `blocked.js` form có time config

### Kiểm tra Extension trước
- **Đọc `blocked.html`** — xem layout hiện tại, class CSS, container chính
- **Đọc `blocked.js`** — xem:
  1. Cách lấy domain từ URL param
  2. Key lưu Google token trong `chrome.storage.local`
  3. `CONFIG.API_BASE` endpoint
  4. Code request access đã thêm ở Phần 1
- **Đọc `background.js`** — xem cách build blocked URL (tên URL params đang truyền)
- **KHÔNG thay đổi background.js**

### Sửa `blocked.html` — thay thế phần request access đã thêm ở Phần 1

> ⚠️ Tìm đúng block HTML đã thêm ở Phần 1 (`id="request-section"`), thay thế toàn bộ block đó bằng:

```html
<div id="request-section" style="margin-top: 24px; padding-top: 20px; border-top: 1px solid rgba(255,255,255,0.1);">
  <!-- Loading state khi gọi block-info -->
  <div id="block-info-loading" style="text-align:center; color: rgba(255,255,255,0.4); font-size: 13px; padding: 8px 0;">
    Đang tải thông tin...
  </div>

  <!-- Main request UI (ẩn cho đến khi block-info load xong) -->
  <div id="request-ui" style="display: none;">

    <!-- Context message theo reason -->
    <div id="reason-message" style="
      padding: 10px 14px;
      border-radius: 8px;
      font-size: 13px;
      margin-bottom: 14px;
      background: rgba(255,255,255,0.05);
      border: 1px solid rgba(255,255,255,0.1);
      color: rgba(255,255,255,0.7);
      line-height: 1.5;
    "></div>

    <!-- Time config (chỉ hiện cho not_in_whitelist) -->
    <div id="time-config-section" style="display: none; margin-bottom: 14px;">
      <!-- Giới hạn phút -->
      <div style="
        background: rgba(255,255,255,0.04);
        border: 1px solid rgba(255,255,255,0.1);
        border-radius: 8px;
        padding: 12px;
        margin-bottom: 8px;
      ">
        <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 8px;">
          <div>
            <div style="font-size: 13px; color: rgba(255,255,255,0.85); font-weight: 500;">Đề xuất giới hạn thời gian</div>
            <div style="font-size: 11px; color: rgba(255,255,255,0.4); margin-top: 2px;">Không bắt buộc</div>
          </div>
          <label style="position: relative; display: inline-block; width: 36px; height: 20px; cursor: pointer;">
            <input type="checkbox" id="chk-duration" style="opacity: 0; width: 0; height: 0;" />
            <span id="toggle-duration" style="
              position: absolute; inset: 0; border-radius: 20px;
              background: rgba(255,255,255,0.15); transition: 0.2s;
            "></span>
          </label>
        </div>
        <div id="duration-input" style="display: none; align-items: center; gap: 8px;">
          <input type="number" id="input-duration" value="60" min="5" max="480" style="
            width: 70px; padding: 6px 8px; text-align: center; border-radius: 6px;
            background: rgba(255,255,255,0.08); border: 1px solid rgba(255,255,255,0.2);
            color: white; font-size: 13px;
          " />
          <span style="font-size: 12px; color: rgba(255,255,255,0.5);">phút mỗi ngày</span>
        </div>
      </div>

      <!-- Khung giờ -->
      <div style="
        background: rgba(255,255,255,0.04);
        border: 1px solid rgba(255,255,255,0.1);
        border-radius: 8px;
        padding: 12px;
      ">
        <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 8px;">
          <div>
            <div style="font-size: 13px; color: rgba(255,255,255,0.85); font-weight: 500;">Đề xuất khung giờ</div>
            <div style="font-size: 11px; color: rgba(255,255,255,0.4); margin-top: 2px;">Không bắt buộc</div>
          </div>
          <label style="position: relative; display: inline-block; width: 36px; height: 20px; cursor: pointer;">
            <input type="checkbox" id="chk-timewindow" style="opacity: 0; width: 0; height: 0;" />
            <span id="toggle-timewindow" style="
              position: absolute; inset: 0; border-radius: 20px;
              background: rgba(255,255,255,0.15); transition: 0.2s;
            "></span>
          </label>
        </div>
        <div id="timewindow-inputs" style="display: none; gap: 8px;">
          <div style="display: grid; grid-template-columns: 1fr 1fr; gap: 8px;">
            <div>
              <div style="font-size: 11px; color: rgba(255,255,255,0.4); margin-bottom: 4px;">Từ lúc</div>
              <input type="time" id="input-start" value="07:00" style="
                width: 100%; padding: 6px 8px; border-radius: 6px;
                background: rgba(255,255,255,0.08); border: 1px solid rgba(255,255,255,0.2);
                color: white; font-size: 13px; box-sizing: border-box;
              " />
            </div>
            <div>
              <div style="font-size: 11px; color: rgba(255,255,255,0.4); margin-bottom: 4px;">Đến lúc</div>
              <input type="time" id="input-end" value="21:00" style="
                width: 100%; padding: 6px 8px; border-radius: 6px;
                background: rgba(255,255,255,0.08); border: 1px solid rgba(255,255,255,0.2);
                color: white; font-size: 13px; box-sizing: border-box;
              " />
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Extra time request (chỉ hiện cho time_limit_exceeded) -->
    <div id="extra-time-section" style="display: none; margin-bottom: 14px;">
      <div style="
        background: rgba(251,191,36,0.08);
        border: 1px solid rgba(251,191,36,0.2);
        border-radius: 8px;
        padding: 12px;
      ">
        <div style="font-size: 13px; color: rgba(255,255,255,0.85); margin-bottom: 8px;">
          Xin thêm bao nhiêu phút?
        </div>
        <div style="display: flex; gap: 8px; flex-wrap: wrap;">
          <button class="time-btn" data-mins="15" style="
            padding: 6px 14px; border-radius: 6px; font-size: 12px; cursor: pointer;
            background: rgba(251,191,36,0.1); color: #fbbf24;
            border: 1px solid rgba(251,191,36,0.3);
          ">15 phút</button>
          <button class="time-btn" data-mins="30" style="
            padding: 6px 14px; border-radius: 6px; font-size: 12px; cursor: pointer;
            background: rgba(251,191,36,0.15); color: #fbbf24;
            border: 1px solid rgba(251,191,36,0.4);
          ">30 phút</button>
          <button class="time-btn" data-mins="60" style="
            padding: 6px 14px; border-radius: 6px; font-size: 12px; cursor: pointer;
            background: rgba(251,191,36,0.1); color: #fbbf24;
            border: 1px solid rgba(251,191,36,0.3);
          ">60 phút</button>
        </div>
        <input type="number" id="input-extra-mins" value="30" min="5" max="480" style="
          width: 80px; margin-top: 8px; padding: 6px 8px; text-align: center;
          border-radius: 6px; background: rgba(255,255,255,0.08);
          border: 1px solid rgba(255,255,255,0.2); color: white; font-size: 13px;
        " />
        <span style="font-size: 12px; color: rgba(255,255,255,0.5); margin-left: 6px;">phút</span>
      </div>
    </div>

    <!-- Send button -->
    <button id="btn-request-access" style="
      background: rgba(124,58,237,0.15); color: #a78bfa;
      border: 1px solid rgba(124,58,237,0.4); padding: 10px 20px;
      border-radius: 8px; font-size: 14px; cursor: pointer; width: 100%;
      transition: all 0.2s; font-weight: 500;
    ">
      📨 Gửi yêu cầu cho bố/mẹ
    </button>

    <div id="request-status" style="
      margin-top: 10px; padding: 8px 12px; border-radius: 6px;
      font-size: 13px; text-align: center; display: none;
    "></div>
  </div>
</div>
```

### Sửa `blocked.js` — thay thế toàn bộ block request access đã thêm ở Phần 1

> ⚠️ Tìm đúng block code đã thêm ở Phần 1 (từ `// REQUEST ACCESS — Thêm mới`), thay thế toàn bộ bằng:

```javascript
// ============================================================
// REQUEST ACCESS v2 — Thay thế block cũ từ Phần 1
// KHÔNG sửa bất kỳ code nào khác trong file này
// ============================================================
(function initRequestAccess() {
  const loadingEl = document.getElementById('block-info-loading');
  const requestUiEl = document.getElementById('request-ui');
  const reasonMsgEl = document.getElementById('reason-message');
  const btnRequest = document.getElementById('btn-request-access');
  const statusDiv = document.getElementById('request-status');
  const timeConfigSection = document.getElementById('time-config-section');
  const extraTimeSection = document.getElementById('extra-time-section');

  if (!btnRequest || !statusDiv || !loadingEl || !requestUiEl) return;

  // ⚠️ Đọc background.js để xác nhận:
  // 1. Tên URL param truyền domain (đổi 'domain' nếu khác)
  // 2. Key lưu token trong chrome.storage.local (đổi 'googleToken' nếu khác)
  const urlParams = new URLSearchParams(window.location.search);
  const blockedDomain = urlParams.get('domain') || urlParams.get('site') || '';
  const blockedFullUrl = urlParams.get('url') || urlParams.get('fullUrl') || '';
  const TOKEN_KEY = 'googleToken'; // ⚠️ SỬA KEY NÀY theo background.js

  // ⚠️ CONFIG.API_BASE từ config.js (ví dụ: "https://.../api/extension")
  const getApiBase = () =>
    typeof CONFIG !== 'undefined' ? CONFIG.API_BASE : '/api/extension';

  let currentReason = 'not_in_whitelist';
  let isPolling = false;
  let pollInterval = null;

  function showStatus(message, type) {
    statusDiv.textContent = message;
    statusDiv.style.display = 'block';
    const colors = {
      success: { bg: 'rgba(34,197,94,0.15)', color: '#4ade80', border: 'rgba(34,197,94,0.3)' },
      error:   { bg: 'rgba(239,68,68,0.15)',  color: '#f87171', border: 'rgba(239,68,68,0.3)' },
      info:    { bg: 'rgba(251,191,36,0.15)', color: '#fbbf24', border: 'rgba(251,191,36,0.3)' },
    };
    const c = colors[type] || colors.info;
    Object.assign(statusDiv.style, {
      background: c.bg, color: c.color,
      border: `1px solid ${c.border}`,
    });
  }

  // Toggle checkbox style
  function setupToggle(checkboxId, toggleId, inputContainerId) {
    const chk = document.getElementById(checkboxId);
    const toggle = document.getElementById(toggleId);
    const container = document.getElementById(inputContainerId);
    if (!chk || !toggle || !container) return;
    chk.addEventListener('change', () => {
      toggle.style.background = chk.checked ? 'rgba(124,58,237,0.8)' : 'rgba(255,255,255,0.15)';
      container.style.display = chk.checked ? 'flex' : 'none';
    });
  }

  setupToggle('chk-duration', 'toggle-duration', 'duration-input');
  setupToggle('chk-timewindow', 'toggle-timewindow', 'timewindow-inputs');

  // Quick time buttons
  document.querySelectorAll('.time-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      const mins = btn.getAttribute('data-mins');
      const input = document.getElementById('input-extra-mins');
      if (input) input.value = mins;
      document.querySelectorAll('.time-btn').forEach(b =>
        b.style.background = 'rgba(251,191,36,0.1)');
      btn.style.background = 'rgba(251,191,36,0.25)';
    });
  });

  // Step 1: Load block-info khi trang mở
  async function loadBlockInfo() {
    if (!blockedDomain) {
      loadingEl.style.display = 'none';
      requestUiEl.style.display = 'block';
      return;
    }

    try {
      const stored = await chrome.storage.local.get([TOKEN_KEY]);
      const token = stored[TOKEN_KEY];
      if (!token) throw new Error('no token');

      const res = await fetch(
        `${getApiBase()}/block-info?domain=${encodeURIComponent(blockedDomain)}`,
        { headers: { 'Authorization': `Bearer ${token}` } }
      );
      if (!res.ok) throw new Error('api error');
      const info = await res.json();

      currentReason = info.reason || 'not_in_whitelist';

      // Hiện message theo reason
      if (currentReason === 'internet_paused') {
        reasonMsgEl.innerHTML = '⏸ <strong>Internet đang bị tạm dừng</strong> bởi phụ huynh.<br>Bạn có thể gửi yêu cầu để bật lại.';
        reasonMsgEl.style.borderColor = 'rgba(239,68,68,0.3)';
        btnRequest.textContent = '📨 Yêu cầu bật lại Internet';
      } else if (currentReason === 'time_limit_exceeded') {
        const limit = info.currentLimitMinutes ? `${info.currentLimitMinutes} phút` : 'đã hết';
        if (info.allowedStartTime) {
          reasonMsgEl.innerHTML = `⏰ Ngoài <strong>khung giờ</strong> cho phép.<br>Trang này chỉ được dùng ${info.allowedStartTime}–${info.allowedEndTime}.`;
        } else {
          reasonMsgEl.innerHTML = `⏱ Bạn đã <strong>hết thời gian</strong> (${limit}) cho <strong>${blockedDomain}</strong> hôm nay.`;
        }
        reasonMsgEl.style.borderColor = 'rgba(251,191,36,0.3)';
        extraTimeSection.style.display = 'block';
        btnRequest.textContent = '📨 Xin thêm thời gian';
      } else {
        reasonMsgEl.innerHTML = `🌐 Trang <strong>${blockedDomain}</strong> chưa được bố/mẹ cho phép.`;
        timeConfigSection.style.display = 'block';
        btnRequest.textContent = '📨 Gửi yêu cầu truy cập';
      }

    } catch {
      // Nếu lỗi, vẫn hiện form mặc định
      reasonMsgEl.innerHTML = `🌐 Trang <strong>${blockedDomain}</strong> đang bị chặn.`;
      timeConfigSection.style.display = 'block';
    }

    loadingEl.style.display = 'none';
    requestUiEl.style.display = 'block';
  }

  // Step 2: Gửi request
  btnRequest.addEventListener('click', async () => {
    btnRequest.disabled = true;
    btnRequest.style.opacity = '0.6';
    btnRequest.textContent = 'Đang gửi...';

    try {
      const stored = await chrome.storage.local.get([TOKEN_KEY]);
      const token = stored[TOKEN_KEY];
      if (!token) throw new Error('no token');

      // Lấy time config (chỉ cho not_in_whitelist)
      const chkDuration = document.getElementById('chk-duration');
      const chkTimewindow = document.getElementById('chk-timewindow');
      const inputDuration = document.getElementById('input-duration');
      const inputStart = document.getElementById('input-start');
      const inputEnd = document.getElementById('input-end');
      const inputExtraMins = document.getElementById('input-extra-mins');

      let requestedDurationMinutes = null;
      let requestedStartTime = null;
      let requestedEndTime = null;

      if (currentReason === 'not_in_whitelist') {
        if (chkDuration?.checked && inputDuration?.value) {
          requestedDurationMinutes = parseInt(inputDuration.value);
        }
        if (chkTimewindow?.checked && inputStart?.value && inputEnd?.value) {
          requestedStartTime = inputStart.value;
          requestedEndTime = inputEnd.value;
        }
      } else if (currentReason === 'time_limit_exceeded') {
        requestedDurationMinutes = parseInt(inputExtraMins?.value || '30');
      }

      const response = await fetch(`${getApiBase()}/request-access`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${token}`,
        },
        body: JSON.stringify({
          domain: blockedDomain,
          fullUrl: blockedFullUrl,
          reason: currentReason,
          requestedDurationMinutes,
          requestedStartTime,
          requestedEndTime,
        }),
      });

      if (response.ok) {
        showStatus('✅ Đã gửi! Bố/mẹ sẽ nhận được thông báo ngay.', 'success');
        btnRequest.textContent = 'Đã gửi yêu cầu';
        // Bắt đầu polling để auto-redirect khi được duyệt
        startPolling();
      } else {
        const data = await response.json().catch(() => ({}));
        showStatus(data.message || 'Gửi thất bại. Thử lại sau.', 'error');
        btnRequest.disabled = false;
        btnRequest.style.opacity = '1';
        btnRequest.textContent = currentReason === 'internet_paused'
          ? '📨 Yêu cầu bật lại Internet'
          : currentReason === 'time_limit_exceeded'
          ? '📨 Xin thêm thời gian'
          : '📨 Gửi yêu cầu truy cập';
      }
    } catch {
      showStatus('Lỗi kết nối. Kiểm tra mạng và thử lại.', 'error');
      btnRequest.disabled = false;
      btnRequest.style.opacity = '1';
    }
  });

  // Step 3: Polling sau khi gửi — auto redirect khi được duyệt (Fix 8)
  function startPolling() {
    if (isPolling) return;
    isPolling = true;
    showStatus('⏳ Đang chờ bố/mẹ duyệt... Trang sẽ tự mở khi được cho phép.', 'info');

    let attempts = 0;
    const MAX_ATTEMPTS = 40; // ~10 phút

    pollInterval = setInterval(async () => {
      attempts++;
      if (attempts > MAX_ATTEMPTS) {
        clearInterval(pollInterval);
        showStatus('Hết thời gian chờ. Thử gửi lại yêu cầu.', 'error');
        isPolling = false;
        return;
      }

      try {
        const stored = await chrome.storage.local.get([TOKEN_KEY]);
        const token = stored[TOKEN_KEY];
        if (!token) return;

        // Check xem domain đã được allow chưa
        const res = await fetch(
          `${getApiBase()}/check?domain=${encodeURIComponent(blockedDomain)}`,
          { headers: { 'Authorization': `Bearer ${token}` } }
        );
        if (!res.ok) return;
        const data = await res.json();

        // Nếu được duyệt (allowed=true VÀ internet không paused)
        if (data.allowed === true) {
          clearInterval(pollInterval);
          showStatus('✅ Đã được duyệt! Đang mở trang...', 'success');
          setTimeout(() => {
            // Redirect về trang gốc
            const targetUrl = blockedFullUrl || `https://${blockedDomain}`;
            window.location.href = targetUrl;
          }, 1000);
        }
      } catch {
        // Bỏ qua lỗi mạng, thử lại sau
      }
    }, 15_000); // poll mỗi 15 giây
  }

  // Khởi động
  loadBlockInfo();
})();
```

---

## Fix 8: Auto-reload — Backend gửi SignalR sau approve

### Kiểm tra `FamilyGuardianHub.cs`
- Xem có group nào cho child không
- Nếu không có group `child_{childId}`: thêm vào hub và để extension join khi kết nối

> **Thực tế:** Extension KHÔNG kết nối SignalR. Cơ chế auto-reload ở Fix 6 dùng **polling mỗi 15 giây** trong `blocked.js` — đây là cách hoạt động chính.

> SignalR event `AccessApproved` trong backend là tùy chọn bổ sung nếu sau này extension có kết nối hub. Hiện tại dùng polling là đủ và đúng kiến trúc.

---

## Fix 3 (tiếp): Backend `NotificationsController` — thêm filter

> ⚠️ Đọc controller hiện tại. Thêm query param, KHÔNG sửa logic cũ.

```csharp
// Sửa GET /api/notifications thêm filter (nếu chưa có)
[HttpGet]
public async Task<IActionResult> GetNotifications([FromQuery] string? filter = "all")
{
    var guardianId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    var query = _context.Notifications
        .Where(n => n.GuardianId == guardianId); // giữ nguyên điều kiện đang có

    if (filter == "unread")
        query = query.Where(n => !n.IsRead);
    else if (filter == "read")
        query = query.Where(n => n.IsRead);

    // Tiếp tục mapping và return theo cách đang có
    return Ok(await query.OrderByDescending(n => n.CreatedAt).Select(/* mapping đang có */).ToListAsync());
}
```

---

## Checklist cuối

### Fix 1 — Timezone
- [ ] `formatters.ts` có 4 hàm mới: `normalizeBackendDate`, `formatTimeVN`, `formatDateTimeVN`, `formatRelativeTime`
- [ ] Tất cả chỗ hiển thị `requestedAt`, `createdAt` đã dùng `formatDateTimeVN`

### Fix 2 & 3 — Notification UI + Tabs
- [ ] `NotificationsPage.tsx` có 2 tab: Yêu cầu / Thông báo
- [ ] Filter chips cho từng tab hoạt động đúng
- [ ] Query param `?status=pending|handled|all` hoạt động ở backend
- [ ] `AccessRequestCard.tsx` đã thay thế bằng phiên bản mới
- [ ] `ReasonBadge` hiện đúng màu theo reason
- [ ] Giờ hiển thị đúng 24h format: "16:27" không phải "23:27"

### Fix 4 & 7 — Kill switch UI
- [ ] Trang chi tiết con có overlay `opacity-40 pointer-events-none` khi `internetPaused=true`
- [ ] Banner cảnh báo hiện khi internet paused
- [ ] Kill switch toggle KHÔNG bị disable (nằm ngoài wrapper)
- [ ] Notification page: card `internet_paused` có nút "Bật lại Internet"
- [ ] `approve_internet` action gọi endpoint `pause-internet` và cập nhật child

### Fix 5 & 6 — Reason + Time config
- [ ] Backend `GET /api/extension/block-info` hoạt động
- [ ] `access_requests` lưu đúng `reason`, `requested_duration_minutes`, v.v.
- [ ] `SubmitRequestAsync` nhận và lưu các field mới
- [ ] `RespondToRequestAsync` xử lý `extend_time` (cộng bonus_seconds)
- [ ] `RespondToRequestAsync` xử lý `approve_permanent` với time config
- [ ] `RespondToRequestAsync` xử lý `approve_internet` (tắt kill switch)
- [ ] Check duplicate: `not_in_whitelist` báo lỗi nếu domain đã trong whitelist
- [ ] `bonus_seconds` được trừ trong heartbeat khi tính thời gian còn lại

### Fix 6 — Extension blocked.html v2
- [ ] blocked.html có đủ elements: `request-section`, `block-info-loading`, `request-ui`, `reason-message`, `time-config-section`, `extra-time-section`
- [ ] blocked.js gọi `/block-info` khi trang load
- [ ] UI thay đổi theo reason (internet_paused / time_limit_exceeded / not_in_whitelist)
- [ ] Time config section hiện đúng (checkbox toggle)
- [ ] Payload gửi `/request-access` có đủ `reason`, `requestedDurationMinutes`, v.v.

### Fix 8 — Auto-reload
- [ ] Sau khi gửi request, blocked.js poll `/check` mỗi 15s
- [ ] Khi `allowed=true` → redirect về `blockedFullUrl` hoặc `https://{domain}`
- [ ] Poll tự dừng sau 40 lần (~10 phút)
- [ ] `bonus_seconds` được tính đúng trong heartbeat → `/check` trả `allowed=true` sau khi gia hạn

---

## Lưu ý quan trọng

### `bonus_seconds` trong Heartbeat
> ⚠️ Sau khi thêm cột `bonus_seconds` vào `daily_usage_stats`, cần cập nhật heartbeat service:

Tìm chỗ tính thời gian còn lại trong `UpdateHeartbeatAsync` và `GetBlockInfoAsync`:
```csharp
// TRƯỚC (đang có):
var usedSeconds = stat.TotalSeconds;

// SAU (thêm mới):
var usedSeconds = stat.TotalSeconds - Math.Max(0, stat.BonusSeconds);
```

Đây là thay đổi nhỏ nhưng quan trọng để auto-reload hoạt động sau khi gia hạn.

### Dark Mode
Tất cả component mới dùng CSS variables: `bg-bg-surface`, `bg-bg-subtle`, `text-tx-primary`, `text-tx-secondary`, `border-border-base`, `brand-DEFAULT`. Màu status dùng opacity: `bg-red-500/10 text-red-500 dark:text-red-400`.
