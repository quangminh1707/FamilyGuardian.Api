# Family Guardian — Hướng dẫn triển khai 3 tính năng mới

> **Ngày tạo:** 2026-05-08  
> **Áp dụng cho:** Backend (ASP.NET Core 9), Frontend (React Vite + TypeScript), Extension (Chrome MV3)

---

## ⚠️ Quy tắc bất di bất dịch — KHÔNG ĐƯỢC VI PHẠM

| Quy tắc | Chi tiết |
|---------|---------|
| `sp_ExtensionCheckAccess` | Tuyệt đối KHÔNG sửa stored procedure này |
| Extension background.js | KHÔNG thay đổi logic đang chạy. Chỉ được THÊM mới |
| DateTime | Dùng `DateTime.Now`, KHÔNG dùng `DateTime.UtcNow` cho notification |
| Notification query | Luôn query theo `guardian_id`, KHÔNG phải `child_id` |
| Block logic | KHÔNG await `showBannerAsync` trước khi block |
| Logic hiện tại | KHÔNG thay đổi bất kỳ logic nào đang hoạt động khi thêm tính năng mới |

---

## 📦 Trạng thái SQL
-- ============================================================
-- FEATURE 1: Database Indexing
-- ============================================================
CREATE INDEX idx_wal_child_session ON web_access_logs(child_id, session_start);
CREATE INDEX idx_wal_access_result ON web_access_logs(child_id, access_result);
CREATE INDEX idx_dus_child_date ON daily_usage_stats(child_id, usage_date);

-- ============================================================
-- FEATURE 2: Request Access
-- ============================================================
CREATE TABLE access_requests (
    id              INT AUTO_INCREMENT PRIMARY KEY,
    child_id        INT NOT NULL,
    guardian_id     INT NOT NULL,
    domain          VARCHAR(255) NOT NULL,
    full_url        VARCHAR(2000) NULL,
    status          ENUM('pending','approved_temp','approved_permanent','rejected') NOT NULL DEFAULT 'pending',
    temp_expires_at TIMESTAMP NULL DEFAULT NULL,
    requested_at    TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    responded_at    TIMESTAMP NULL DEFAULT NULL,
    CONSTRAINT fk_ar_child    FOREIGN KEY (child_id)    REFERENCES users(id) ON DELETE CASCADE,
    CONSTRAINT fk_ar_guardian FOREIGN KEY (guardian_id) REFERENCES users(id) ON DELETE CASCADE,
    INDEX idx_ar_guardian_status (guardian_id, status),
    INDEX idx_ar_child (child_id, domain)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Thêm cột temp_expires_at vào allowed_websites
ALTER TABLE allowed_websites
    ADD COLUMN temp_expires_at TIMESTAMP NULL DEFAULT NULL AFTER updated_at;

-- ============================================================
-- FEATURE 3: Kill Switch
-- ============================================================
ALTER TABLE users
    ADD COLUMN internet_paused TINYINT(1) NOT NULL DEFAULT 0 AFTER filter_enabled;

-- Sửa sp_GetGuardianChildren thêm cột internet_paused
DROP PROCEDURE IF EXISTS sp_GetGuardianChildren;
DELIMITER ;;
CREATE PROCEDURE sp_GetGuardianChildren(IN p_guardian_id INT)
BEGIN
    SELECT
        u.id,
        u.full_name,
        u.email,
        u.avatar_url,
        u.is_active,
        u.created_at,
        u.filter_enabled,
        u.internet_paused,
        COALESCE(os.is_online, FALSE) AS is_online,
        os.last_seen_at,
        os.ip_address,
        (SELECT COUNT(*) FROM allowed_websites aw
         WHERE aw.child_id = u.id AND aw.is_active = TRUE) AS active_websites_count,
        (SELECT COALESCE(SUM(dus.total_seconds), 0)
         FROM daily_usage_stats dus
         WHERE dus.child_id = u.id AND dus.usage_date = CURDATE()) AS today_total_seconds
    FROM users u
    INNER JOIN guardian_child_relationships gcr ON gcr.child_id = u.id AND gcr.guardian_id = p_guardian_id
    LEFT JOIN user_online_status os ON os.user_id = u.id
    WHERE u.is_active = TRUE
    ORDER BY u.full_name;
END;;
DELIMITER ;

> ✅ **SQL đã được chạy xong.** Các bảng và cột sau đã tồn tại trong DB:
> - Index: `idx_wal_child_session`, `idx_wal_access_result` trên `web_access_logs`
> - Index: `idx_dus_child_date` trên `daily_usage_stats`
> - Bảng mới: `access_requests` (id, child_id, guardian_id, domain, full_url, status, temp_expires_at, requested_at, responded_at)
> - Cột mới: `allowed_websites.temp_expires_at TIMESTAMP NULL`
> - Cột mới: `users.internet_paused TINYINT(1) DEFAULT 0`
> - SP `sp_GetGuardianChildren` đã được sửa: trả thêm cột `internet_paused`

---

## Feature 1: Database Indexing

> Chỉ SQL, không cần thay đổi code. ✅ **Đã hoàn thành qua SQL.**

---

## Feature 2: Request Access (Yêu cầu truy cập)

### Tổng quan luồng

```
Con bị chặn → blocked.html → nhấn "Gửi yêu cầu"
  → POST /api/extension/request-access (Google Token auth)
  → Backend tạo access_requests entry
  → Backend gửi SignalR "AccessRequest" tới Guardian dashboard
  → Guardian thấy toast + badge trên notification bell
  → Guardian nhấn "Đồng ý 30 phút" / "Thêm vào danh sách" / "Từ chối"
  → PATCH /api/access-requests/{id}/respond
  → Nếu approve: thêm domain vào allowed_websites (có hoặc không có temp_expires_at)
  → Extension tự discover khi gọi /check lần tiếp theo (cache 1 phút)
```

---

### BƯỚC 1 — KIỂM TRA CODE BACKEND HIỆN CÓ

Trước khi viết bất kỳ dòng code nào, đọc và kiểm tra các file sau:

#### 1.1 Đọc `ExtensionController.cs`
- Xác nhận danh sách endpoint hiện có: `GET /check`, `GET /config`, `POST /heartbeat`, `POST /ping`, `POST /warning-ack`, `POST /tw-warning-ack`
- Xác nhận cách lấy `google_id` từ Google Token (HttpContext hoặc custom attribute)
- Xác nhận cách tìm `userId` từ `google_id`
- **KHÔNG sửa bất kỳ endpoint nào đang có**

#### 1.2 Đọc `IExtensionService.cs` và `ExtensionService.cs`
- Xem các method đang có
- Xem cách inject DbContext
- Xem cách gọi SignalR hub
- **Chỉ THÊM method mới, không sửa method cũ**

#### 1.3 Đọc `FamilyGuardianHub.cs` (SignalR Hub)
- Xem các event đang có: `ExtensionOffline`, `TimeWarning`
- Xem cách group theo guardian: `guardian_{guardianId}`
- **Chỉ THÊM method mới**

#### 1.4 Đọc `ApplicationDbContext.cs`
- Xem các DbSet đang có
- Xem cách cấu hình Entity

#### 1.5 Đọc `ChildrenController.cs`
- Xem cách xác thực guardian ownership (guardianId từ JWT)
- Xem cách trả lỗi 403/404 nhất quán với code hiện tại

---

### BƯỚC 2 — BACKEND

#### 2.1 Entity `AccessRequest.cs`

Tạo file `Models/AccessRequest.cs`:

```csharp
public class AccessRequest
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public int GuardianId { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string? FullUrl { get; set; }
    // "pending" | "approved_temp" | "approved_permanent" | "rejected"
    public string Status { get; set; } = "pending";
    public DateTime? TempExpiresAt { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.Now;
    public DateTime? RespondedAt { get; set; }

    // Navigation
    public User Child { get; set; } = null!;
    public User Guardian { get; set; } = null!;
}
```

#### 2.2 Cập nhật `ApplicationDbContext.cs`

Thêm vào DbContext (KHÔNG sửa gì khác):
```csharp
public DbSet<AccessRequest> AccessRequests { get; set; }
```

Thêm vào `OnModelCreating` (nếu có fluent config):
```csharp
modelBuilder.Entity<AccessRequest>(entity =>
{
    entity.ToTable("access_requests");
    entity.HasIndex(e => new { e.GuardianId, e.Status });
    entity.HasIndex(e => new { e.ChildId, e.Domain });
});
```

#### 2.3 Cập nhật Entity `User.cs`

Thêm property (KHÔNG sửa gì khác trong User):
```csharp
public bool InternetPaused { get; set; } = false; // cho Feature 3
public ICollection<AccessRequest> AccessRequestsAsChild { get; set; } = new List<AccessRequest>();
public ICollection<AccessRequest> AccessRequestsAsGuardian { get; set; } = new List<AccessRequest>();
```

#### 2.4 Cập nhật `AllowedWebsite.cs` Entity

Thêm property (KHÔNG sửa gì khác):
```csharp
public DateTime? TempExpiresAt { get; set; }
```

#### 2.5 Interface `IAccessRequestService.cs`

Tạo file `Services/IAccessRequestService.cs`:

```csharp
public interface IAccessRequestService
{
    // Extension gọi — child gửi request
    Task<(bool Success, string Message)> SubmitRequestAsync(string googleId, string domain, string? fullUrl);

    // Guardian gọi — xem danh sách
    Task<List<AccessRequestDto>> GetPendingRequestsAsync(int guardianId);

    // Guardian gọi — phản hồi
    Task<(bool Success, string Message)> RespondToRequestAsync(int requestId, int guardianId, RespondAccessRequestDto dto);
}
```

#### 2.6 DTO Classes

Tạo file `DTOs/AccessRequestDtos.cs`:

```csharp
public class AccessRequestDto
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public string ChildName { get; set; } = string.Empty;
    public string? ChildAvatarUrl { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string? FullUrl { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public DateTime? TempExpiresAt { get; set; }
}

public class RespondAccessRequestDto
{
    // "approve_temp" | "approve_permanent" | "reject"
    [Required]
    public string Action { get; set; } = string.Empty;

    // Chỉ dùng khi Action = "approve_temp"
    public int DurationMinutes { get; set; } = 30;
}
```

#### 2.7 Implementation `AccessRequestService.cs`

Tạo file `Services/AccessRequestService.cs`:

```csharp
public class AccessRequestService : IAccessRequestService
{
    private readonly ApplicationDbContext _context;
    private readonly IHubContext<FamilyGuardianHub> _hub;

    public AccessRequestService(ApplicationDbContext context, IHubContext<FamilyGuardianHub> hub)
    {
        _context = context;
        _hub = hub;
    }

    public async Task<(bool Success, string Message)> SubmitRequestAsync(
        string googleId, string domain, string? fullUrl)
    {
        // Tìm child
        var child = await _context.Users
            .FirstOrDefaultAsync(u => u.GoogleId == googleId && u.Role == "child");
        if (child == null) return (false, "Không tìm thấy tài khoản con");

        // Tìm tất cả guardian của child
        var guardianIds = await _context.GuardianChildRelationships
            .Where(r => r.ChildId == child.Id)
            .Select(r => r.GuardianId)
            .ToListAsync();
        if (!guardianIds.Any()) return (false, "Không tìm thấy guardian");

        // Kiểm tra đã có pending request chưa (tránh spam)
        var existingPending = await _context.AccessRequests
            .AnyAsync(r => r.ChildId == child.Id
                        && r.Domain == domain
                        && r.Status == "pending");
        if (existingPending) return (false, "Đã gửi yêu cầu cho trang này rồi, vui lòng chờ");

        // Tạo request cho từng guardian
        foreach (var guardianId in guardianIds)
        {
            var request = new AccessRequest
            {
                ChildId = child.Id,
                GuardianId = guardianId,
                Domain = domain,
                FullUrl = fullUrl,
                Status = "pending",
                RequestedAt = DateTime.Now
            };
            _context.AccessRequests.Add(request);

            // Tạo notification trong DB
            var notification = new Notification
            {
                GuardianId = guardianId,
                ChildId = child.Id,
                Title = "Yêu cầu truy cập",
                Message = $"{child.FullName} muốn truy cập {domain}",
                Type = "info",
                CreatedAt = DateTime.Now
            };
            _context.Notifications.Add(notification);
        }

        await _context.SaveChangesAsync();

        // Gửi SignalR tới tất cả guardian
        foreach (var guardianId in guardianIds)
        {
            await _hub.Clients
                .Group($"guardian_{guardianId}")
                .SendAsync("AccessRequest", new
                {
                    childName = child.FullName,
                    childAvatarUrl = child.AvatarUrl,
                    domain = domain,
                    message = $"{child.FullName} muốn truy cập {domain}"
                });
        }

        return (true, "Đã gửi yêu cầu thành công");
    }

    public async Task<List<AccessRequestDto>> GetPendingRequestsAsync(int guardianId)
    {
        return await _context.AccessRequests
            .Include(r => r.Child)
            .Where(r => r.GuardianId == guardianId && r.Status == "pending")
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
                RequestedAt = r.RequestedAt
            })
            .ToListAsync();
    }

    public async Task<(bool Success, string Message)> RespondToRequestAsync(
        int requestId, int guardianId, RespondAccessRequestDto dto)
    {
        var request = await _context.AccessRequests
            .Include(r => r.Child)
            .FirstOrDefaultAsync(r => r.Id == requestId && r.GuardianId == guardianId);

        if (request == null) return (false, "Không tìm thấy yêu cầu");
        if (request.Status != "pending") return (false, "Yêu cầu này đã được xử lý rồi");

        request.RespondedAt = DateTime.Now;

        if (dto.Action == "reject")
        {
            request.Status = "rejected";
            await _context.SaveChangesAsync();
            return (true, "Đã từ chối yêu cầu");
        }

        // Kiểm tra domain đã có trong whitelist chưa
        var existing = await _context.AllowedWebsites
            .FirstOrDefaultAsync(w => w.ChildId == request.ChildId && w.Domain == request.Domain);

        if (dto.Action == "approve_temp")
        {
            request.Status = "approved_temp";
            var expiresAt = DateTime.Now.AddMinutes(dto.DurationMinutes);
            request.TempExpiresAt = expiresAt;

            if (existing != null)
            {
                // Kích hoạt lại và set thời hạn
                existing.IsActive = true;
                existing.TempExpiresAt = expiresAt;
            }
            else
            {
                _context.AllowedWebsites.Add(new AllowedWebsite
                {
                    ChildId = request.ChildId,
                    Domain = request.Domain,
                    DisplayName = request.Domain,
                    FaviconUrl = $"https://www.google.com/s2/favicons?domain={request.Domain}&sz=64",
                    IsActive = true,
                    AddedBy = guardianId,
                    TempExpiresAt = expiresAt,
                    CreatedAt = DateTime.Now
                });
            }
        }
        else if (dto.Action == "approve_permanent")
        {
            request.Status = "approved_permanent";

            if (existing != null)
            {
                existing.IsActive = true;
                existing.TempExpiresAt = null; // xóa temp nếu có
            }
            else
            {
                _context.AllowedWebsites.Add(new AllowedWebsite
                {
                    ChildId = request.ChildId,
                    Domain = request.Domain,
                    DisplayName = request.Domain,
                    FaviconUrl = $"https://www.google.com/s2/favicons?domain={request.Domain}&sz=64",
                    IsActive = true,
                    AddedBy = guardianId,
                    CreatedAt = DateTime.Now
                });
            }
        }
        else
        {
            return (false, "Action không hợp lệ");
        }

        await _context.SaveChangesAsync();
        return (true, "Đã xử lý yêu cầu thành công");
    }
}
```

#### 2.8 Thêm endpoint vào `ExtensionController.cs`

> ⚠️ Đọc file ExtensionController hiện tại trước. Chỉ thêm endpoint mới, KHÔNG sửa gì khác.

Inject service (thêm vào constructor):
```csharp
private readonly IAccessRequestService _accessRequestService;
// thêm vào constructor parameter và gán
```

Thêm endpoint mới (sau các endpoint hiện có):
```csharp
[HttpPost("request-access")]
public async Task<IActionResult> RequestAccess([FromBody] RequestAccessDto dto)
{
    // Lấy google_id theo đúng cách đang dùng trong ExtensionController (đọc code hiện tại)
    var googleId = /* cách lấy google_id đang dùng */;
    if (string.IsNullOrEmpty(googleId))
        return Unauthorized();

    if (string.IsNullOrEmpty(dto.Domain))
        return BadRequest("Domain không được để trống");

    var (success, message) = await _accessRequestService.SubmitRequestAsync(
        googleId, dto.Domain, dto.FullUrl);

    if (!success) return BadRequest(new { message });
    return Ok(new { message });
}
```

DTO cho endpoint này (thêm vào file DTOs phù hợp):
```csharp
public class RequestAccessDto
{
    public string Domain { get; set; } = string.Empty;
    public string? FullUrl { get; set; }
}
```

#### 2.9 Tạo `AccessRequestsController.cs` (controller mới)

```csharp
[ApiController]
[Route("api/access-requests")]
[Authorize(Roles = "guardian")]
public class AccessRequestsController : ControllerBase
{
    private readonly IAccessRequestService _service;

    public AccessRequestsController(IAccessRequestService service)
    {
        _service = service;
    }

    // GET /api/access-requests
    [HttpGet]
    public async Task<IActionResult> GetRequests()
    {
        var guardianId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var requests = await _service.GetPendingRequestsAsync(guardianId);
        return Ok(requests);
    }

    // PATCH /api/access-requests/{id}/respond
    [HttpPatch("{id}/respond")]
    public async Task<IActionResult> Respond(int id, [FromBody] RespondAccessRequestDto dto)
    {
        var guardianId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        var (success, message) = await _service.RespondToRequestAsync(id, guardianId, dto);
        if (!success) return BadRequest(new { message });
        return Ok(new { message });
    }
}
```

#### 2.10 Xác nhận `FamilyGuardianHub.cs`

> ⚠️ Đọc file hub hiện tại. Xác nhận event name `"AccessRequest"` chưa bị conflict với event đang có.
> Không cần thêm method vào hub vì event được gửi qua `IHubContext` từ Service.

#### 2.11 Cập nhật BackgroundService — Cleanup temp access

> ⚠️ Đọc BackgroundService hiện tại trước. Tìm chu kỳ phù hợp (gần ping detection, mỗi 60s là ổn).  
> KHÔNG sửa logic ping detection. Chỉ THÊM block sau vào vòng lặp:

```csharp
// Cleanup temp access đã hết hạn
var expiredTempAccess = await context.AllowedWebsites
    .Where(w => w.TempExpiresAt != null && w.TempExpiresAt < DateTime.Now)
    .ToListAsync();

foreach (var w in expiredTempAccess)
{
    w.IsActive = false;
    w.TempExpiresAt = null;
}

if (expiredTempAccess.Any())
    await context.SaveChangesAsync();
```

#### 2.12 Đăng ký DI trong `Program.cs`

Thêm (KHÔNG sửa các đăng ký đang có):
```csharp
builder.Services.AddScoped<IAccessRequestService, AccessRequestService>();
```

---

### BƯỚC 3 — FRONTEND

#### 3.1 Kiểm tra file hiện có

Trước khi viết code, đọc các file sau:

- **`src/hooks/useExtensionMonitor.ts`** — xem cách handle SignalR events. Tìm handler `TimeWarning` để hiểu pattern, áp dụng tương tự cho `AccessRequest`
- **`src/store/notificationStore.ts`** — xem state notification hiện tại
- **`src/layouts/AppLayout.tsx`** — xem notification bell đang ở đâu, cách import components
- **`src/api/`** — xem naming convention của api files (ví dụ: `childrenApi.ts`)
- **`src/components/feedback/`** — đọc `Toast.tsx`, `toastStore.ts`, `ConfirmModal.tsx`
- **`src/components/WebsiteCard.tsx`** — xem pattern CSS variables đang dùng
- **`src/lib/formatters.ts`** — xem hàm format thời gian để dùng đúng (có timezone fix 'Z')

#### 3.2 Tạo `src/api/accessRequestsApi.ts`

```typescript
import axiosInstance from './axiosInstance'; // dùng đúng tên file axios đang có

export interface AccessRequestDto {
  id: number;
  childId: number;
  childName: string;
  childAvatarUrl?: string;
  domain: string;
  fullUrl?: string;
  status: string;
  requestedAt: string;
  tempExpiresAt?: string;
}

export interface RespondAccessRequestDto {
  action: 'approve_temp' | 'approve_permanent' | 'reject';
  durationMinutes?: number;
}

export const accessRequestsApi = {
  getPending: () =>
    axiosInstance.get<AccessRequestDto[]>('/access-requests'),

  respond: (id: number, dto: RespondAccessRequestDto) =>
    axiosInstance.patch(`/access-requests/${id}/respond`, dto),
};
```

#### 3.3 Cập nhật `useExtensionMonitor.ts`

> ⚠️ Đọc file hiện tại trước. Thêm handler `AccessRequest` theo đúng pattern của `TimeWarning`. KHÔNG thay đổi bất kỳ handler nào đang có.

Thêm vào sau handler `TimeWarning` đang có:
```typescript
connection.on('AccessRequest', (data: {
  childName: string;
  childAvatarUrl?: string;
  domain: string;
  message: string;
}) => {
  toast.warning(`🔔 ${data.childName} muốn truy cập ${data.domain}`);
  queryClient.invalidateQueries({ queryKey: ['access-requests'] });
  queryClient.invalidateQueries({ queryKey: ['notifications'] });
});
```

#### 3.4 Tạo `src/components/AccessRequestCard.tsx`

> Dùng đúng CSS variables hệ thống: `bg-bg-subtle`, `text-tx-primary`, `text-tx-secondary`, `border-border-base`, `brand-DEFAULT`.

```tsx
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import {
  accessRequestsApi,
  AccessRequestDto,
  RespondAccessRequestDto,
} from '../api/accessRequestsApi';
import { toast } from './feedback'; // dùng đúng path import toast hiện tại
import ConfirmModal from './feedback/ConfirmModal';

interface Props {
  request: AccessRequestDto;
}

// Timezone fix — dùng đúng logic đang có trong formatters.ts
function normalizeDate(dateStr: string): Date {
  const normalized =
    dateStr.endsWith('Z') || /[+-]\d{2}:\d{2}$/.test(dateStr)
      ? dateStr
      : dateStr + 'Z';
  return new Date(normalized);
}

export default function AccessRequestCard({ request }: Props) {
  const queryClient = useQueryClient();
  const [confirmAction, setConfirmAction] = useState<RespondAccessRequestDto | null>(null);

  const mutation = useMutation({
    mutationFn: (dto: RespondAccessRequestDto) =>
      accessRequestsApi.respond(request.id, dto),
    onSuccess: (_, dto) => {
      queryClient.invalidateQueries({ queryKey: ['access-requests'] });
      queryClient.invalidateQueries({ queryKey: ['notifications'] });
      if (dto.action === 'reject') toast.delete('Đã từ chối yêu cầu');
      else if (dto.action === 'approve_temp')
        toast.success(`Đã cho phép truy cập ${dto.durationMinutes} phút`);
      else toast.success('Đã thêm vào danh sách cho phép');
    },
    onError: () => toast.error('Có lỗi xảy ra, thử lại sau'),
  });

  const faviconUrl = `https://www.google.com/s2/favicons?domain=${request.domain}&sz=32`;
  const timeStr = normalizeDate(request.requestedAt).toLocaleTimeString('vi-VN', {
    hour: '2-digit',
    minute: '2-digit',
  });

  return (
    <>
      <div className="flex items-start gap-3 p-3 rounded-lg bg-bg-subtle border border-border-base">
        {/* Avatar con */}
        <img
          src={request.childAvatarUrl || '/default-avatar.png'}
          alt={request.childName}
          className="w-9 h-9 rounded-full flex-shrink-0 object-cover"
        />

        <div className="flex-1 min-w-0">
          <p className="text-sm font-medium text-tx-primary">
            <span className="text-brand-DEFAULT">{request.childName}</span>
            {' '}muốn truy cập
          </p>

          <div className="flex items-center gap-1.5 mt-0.5">
            <img src={faviconUrl} alt="" className="w-4 h-4 flex-shrink-0" />
            <span className="text-sm text-tx-secondary truncate">{request.domain}</span>
          </div>

          <p className="text-xs text-tx-secondary mt-1">{timeStr}</p>

          {/* Action buttons */}
          <div className="flex flex-wrap gap-2 mt-2">
            <button
              onClick={() => setConfirmAction({ action: 'approve_temp', durationMinutes: 30 })}
              disabled={mutation.isPending}
              className="px-2.5 py-1 text-xs rounded-md font-medium
                         bg-amber-500/10 text-amber-600 dark:text-amber-400
                         border border-amber-500/30 hover:bg-amber-500/20
                         transition-colors disabled:opacity-50"
            >
              ⏱ 30 phút
            </button>
            <button
              onClick={() => setConfirmAction({ action: 'approve_permanent' })}
              disabled={mutation.isPending}
              className="px-2.5 py-1 text-xs rounded-md font-medium
                         bg-green-500/10 text-green-600 dark:text-green-400
                         border border-green-500/30 hover:bg-green-500/20
                         transition-colors disabled:opacity-50"
            >
              ✅ Thêm vào DS
            </button>
            <button
              onClick={() => setConfirmAction({ action: 'reject' })}
              disabled={mutation.isPending}
              className="px-2.5 py-1 text-xs rounded-md font-medium
                         bg-red-500/10 text-red-600 dark:text-red-400
                         border border-red-500/30 hover:bg-red-500/20
                         transition-colors disabled:opacity-50"
            >
              ✕ Từ chối
            </button>
          </div>
        </div>
      </div>

      {confirmAction && (
        <ConfirmModal
          isOpen={true}
          onClose={() => setConfirmAction(null)}
          onConfirm={() => {
            mutation.mutate(confirmAction);
            setConfirmAction(null);
          }}
          title={
            confirmAction.action === 'reject'
              ? 'Từ chối yêu cầu'
              : confirmAction.action === 'approve_temp'
              ? 'Cho phép tạm thời'
              : 'Thêm vào danh sách'
          }
          message={
            confirmAction.action === 'reject'
              ? `Từ chối cho ${request.childName} truy cập ${request.domain}?`
              : confirmAction.action === 'approve_temp'
              ? `Cho phép ${request.childName} truy cập ${request.domain} trong ${confirmAction.durationMinutes} phút?`
              : `Thêm ${request.domain} vào danh sách cho phép vĩnh viễn cho ${request.childName}?`
          }
          variant={confirmAction.action === 'reject' ? 'danger' : 'default'}
          confirmText="Xác nhận"
        />
      )}
    </>
  );
}
```

#### 3.5 Tích hợp vào Notification Panel / Dashboard

> ⚠️ Đọc code notification panel đang có. Xác định đây là component riêng hay inline trong layout.

Thêm query (trong component chứa notification panel):
```typescript
// Chỉ fetch cho Guardian
const { data: pendingRequests } = useQuery({
  queryKey: ['access-requests'],
  queryFn: () => accessRequestsApi.getPending().then(r => r.data),
  refetchInterval: 30_000, // khớp với heartbeat extension
  enabled: user?.role === 'guardian',
});
```

Thêm badge count vào notification bell (cộng thêm vào unread count đang có):
```tsx
{/* Thêm pending requests vào logic badge đang có */}
{((unreadCount ?? 0) + (pendingRequests?.length ?? 0)) > 0 && (
  <span className="absolute -top-1 -right-1 w-4 h-4 text-[10px] font-bold
                   bg-red-500 text-white rounded-full flex items-center justify-center">
    {(unreadCount ?? 0) + (pendingRequests?.length ?? 0)}
  </span>
)}
```

Thêm section trong notification panel (đặt TRƯỚC danh sách notification thường):
```tsx
{pendingRequests && pendingRequests.length > 0 && (
  <div className="p-3 border-b border-border-base">
    <p className="text-xs font-semibold text-tx-secondary uppercase tracking-wide mb-2">
      Yêu cầu truy cập ({pendingRequests.length})
    </p>
    <div className="space-y-2">
      {pendingRequests.map(req => (
        <AccessRequestCard key={req.id} request={req} />
      ))}
    </div>
  </div>
)}
```

---

### BƯỚC 4 — EXTENSION (blocked.html + blocked.js)

#### 4.1 Kiểm tra file hiện có — QUAN TRỌNG

Trước khi sửa, đọc kỹ:

- **`blocked.html`** — xem layout, CSS classes, cấu trúc HTML
- **`blocked.js`** — xem cách domain được lấy từ URL params (tên param là gì: `domain`? `url`? `site`?)
- **`background.js`** — tìm:
  1. Key lưu Google Token trong `chrome.storage.local` (ví dụ: `'token'`, `'googleToken'`, `'accessToken'`)
  2. Xem `CONFIG.API_BASE` value (ví dụ: `"https://familyguardian-api.onrender.com/api/extension"`)
  3. KHÔNG thay đổi bất kỳ logic nào trong background.js

#### 4.2 Sửa `blocked.html`

> Thêm phần UI "Gửi yêu cầu" vào sau nội dung blocked hiện tại. Giữ nguyên toàn bộ HTML đang có.

```html
<!-- Thêm vào sau phần nội dung blocked hiện tại, bên trong container chính -->
<div id="request-section" style="
  margin-top: 24px;
  padding-top: 20px;
  border-top: 1px solid rgba(255,255,255,0.1);
">
  <button id="btn-request-access" style="
    background: rgba(124,58,237,0.15);
    color: #a78bfa;
    border: 1px solid rgba(124,58,237,0.4);
    padding: 10px 20px;
    border-radius: 8px;
    font-size: 14px;
    cursor: pointer;
    width: 100%;
    transition: background 0.2s;
  ">
    📨 Gửi yêu cầu truy cập cho bố/mẹ
  </button>

  <div id="request-status" style="
    margin-top: 10px;
    padding: 8px 12px;
    border-radius: 6px;
    font-size: 13px;
    text-align: center;
    display: none;
  "></div>
</div>
```

#### 4.3 Sửa `blocked.js`

> ⚠️ Đọc file blocked.js hiện tại trước. KHÔNG thay đổi code cũ. Chỉ THÊM phần sau vào cuối file.
>
> Trước khi thêm: xác nhận tên URL param dùng để truyền domain (đọc blocked.js + nơi tạo URL blocked trong background.js).

```javascript
// ============================================================
// REQUEST ACCESS — Thêm mới, KHÔNG sửa code cũ phía trên
// ============================================================
(function initRequestAccess() {
  const btnRequest = document.getElementById('btn-request-access');
  const statusDiv = document.getElementById('request-status');
  if (!btnRequest || !statusDiv) return;

  // ⚠️ Kiểm tra tên URL param trong blocked.js/background.js hiện tại
  // Thay 'domain' bằng tên param thực tế nếu khác
  const urlParams = new URLSearchParams(window.location.search);
  const blockedDomain = urlParams.get('domain') || '';
  const blockedFullUrl = urlParams.get('url') || urlParams.get('fullUrl') || '';

  function showStatus(message, type) {
    statusDiv.textContent = message;
    statusDiv.style.display = 'block';
    if (type === 'success') {
      statusDiv.style.background = 'rgba(34,197,94,0.15)';
      statusDiv.style.color = '#4ade80';
      statusDiv.style.border = '1px solid rgba(34,197,94,0.3)';
    } else if (type === 'error') {
      statusDiv.style.background = 'rgba(239,68,68,0.15)';
      statusDiv.style.color = '#f87171';
      statusDiv.style.border = '1px solid rgba(239,68,68,0.3)';
    } else {
      statusDiv.style.background = 'rgba(251,191,36,0.15)';
      statusDiv.style.color = '#fbbf24';
      statusDiv.style.border = '1px solid rgba(251,191,36,0.3)';
    }
  }

  btnRequest.addEventListener('mouseover', () => {
    btnRequest.style.background = 'rgba(124,58,237,0.25)';
  });
  btnRequest.addEventListener('mouseout', () => {
    if (!btnRequest.disabled) btnRequest.style.background = 'rgba(124,58,237,0.15)';
  });

  btnRequest.addEventListener('click', async () => {
    btnRequest.disabled = true;
    btnRequest.style.opacity = '0.6';
    btnRequest.textContent = 'Đang gửi...';

    try {
      // ⚠️ QUAN TRỌNG: Thay 'googleToken' bằng KEY thực tế đang dùng trong background.js
      // Ví dụ: chrome.storage.local.get(['token']) nếu key là 'token'
      const stored = await chrome.storage.local.get(['googleToken']); // SỬA KEY NÀY
      const token = stored.googleToken; // SỬA KEY NÀY

      if (!token) {
        showStatus('Không tìm thấy phiên đăng nhập. Mở popup extension và đăng nhập lại.', 'error');
        btnRequest.disabled = false;
        btnRequest.style.opacity = '1';
        btnRequest.textContent = '📨 Gửi yêu cầu truy cập cho bố/mẹ';
        return;
      }

      // Xây API URL từ CONFIG.API_BASE đang có trong config.js
      // CONFIG.API_BASE = "https://.../api/extension"
      // → request-access endpoint = "https://.../api/extension/request-access"
      const apiUrl = (typeof CONFIG !== 'undefined' ? CONFIG.API_BASE : '/api/extension')
        + '/request-access';

      const response = await fetch(apiUrl, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${token}`,
        },
        body: JSON.stringify({
          domain: blockedDomain,
          fullUrl: blockedFullUrl,
        }),
      });

      if (response.ok) {
        showStatus('✅ Đã gửi! Bố/mẹ sẽ nhận được thông báo ngay.', 'success');
        btnRequest.textContent = 'Đã gửi yêu cầu';
        // Disable 5 phút để tránh spam
        setTimeout(() => {
          btnRequest.disabled = false;
          btnRequest.style.opacity = '1';
          btnRequest.textContent = '📨 Gửi yêu cầu truy cập cho bố/mẹ';
          statusDiv.style.display = 'none';
        }, 5 * 60 * 1000);
      } else {
        const data = await response.json().catch(() => ({}));
        showStatus(data.message || 'Gửi thất bại. Thử lại sau.', 'error');
        btnRequest.disabled = false;
        btnRequest.style.opacity = '1';
        btnRequest.textContent = '📨 Gửi yêu cầu truy cập cho bố/mẹ';
      }
    } catch (err) {
      showStatus('Lỗi kết nối. Kiểm tra mạng và thử lại.', 'error');
      btnRequest.disabled = false;
      btnRequest.style.opacity = '1';
      btnRequest.textContent = '📨 Gửi yêu cầu truy cập cho bố/mẹ';
    }
  });
})();
```

---

## Feature 3: Kill Switch (Tạm dừng Internet)

### Tổng quan luồng

```
Guardian nhấn "Tạm dừng Internet" trên Child Card
  → PATCH /api/children/{childId}/pause-internet
  → users.internet_paused = 1
  → Extension gọi /check domain bất kỳ → backend check internet_paused = 1 → trả allowed=false
  → Extension redirect blocked.html (logic hiện tại, không cần sửa)
  → Extension heartbeat 30s → backend check internet_paused = 1 → trả limitExceeded=true
  → Extension blockTab() ngay (logic hiện tại, không cần sửa)
```

> **Tại sao KHÔNG cần sửa Extension:**
> - `/check` trả `allowed=false` → extension redirect blocked.html ✅ (logic đang chạy)
> - heartbeat trả `limitExceeded=true` → extension gọi `blockTab()` ✅ (logic đang chạy)
> - Kill Switch chỉ thay đổi giá trị trả về ở backend, extension không cần biết lý do.
>
> **Tuyệt đối KHÔNG động vào background.js cho feature này.**

---

### BƯỚC 1 — KIỂM TRA CODE BACKEND HIỆN CÓ

#### 1.1 Đọc `ExtensionService.cs` — method CheckAccess
- Xem toàn bộ flow: nhận `google_id` → tìm user → gọi `sp_ExtensionCheckAccess` → trả kết quả
- Xác định vị trí thêm check `internet_paused` (phải TRƯỚC khi gọi SP)
- Xác định object `user` đã được query ở đâu (tránh query 2 lần)

#### 1.2 Đọc `ExtensionService.cs` — method UpdateHeartbeat
- Xem toàn bộ flow: nhận request → xử lý → trả `HeartbeatResult`
- Xác định vị trí thêm check `internet_paused` (phải TRƯỚC mọi logic website check)
- Xác định object `user` đã được query ở đâu (tránh query 2 lần)

#### 1.3 Đọc DTO map từ `sp_GetGuardianChildren`
- Xác nhận class/record đang dùng để map result SP
- Cần thêm field `InternetPaused`

#### 1.4 Đọc `ChildrenController.cs`
- Xem cách verify guardian owns child (dùng lại pattern đang có)
- Xem endpoint `PATCH /{childId}/filter` để biết pattern toggle đang dùng

---

### BƯỚC 2 — BACKEND

#### 2.1 Cập nhật Entity `User.cs`

Đã hướng dẫn thêm `InternetPaused` ở Feature 2 phần 2.3. Nếu chưa thêm thì thêm vào:
```csharp
public bool InternetPaused { get; set; } = false;
```

#### 2.2 Cập nhật DTO map từ `sp_GetGuardianChildren`

Tìm class DTO đang dùng để map kết quả SP (đọc code để xác định tên class), thêm:
```csharp
public bool InternetPaused { get; set; }
```

#### 2.3 Thêm endpoint vào `ChildrenController.cs`

> ⚠️ Đọc file trước. Copy đúng pattern verify ownership đang dùng.

```csharp
// PATCH /api/children/{childId}/pause-internet
[HttpPatch("{childId}/pause-internet")]
public async Task<IActionResult> TogglePauseInternet(int childId)
{
    var guardianId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    // Dùng đúng pattern verify ownership đang có trong ChildrenController
    var relationship = await _context.GuardianChildRelationships
        .FirstOrDefaultAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
    if (relationship == null) return Forbid();

    var child = await _context.Users.FindAsync(childId);
    if (child == null) return NotFound();

    child.InternetPaused = !child.InternetPaused;
    await _context.SaveChangesAsync();

    return Ok(new
    {
        internetPaused = child.InternetPaused,
        message = child.InternetPaused
            ? $"Đã tạm dừng internet cho {child.FullName}"
            : $"Đã bật lại internet cho {child.FullName}"
    });
}
```

#### 2.4 Cập nhật `ExtensionService.cs` — CheckAccess

> ⚠️ Đọc toàn bộ method `CheckAccessAsync` (hoặc tên thực tế) trước khi sửa.
> Thêm check `internet_paused` vào ĐẦU method, TRƯỚC khi gọi `sp_ExtensionCheckAccess`.
> KHÔNG thay đổi logic sau check này.

```csharp
// THÊM VÀO ĐẦU method, sau khi đã có object user (tránh query 2 lần nếu đã query ở trên)
// Nếu chưa có user object thì query:
// var user = await _context.Users.FirstOrDefaultAsync(u => u.GoogleId == googleId);
// if (user == null) return new CheckAccessResult { Allowed = false };

if (user.InternetPaused)
{
    return new CheckAccessResult { Allowed = false }; // hoặc kiểu trả về đang dùng
}

// Tiếp tục gọi sp_ExtensionCheckAccess như cũ
```

#### 2.5 Cập nhật `ExtensionService.cs` — UpdateHeartbeat

> ⚠️ Đọc toàn bộ method `UpdateHeartbeatAsync` trước khi sửa.
> Thêm check `internet_paused` vào ĐẦU method, TRƯỚC mọi logic khác.
> KHÔNG thay đổi logic cũ phía sau.

```csharp
// THÊM VÀO ĐẦU method UpdateHeartbeatAsync
// (tái sử dụng user object nếu đã query, nếu chưa thì query)
if (user.InternetPaused)
{
    return new HeartbeatResult
    {
        LimitExceeded = true
        // Các field khác giữ default null/false/0
    };
}

// Tiếp tục logic cũ từ đây — KHÔNG SỬA GÌ
```

---

### BƯỚC 3 — FRONTEND

#### 3.1 Kiểm tra file hiện có

- **Tìm Child Card component** (tên file có thể là `ChildCard.tsx`, `ChildItem.tsx`, hoặc inline trong Dashboard page — đọc routing và Dashboard page để xác định)
- **Đọc `FilterToggle.tsx`** — xem pattern toggle, xem cách dùng mutation và confirm modal
- **Đọc API file children** — xem endpoint `PATCH /{childId}/filter` được implement như thế nào để làm tương tự
- **Đọc TypeScript interface của child object** — thêm `internetPaused: boolean`

#### 3.2 Cập nhật TypeScript interface/type cho Child

Tìm interface/type (ví dụ `ChildDto`, `Child`, `ChildInfo`), thêm:
```typescript
internetPaused: boolean;
```

#### 3.3 Thêm API function vào file children API

```typescript
// Thêm vào file API children đang có (KHÔNG sửa functions cũ)
export const togglePauseInternet = (childId: number) =>
  axiosInstance.patch<{ internetPaused: boolean; message: string }>(
    `/children/${childId}/pause-internet`
  );
```

#### 3.4 Thêm Kill Switch vào Child Card

> ⚠️ Đọc Child Card component hiện tại. Đặt toggle gần `FilterToggle` đang có.
> KHÔNG thay đổi bất kỳ UI/logic nào đang có. Chỉ thêm phần Kill Switch.

Thêm state và mutation vào component:
```tsx
const [showPauseConfirm, setShowPauseConfirm] = useState(false);
const queryClient = useQueryClient(); // nếu chưa có

const pauseMutation = useMutation({
  mutationFn: () => togglePauseInternet(child.id),
  onSuccess: (res) => {
    queryClient.invalidateQueries({ queryKey: ['children'] }); // dùng đúng queryKey đang có
    if (res.data.internetPaused) {
      toast.warning(`⏸ Đã tạm dừng internet cho ${child.fullName}`);
    } else {
      toast.success(`▶️ Đã bật lại internet cho ${child.fullName}`);
    }
  },
  onError: () => toast.error('Có lỗi xảy ra'),
});
```

Thêm UI (đặt sau `FilterToggle` đang có):
```tsx
{/* Kill Switch — đặt sau FilterToggle */}
<div className="flex items-center justify-between gap-2 mt-2 pt-2 border-t border-border-base">
  <div className="flex items-center gap-1.5">
    <span className={`w-2 h-2 rounded-full flex-shrink-0 ${
      child.internetPaused ? 'bg-red-500' : 'bg-green-500'
    }`} />
    <span className="text-xs text-tx-secondary">
      {child.internetPaused ? 'Internet đang tạm dừng' : 'Internet hoạt động'}
    </span>
  </div>

  <button
    onClick={() => setShowPauseConfirm(true)}
    disabled={pauseMutation.isPending}
    className={`
      px-3 py-1 text-xs rounded-md font-medium transition-colors disabled:opacity-50
      ${child.internetPaused
        ? 'bg-green-500/10 text-green-600 dark:text-green-400 border border-green-500/30 hover:bg-green-500/20'
        : 'bg-red-500/10 text-red-600 dark:text-red-400 border border-red-500/30 hover:bg-red-500/20'
      }
    `}
  >
    {child.internetPaused ? '▶ Bật lại' : '⏸ Tạm dừng'}
  </button>
</div>

{/* Confirm Modal */}
<ConfirmModal
  isOpen={showPauseConfirm}
  onClose={() => setShowPauseConfirm(false)}
  onConfirm={() => {
    pauseMutation.mutate();
    setShowPauseConfirm(false);
  }}
  title={child.internetPaused ? 'Bật lại Internet' : 'Tạm dừng Internet'}
  message={
    child.internetPaused
      ? `Bật lại kết nối internet cho ${child.fullName}?`
      : `Tạm dừng toàn bộ truy cập web cho ${child.fullName}? Mọi trang đều bị chặn cho đến khi bạn bật lại.`
  }
  variant={child.internetPaused ? 'default' : 'warning'}
  confirmText={child.internetPaused ? 'Bật lại' : 'Tạm dừng'}
/>
```

---

### BƯỚC 4 — EXTENSION

> ✅ **Không cần sửa gì trong Extension.**
>
> **Giải thích:**
> - Khi `internet_paused = 1`, backend trả `allowed=false` ở endpoint `/check` → extension redirect blocked.html như bình thường
> - Khi heartbeat 30s, backend trả `limitExceeded=true` → extension gọi `blockTab()` như bình thường
> - Cả 2 luồng này đã hoạt động ổn định, không cần thêm bất kỳ logic nào vào extension
>
> **Tuyệt đối KHÔNG động vào background.js.**

---

## Checklist cuối — Trước khi test

### Feature 2 — Request Access
- [ ] SQL đã chạy: bảng `access_requests` tồn tại, cột `allowed_websites.temp_expires_at` tồn tại
- [ ] Entity `AccessRequest` đã thêm vào DbContext
- [ ] Entity `AllowedWebsite` có property `TempExpiresAt`
- [ ] `IAccessRequestService` và `AccessRequestService` đã tạo
- [ ] `IAccessRequestService` đăng ký DI trong Program.cs
- [ ] `POST /api/extension/request-access` hoạt động với Google Token auth
- [ ] `GET /api/access-requests` trả đúng danh sách pending cho guardian
- [ ] `PATCH /api/access-requests/{id}/respond` xử lý đủ 3 action (approve_temp, approve_permanent, reject)
- [ ] SignalR event `"AccessRequest"` được emit khi child gửi request
- [ ] Notification được tạo trong DB khi child gửi request
- [ ] BackgroundService cleanup `temp_expires_at` chạy định kỳ
- [ ] Frontend: `useExtensionMonitor` có handler `"AccessRequest"`
- [ ] Frontend: `AccessRequestCard` hiện đúng 3 nút action
- [ ] Frontend: badge count trên notification bell cộng thêm pending requests
- [ ] Frontend: section pending requests hiện TRƯỚC notification thường
- [ ] Extension: nút "Gửi yêu cầu" hiện trong `blocked.html`
- [ ] Extension: key lưu Google Token trong `blocked.js` khớp với `background.js`
- [ ] Extension: URL param lấy domain trong `blocked.js` khớp với cách tạo blocked URL trong `background.js`
- [ ] Test end-to-end: gửi → guardian nhận toast → approve_temp → child vào được trong 30 phút → sau 30 phút tự hết

### Feature 3 — Kill Switch
- [ ] SQL đã chạy: cột `users.internet_paused` tồn tại, SP `sp_GetGuardianChildren` đã có cột `internet_paused`
- [ ] Entity `User` có property `InternetPaused`
- [ ] DTO child (map từ SP) có field `InternetPaused`
- [ ] `PATCH /api/children/{childId}/pause-internet` toggle đúng
- [ ] `CheckAccessAsync` check `internet_paused` TRƯỚC khi gọi SP
- [ ] `UpdateHeartbeatAsync` check `internet_paused` TRƯỚC mọi logic website
- [ ] TypeScript interface có `internetPaused: boolean`
- [ ] API function `togglePauseInternet` đã thêm
- [ ] Frontend: toggle button và indicator hiện đúng trạng thái
- [ ] Frontend: `ConfirmModal` variant `warning` hiện trước khi tạm dừng
- [ ] Frontend: invalidate query `children` sau khi toggle
- [ ] Test: bật kill switch → mở tab mới bị blocked.html ngay → trong 30s heartbeat block tab đang mở

### Feature 1 — DB Indexing
- [ ] Chạy `SHOW INDEX FROM web_access_logs;` — thấy `idx_wal_child_session` và `idx_wal_access_result`
- [ ] Chạy `SHOW INDEX FROM daily_usage_stats;` — thấy `idx_dus_child_date`

---

## Lưu ý về Dark Mode

Tất cả component mới phải dùng CSS variables, KHÔNG hardcode màu:
```
bg-bg-base, bg-bg-surface, bg-bg-elevated, bg-bg-subtle, bg-bg-muted
text-tx-primary, text-tx-secondary
border-border-base
brand-DEFAULT (violet)
```

Màu status dùng opacity classes Tailwind để hoạt động ở cả 2 mode:
```
bg-green-500/10  text-green-600 dark:text-green-400
bg-red-500/10    text-red-600   dark:text-red-400
bg-amber-500/10  text-amber-600 dark:text-amber-400
```

Sidebar giữ tối ở cả 2 mode — không đặt component mới vào sidebar.
