# Family Guardian — Cải tiến Modal Chụp ảnh + Hẹn giờ + Xóa ảnh (Phần 15)

> **Ngày tạo:** 2026-05-22
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE_12.md (Phần 12)

---

## ⚠️ Quy tắc bất di bất dịch

| Quy tắc | Chi tiết |
|---------|---------|
| `sp_ExtensionCheckAccess` | Tuyệt đối KHÔNG sửa |
| Block/heartbeat/ping/poll logic background.js | KHÔNG thay đổi |
| Logic hiện tại | KHÔNG thay đổi, chỉ bổ sung |
| Dark mode | CSS variables: `bg-bg-surface`, `text-tx-primary`, `border-border-base`, `brand-DEFAULT` |

---

## SQL đã chạy xong, làm tiếp phần dưới

```sql
-- Bảng hẹn giờ chụp ảnh
CREATE TABLE scheduled_screenshots (
  id                 INT AUTO_INCREMENT PRIMARY KEY,
  child_id           INT NOT NULL,
  allowed_website_id INT NULL,
  domain             VARCHAR(255) NOT NULL,
  scheduled_at       DATETIME NOT NULL,
  requested_by       INT NOT NULL,
  status             ENUM('pending','executed','cancelled') NOT NULL DEFAULT 'pending',
  screenshot_id      INT NULL,
  created_at         DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT fk_ss_child    FOREIGN KEY (child_id)           REFERENCES users(id) ON DELETE CASCADE,
  CONSTRAINT fk_ss_website  FOREIGN KEY (allowed_website_id) REFERENCES allowed_websites(id) ON DELETE SET NULL,
  CONSTRAINT fk_ss_guardian FOREIGN KEY (requested_by)       REFERENCES users(id),
  CONSTRAINT fk_ss_shot     FOREIGN KEY (screenshot_id)      REFERENCES website_screenshots(id) ON DELETE SET NULL,
  INDEX idx_ss_pending (status, scheduled_at),
  INDEX idx_ss_child   (child_id, domain)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
```



---

## Tổng quan thay đổi

| # | Phần | Nội dung |
|---|------|---------|
| 1 | Backend Entity | `ScheduledScreenshot.cs` |
| 2 | Backend Service | Thêm vào `IScreenshotService` + `ScreenshotService` |
| 3 | Backend Controller | Thêm endpoints vào `ChildrenController` |
| 4 | Backend Quartz Job | `ExecuteScheduledScreenshotsJob.cs` |
| 5 | Frontend API | Thêm functions vào `childrenApi.ts` |
| 6 | Frontend Modal | Redesign `ScreenshotModal.tsx` hoàn toàn |
| 7 | Extension | Không thay đổi |

---

## BƯỚC 1 — Backend: Entity `ScheduledScreenshot`

### 1.1 Kiểm tra trước
Mở `Models/`. Xem có file nào liên quan scheduled screenshot chưa.

### 1.2 Tạo `Models/ScheduledScreenshot.cs`

```csharp
using System.ComponentModel.DataAnnotations.Schema;

namespace FamilyGuardian.Api.Models;

[Table("scheduled_screenshots")]
public class ScheduledScreenshot
{
    [Column("id")]
    public int Id { get; set; }

    [Column("child_id")]
    public int ChildId { get; set; }

    [Column("allowed_website_id")]
    public int? AllowedWebsiteId { get; set; }

    [Column("domain")]
    public string Domain { get; set; } = string.Empty;

    [Column("scheduled_at")]
    public DateTime ScheduledAt { get; set; }

    [Column("requested_by")]
    public int RequestedBy { get; set; }

    [Column("status")]
    public string Status { get; set; } = "pending"; // pending|executed|cancelled

    [Column("screenshot_id")]
    public int? ScreenshotId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [ForeignKey("ChildId")]
    public User? Child { get; set; }
}
```

### 1.3 Thêm vào `AppDbContext.cs`

```csharp
public DbSet<ScheduledScreenshot> ScheduledScreenshots { get; set; }
```

---

## BƯỚC 2 — Backend: Mở rộng `IScreenshotService`

### 2.1 Kiểm tra trước
Mở `Services/IScreenshotService.cs` (đã tạo từ Guide 11). Đọc toàn bộ.

### 2.2 Thêm vào interface (KHÔNG thay đổi method hiện có)

```csharp
// Thêm vào IScreenshotService:
Task<bool> DeleteScreenshotAsync(int guardianId, int childId, int screenshotId);
Task<int> ScheduleScreenshotAsync(int guardianId, int childId, string domain, DateTime scheduledAt);
Task<List<ScheduledScreenshotDto>> GetScheduledAsync(int guardianId, int childId, string domain);
Task<bool> CancelScheduledAsync(int guardianId, int scheduleId);
Task ExecutePendingScheduledAsync(); // gọi bởi Quartz job

// Thêm DTO:
public class ScheduledScreenshotDto
{
    public int Id { get; set; }
    public string Domain { get; set; } = string.Empty;
    public DateTime ScheduledAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? ScreenshotId { get; set; }
}
```

### 2.3 Implement trong `ScreenshotService.cs`

Thêm vào cuối class (KHÔNG thay đổi method hiện có):

```csharp
public async Task<bool> DeleteScreenshotAsync(int guardianId, int childId, int screenshotId)
{
    var shot = await _context.WebsiteScreenshots.FindAsync(screenshotId);
    if (shot == null || shot.ChildId != childId || shot.RequestedBy != guardianId)
        return false;

    // Xóa file vật lý nếu có
    if (!string.IsNullOrEmpty(shot.ImagePath))
    {
        var baseDir = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        var fullPath = Path.Combine(baseDir, shot.ImagePath.TrimStart('/'));
        if (File.Exists(fullPath)) File.Delete(fullPath);
    }

    _context.WebsiteScreenshots.Remove(shot);
    await _context.SaveChangesAsync();
    return true;
}

public async Task<int> ScheduleScreenshotAsync(
    int guardianId, int childId, string domain, DateTime scheduledAt)
{
    var hasRelation = await _context.GuardianChildRelationships
        .AsNoTracking()
        .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
    if (!hasRelation) return -1;

    var website = await _context.AllowedWebsites
        .AsNoTracking()
        .FirstOrDefaultAsync(w => w.ChildId == childId && w.Domain == domain && w.IsActive);

    var schedule = new ScheduledScreenshot
    {
        ChildId           = childId,
        AllowedWebsiteId  = website?.Id,
        Domain            = domain,
        ScheduledAt       = scheduledAt,
        RequestedBy       = guardianId,
        Status            = "pending",
        CreatedAt         = DateTime.Now
    };

    _context.ScheduledScreenshots.Add(schedule);
    await _context.SaveChangesAsync();

    _logger.LogInformation(
        "Screenshot scheduled: Id={Id}, ChildId={ChildId}, Domain={Domain}, At={At}",
        schedule.Id, childId, domain, scheduledAt);

    return schedule.Id;
}

public async Task<List<ScheduledScreenshotDto>> GetScheduledAsync(
    int guardianId, int childId, string domain)
{
    var hasRelation = await _context.GuardianChildRelationships
        .AsNoTracking()
        .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);
    if (!hasRelation) return [];

    return await _context.ScheduledScreenshots
        .AsNoTracking()
        .Where(s => s.ChildId == childId
                 && s.Domain  == domain
                 && s.Status  == "pending"
                 && s.ScheduledAt >= DateTime.Now)
        .OrderBy(s => s.ScheduledAt)
        .Select(s => new ScheduledScreenshotDto
        {
            Id           = s.Id,
            Domain       = s.Domain,
            ScheduledAt  = s.ScheduledAt,
            Status       = s.Status,
            ScreenshotId = s.ScreenshotId
        })
        .ToListAsync();
}

public async Task<bool> CancelScheduledAsync(int guardianId, int scheduleId)
{
    var schedule = await _context.ScheduledScreenshots.FindAsync(scheduleId);
    if (schedule == null || schedule.RequestedBy != guardianId) return false;

    schedule.Status = "cancelled";
    await _context.SaveChangesAsync();
    return true;
}

public async Task ExecutePendingScheduledAsync()
{
    var now = DateTime.Now;
    var pending = await _context.ScheduledScreenshots
        .Where(s => s.Status == "pending" && s.ScheduledAt <= now)
        .ToListAsync();

    foreach (var schedule in pending)
    {
        try
        {
            var result = await RequestScreenshotAsync(
                schedule.RequestedBy, schedule.ChildId, schedule.Domain);

            schedule.Status       = "executed";
            schedule.ScreenshotId = result.ScreenshotId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute scheduled screenshot {Id}", schedule.Id);
            schedule.Status = "cancelled";
        }
    }

    if (pending.Count > 0)
        await _context.SaveChangesAsync();
}
```

---

## BƯỚC 3 — Backend: Endpoints mới

### 3.1 Kiểm tra trước
Mở `ChildrenController.cs`. Xem constructor hiện tại. Kiểm tra cách lấy `guardianId`.

### 3.2 Thêm vào `ChildrenController.cs` (KHÔNG thay đổi gì cũ)

```csharp
// ── Xóa ảnh ──
[HttpDelete("{childId}/screenshots/{screenshotId}")]
[Authorize(Roles = "guardian,admin")]
public async Task<IActionResult> DeleteScreenshot(int childId, int screenshotId)
{
    var guardianId = GetCurrentUserId(); // dùng đúng cách hiện tại
    var ok = await _screenshotService.DeleteScreenshotAsync(guardianId, childId, screenshotId);
    return ok ? Ok() : NotFound();
}

// ── Hẹn giờ chụp ──
[HttpPost("{childId}/schedule-screenshot")]
[Authorize(Roles = "guardian,admin")]
public async Task<IActionResult> ScheduleScreenshot(
    int childId, [FromBody] ScheduleScreenshotDto dto)
{
    if (dto.ScheduledAt <= DateTime.Now)
        return BadRequest("Thời gian hẹn phải trong tương lai");

    var guardianId = GetCurrentUserId();
    var id = await _screenshotService.ScheduleScreenshotAsync(
        guardianId, childId, dto.Domain, dto.ScheduledAt);

    return id > 0
        ? Ok(new { scheduleId = id, message = "Đã hẹn giờ chụp ảnh" })
        : BadRequest("Không có quyền");
}

// ── Lấy danh sách hẹn giờ pending ──
[HttpGet("{childId}/scheduled-screenshots")]
[Authorize(Roles = "guardian,admin")]
public async Task<IActionResult> GetScheduled(
    int childId, [FromQuery] string domain)
{
    if (string.IsNullOrWhiteSpace(domain)) return BadRequest("domain required");
    var guardianId = GetCurrentUserId();
    var list = await _screenshotService.GetScheduledAsync(guardianId, childId, domain);
    return Ok(list);
}

// ── Hủy hẹn giờ ──
[HttpDelete("{childId}/scheduled-screenshots/{scheduleId}")]
[Authorize(Roles = "guardian,admin")]
public async Task<IActionResult> CancelScheduled(int childId, int scheduleId)
{
    var guardianId = GetCurrentUserId();
    var ok = await _screenshotService.CancelScheduledAsync(guardianId, scheduleId);
    return ok ? Ok() : NotFound();
}

// ── DTO ──
public class ScheduleScreenshotDto
{
    public string Domain { get; set; } = string.Empty;
    public DateTime ScheduledAt { get; set; }
}
```

---

## BƯỚC 4 — Backend: Quartz Job

### 4.1 Kiểm tra trước
Mở `Jobs/` (hoặc thư mục chứa Quartz jobs). Đọc một job hiện tại để biết pattern đang dùng (ví dụ `SendScheduledNotificationsJob.cs`). Dùng đúng pattern đó.

### 4.2 Tạo `Jobs/ExecuteScheduledScreenshotsJob.cs`

```csharp
using FamilyGuardian.Api.Services;
using Quartz;

namespace FamilyGuardian.Api.Jobs;

[DisallowConcurrentExecution]
public class ExecuteScheduledScreenshotsJob : IJob
{
    private readonly IScreenshotService _screenshotService;
    private readonly ILogger<ExecuteScheduledScreenshotsJob> _logger;

    public ExecuteScheduledScreenshotsJob(
        IScreenshotService screenshotService,
        ILogger<ExecuteScheduledScreenshotsJob> logger)
    {
        _screenshotService = screenshotService;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogDebug("ExecuteScheduledScreenshotsJob running");
        await _screenshotService.ExecutePendingScheduledAsync();
    }
}
```

### 4.3 Đăng ký job trong `Program.cs`

Tìm đoạn đăng ký Quartz jobs hiện tại (dạng `q.AddJob<...>()`). Thêm vào:

```csharp
q.AddJob<ExecuteScheduledScreenshotsJob>(opts => opts.WithIdentity("ExecuteScheduledScreenshotsJob"));
q.AddTrigger(opts => opts
    .ForJob("ExecuteScheduledScreenshotsJob")
    .WithIdentity("ExecuteScheduledScreenshots-trigger")
    .WithCronSchedule("0 * * * * ?")  // mỗi phút kiểm tra 1 lần
);
```

---

## BƯỚC 5 — Frontend: API functions

### 5.1 Kiểm tra trước
Mở `src/api/childrenApi.ts`. Xem các functions hiện có (đặc biệt từ Guide 11/12).

### 5.2 Thêm vào cuối file (KHÔNG thay đổi gì cũ)

```typescript
// ── Xóa screenshot ──
export const deleteScreenshot = async (childId: number, screenshotId: number) => {
  await api.delete(`/children/${childId}/screenshots/${screenshotId}`);
};

// ── Hẹn giờ chụp ──
export interface ScheduledScreenshotDto {
  id: number;
  domain: string;
  scheduledAt: string;
  status: string;
  screenshotId: number | null;
}

export const scheduleScreenshot = async (
  childId: number, domain: string, scheduledAt: string
) => {
  const res = await api.post<{ scheduleId: number; message: string }>(
    `/children/${childId}/schedule-screenshot`,
    { domain, scheduledAt }
  );
  return res.data;
};

export const getScheduledScreenshots = async (childId: number, domain: string) => {
  const res = await api.get<ScheduledScreenshotDto[]>(
    `/children/${childId}/scheduled-screenshots`,
    { params: { domain } }
  );
  return res.data;
};

export const cancelScheduledScreenshot = async (childId: number, scheduleId: number) => {
  await api.delete(`/children/${childId}/scheduled-screenshots/${scheduleId}`);
};
```

---

## BƯỚC 6 — Frontend: Redesign `ScreenshotModal.tsx`

### 6.1 Kiểm tra trước
Mở `components/ScreenshotModal.tsx` (tạo từ Guide 12).
Đọc toàn bộ. Ghi nhớ:
- Import paths đang dùng
- Cách dùng `toast`
- CSS variable pattern

### 6.2 Viết lại hoàn toàn

> ⚠️ Đây là file mới hoàn toàn thay thế file từ Guide 12.
> Kiểm tra import alias (`@/`) và `toast` import đúng với project trước khi dùng.

```tsx
import { useState, useEffect, useRef } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  requestScreenshot,
  getScreenshots,
  deleteScreenshot,
  scheduleScreenshot,
  getScheduledScreenshots,
  cancelScheduledScreenshot,
  type ScreenshotDto,
  type ScheduledScreenshotDto,
} from '@/api/childrenApi';
// import toast từ đúng store của project

interface ScreenshotModalProps {
  childId: number;
  domain: string;
  websiteName: string;
  onClose: () => void;
}

type TimeFilter   = 'all' | 'today' | 'week' | 'month';
type FailFilter   = 'all' | 'tab_not_found' | 'failed';

const TIME_FILTER_LABELS: Record<TimeFilter, string> = {
  all: 'Tất cả', today: 'Hôm nay', week: '7 ngày', month: '30 ngày'
};

export default function ScreenshotModal({
  childId, domain, websiteName, onClose
}: ScreenshotModalProps) {
  const queryClient = useQueryClient();

  // ── State ──
  const [timeFilter, setTimeFilter]     = useState<TimeFilter>('all');
  const [failFilter, setFailFilter]     = useState<FailFilter>('all');
  const [selectedImage, setSelectedImage] = useState<ScreenshotDto | null>(null);
  const [showSchedule, setShowSchedule] = useState(false);
  const [scheduleDate, setScheduleDate] = useState('');
  const [scheduleTime, setScheduleTime] = useState('');
  const [isTakingShot, setIsTakingShot] = useState(false);

  // ── Queries ──
  const { data: screenshots = [], isLoading } = useQuery({
    queryKey: ['screenshots', childId, domain, 'modal'],
    queryFn: () => getScreenshots(childId, domain, 100),
    refetchInterval: 5000,
  });

  const { data: scheduled = [] } = useQuery({
    queryKey: ['scheduled-screenshots', childId, domain],
    queryFn: () => getScheduledScreenshots(childId, domain),
    refetchInterval: 30000,
  });

  // ── Mutations ──
  const requestMutation = useMutation({
    mutationFn: () => requestScreenshot(childId, domain),
    onMutate: () => setIsTakingShot(true),
    onSuccess: () => {
      // Không toast ở đây — đợi SignalR ScreenshotReady mới toast
      queryClient.invalidateQueries({ queryKey: ['screenshots', childId, domain] });
      setTimeout(() => setIsTakingShot(false), 8000); // tắt spinner sau 8s
    },
    onError: () => {
      toast.error('Không thể gửi yêu cầu chụp ảnh');
      setIsTakingShot(false);
    },
  });

  const scheduleMutation = useMutation({
    mutationFn: () => {
      const dt = new Date(`${scheduleDate}T${scheduleTime}`);
      return scheduleScreenshot(childId, domain, dt.toISOString());
    },
    onSuccess: () => {
      toast.success('Đã hẹn giờ chụp ảnh');
      setShowSchedule(false);
      setScheduleDate('');
      setScheduleTime('');
      queryClient.invalidateQueries({ queryKey: ['scheduled-screenshots', childId, domain] });
    },
    onError: () => toast.error('Không thể hẹn giờ'),
  });

  const deleteMutation = useMutation({
    mutationFn: (screenshotId: number) => deleteScreenshot(childId, screenshotId),
    onSuccess: () => {
      toast.delete('Đã xóa ảnh');
      if (selectedImage) setSelectedImage(null);
      queryClient.invalidateQueries({ queryKey: ['screenshots', childId, domain] });
    },
    onError: () => toast.error('Xóa thất bại'),
  });

  const cancelScheduleMutation = useMutation({
    mutationFn: (scheduleId: number) => cancelScheduledScreenshot(childId, scheduleId),
    onSuccess: () => {
      toast.delete('Đã hủy lịch chụp');
      queryClient.invalidateQueries({ queryKey: ['scheduled-screenshots', childId, domain] });
    },
  });

  // ── Filter logic ──
  const filterByTime = (list: ScreenshotDto[]) => {
    if (timeFilter === 'all') return list;
    const now = new Date();
    return list.filter(s => {
      const d = new Date(s.capturedAt);
      if (timeFilter === 'today') return d.toDateString() === now.toDateString();
      if (timeFilter === 'week')  return d >= new Date(now.getTime() - 7  * 864e5);
      if (timeFilter === 'month') return d >= new Date(now.getTime() - 30 * 864e5);
      return true;
    });
  };

  const allFiltered   = filterByTime(screenshots);
  const capturedList  = allFiltered.filter(s => s.status === 'captured');
  const failedAll     = allFiltered.filter(s => s.status !== 'captured' && s.status !== 'pending');
  const failedVisible = failFilter === 'all'
    ? failedAll
    : failedAll.filter(s => s.status === failFilter);

  // ── Keyboard ──
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        if (selectedImage) setSelectedImage(null);
        else if (showSchedule) setShowSchedule(false);
        else onClose();
      }
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [selectedImage, showSchedule, onClose]);

  // ── Min datetime cho schedule picker ──
  const minDateTime = new Date(Date.now() + 60000).toISOString().slice(0, 16);

  return (
    <>
      {/* Overlay */}
      <div className="fixed inset-0 z-40 bg-black/60 backdrop-blur-sm" onClick={onClose}/>

      {/* Modal */}
      <div
        className="fixed inset-0 z-50 flex items-center justify-center p-4 pointer-events-none"
      >
        <div
          className="relative w-full max-w-3xl max-h-[90vh] rounded-2xl overflow-hidden
                     bg-bg-surface border border-border-base shadow-2xl flex flex-col
                     pointer-events-auto"
          onClick={e => e.stopPropagation()}
        >
          {/* ── Header ── */}
          <div className="flex items-center justify-between px-5 py-4
                          border-b border-border-base shrink-0">
            <div>
              <h2 className="text-sm font-semibold text-tx-primary flex items-center gap-1.5">
                <svg className="w-4 h-4 text-brand-DEFAULT" fill="none" viewBox="0 0 24 24"
                     stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                    d="M3 9a2 2 0 012-2h.93a2 2 0 001.664-.89l.812-1.22A2 2 0 0110.07 4h3.86a2 2 0 011.664.89l.812 1.22A2 2 0 0018.07 7H19a2 2 0 012 2v9a2 2 0 01-2 2H5a2 2 0 01-2-2V9z"/>
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                        d="M15 13a3 3 0 11-6 0 3 3 0 016 0z"/>
                </svg>
                Ảnh chụp màn hình
              </h2>
              <p className="text-xs text-tx-secondary mt-0.5">{websiteName}</p>
            </div>

            <div className="flex items-center gap-2">
              {/* Nút Hẹn giờ chụp */}
              <button
                onClick={() => {
                  if (!showSchedule) {
                    // Pre-fill ngày giờ hiện tại khi mở panel
                    const now = new Date();
                    setScheduleDate(now.toISOString().slice(0, 10));
                    setScheduleTime(now.toTimeString().slice(0, 5));
                  }
                  setShowSchedule(s => !s);
                }}
                className={`flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-medium
                           transition-colors border
                           ${showSchedule
                             ? 'bg-brand-DEFAULT/20 text-brand-DEFAULT border-brand-DEFAULT/40'
                             : 'bg-bg-subtle text-tx-secondary border-border-base hover:text-tx-primary'
                           }`}
              >
                <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                    d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z"/>
                </svg>
                Hẹn giờ
              </button>

              {/* Nút Chụp ngay */}
              <button
                onClick={() => requestMutation.mutate()}
                disabled={requestMutation.isPending || isTakingShot}
                className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-medium
                           bg-brand-DEFAULT text-white hover:bg-brand-DEFAULT/90
                           transition-colors disabled:opacity-60 disabled:cursor-not-allowed"
              >
                {isTakingShot ? (
                  <>
                    <svg className="w-3.5 h-3.5 animate-spin" fill="none" viewBox="0 0 24 24">
                      <circle className="opacity-25" cx="12" cy="12" r="10"
                              stroke="currentColor" strokeWidth="4"/>
                      <path className="opacity-75" fill="currentColor"
                            d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"/>
                    </svg>
                    Đang chụp...
                  </>
                ) : (
                  <>
                    <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                        d="M3 9a2 2 0 012-2h.93a2 2 0 001.664-.89l.812-1.22A2 2 0 0110.07 4h3.86a2 2 0 011.664.89l.812 1.22A2 2 0 0018.07 7H19a2 2 0 012 2v9a2 2 0 01-2 2H5a2 2 0 01-2-2V9z"/>
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                            d="M15 13a3 3 0 11-6 0 3 3 0 016 0z"/>
                    </svg>
                    Chụp ngay
                  </>
                )}
              </button>

              {/* Nút đóng */}
              <button
                onClick={onClose}
                className="p-1.5 rounded-lg text-tx-secondary hover:text-tx-primary
                           hover:bg-bg-subtle transition-colors"
              >
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                        d="M6 18L18 6M6 6l12 12"/>
                </svg>
              </button>
            </div>
          </div>

          {/* ── Panel hẹn giờ (collapsible) ── */}
          {showSchedule && (
            <div className="px-5 py-3 border-b border-border-base bg-bg-subtle shrink-0">
              <p className="text-xs font-medium text-tx-primary mb-2">
                📅 Chọn thời gian chụp tự động
              </p>
              <div className="flex items-center gap-2 flex-wrap">
                <input
                  type="date"
                  value={scheduleDate}
                  min={new Date().toISOString().slice(0, 10)}
                  onChange={e => setScheduleDate(e.target.value)}
                  className="px-3 py-1.5 rounded-lg text-xs border border-border-base
                             bg-bg-surface text-tx-primary focus:outline-none
                             focus:border-brand-DEFAULT"
                />
                <input
                  type="time"
                  value={scheduleTime}
                  onChange={e => setScheduleTime(e.target.value)}
                  className="px-3 py-1.5 rounded-lg text-xs border border-border-base
                             bg-bg-surface text-tx-primary focus:outline-none
                             focus:border-brand-DEFAULT"
                />
                <button
                  onClick={() => scheduleMutation.mutate()}
                  disabled={!scheduleDate || !scheduleTime || scheduleMutation.isPending}
                  className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs
                             font-medium bg-brand-DEFAULT text-white
                             hover:bg-brand-DEFAULT/90 transition-colors
                             disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                          d="M5 13l4 4L19 7"/>
                  </svg>
                  Xác nhận
                </button>
              </div>

              {/* Danh sách lịch đang pending */}
              {scheduled.length > 0 && (
                <div className="mt-3 space-y-1.5">
                  <p className="text-xs text-tx-secondary">Lịch chụp đang chờ:</p>
                  {scheduled.map(s => (
                    <div key={s.id}
                         className="flex items-center justify-between px-3 py-1.5
                                    rounded-lg bg-bg-surface border border-border-base">
                      <span className="text-xs text-tx-primary">
                        📅 {new Date(s.scheduledAt).toLocaleString('vi-VN')}
                      </span>
                      <button
                        onClick={() => cancelScheduleMutation.mutate(s.id)}
                        className="text-xs text-red-400 hover:text-red-300 transition-colors ml-2"
                      >
                        Hủy
                      </button>
                    </div>
                  ))}
                </div>
              )}
            </div>
          )}

          {/* ── Filter thời gian + stats ── */}
          <div className="flex items-center gap-2 px-5 py-2.5
                          border-b border-border-base shrink-0 flex-wrap">
            <svg className="w-3.5 h-3.5 text-tx-secondary shrink-0" fill="none"
                 viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                d="M3 4a1 1 0 011-1h16a1 1 0 011 1v2a1 1 0 01-.293.707L13 13.414V19a1 1 0 01-.553.894l-4 2A1 1 0 017 21v-7.586L3.293 6.707A1 1 0 013 6V4z"/>
            </svg>
            <span className="text-xs text-tx-secondary">Lọc:</span>
            {(Object.keys(TIME_FILTER_LABELS) as TimeFilter[]).map(f => (
              <button
                key={f}
                onClick={() => setTimeFilter(f)}
                className={`px-2.5 py-1 rounded-full text-xs font-medium transition-colors
                  ${timeFilter === f
                    ? 'bg-brand-DEFAULT text-white'
                    : 'bg-bg-subtle text-tx-secondary hover:text-tx-primary'
                  }`}
              >
                {TIME_FILTER_LABELS[f]}
              </button>
            ))}
            <span className="ml-auto text-xs text-tx-secondary">
              {capturedList.length} ảnh
              {failedAll.length > 0 && (
                <span className="ml-1 text-red-400/70">· {failedAll.length} lỗi</span>
              )}
            </span>
          </div>

          {/* ── Body ── */}
          <div className="flex-1 overflow-y-auto p-5">

            {isLoading && (
              <div className="flex items-center justify-center py-16">
                <svg className="w-6 h-6 animate-spin text-brand-DEFAULT"
                     fill="none" viewBox="0 0 24 24">
                  <circle className="opacity-25" cx="12" cy="12" r="10"
                          stroke="currentColor" strokeWidth="4"/>
                  <path className="opacity-75" fill="currentColor"
                        d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"/>
                </svg>
              </div>
            )}

            {/* ── Section thất bại (nếu có) ── */}
            {failedAll.length > 0 && (
              <div className="mb-5">
                <div className="flex items-center gap-2 mb-2 flex-wrap">
                  <p className="text-xs font-medium text-tx-secondary uppercase tracking-wide">
                    Không thành công ({failedAll.length})
                  </p>
                  {/* Filter chips cho failed */}
                  <div className="flex items-center gap-1.5 ml-1">
                    {([
                      { key: 'all',           label: 'Tất cả' },
                      { key: 'tab_not_found', label: 'Chưa mở tab' },
                      { key: 'failed',        label: 'Lỗi khác' },
                    ] as { key: FailFilter; label: string }[]).map(f => (
                      <button
                        key={f.key}
                        onClick={() => setFailFilter(f.key)}
                        className={`px-2 py-0.5 rounded-full text-xs transition-colors
                          ${failFilter === f.key
                            ? 'bg-red-500/20 text-red-400 border border-red-500/30'
                            : 'bg-bg-subtle text-tx-secondary hover:text-tx-primary'
                          }`}
                      >
                        {f.label}
                      </button>
                    ))}
                  </div>
                </div>

                <div className="space-y-1.5">
                  {failedVisible.map(s => (
                    <div key={s.id}
                         className={`flex items-center justify-between px-3 py-2
                                     rounded-lg border text-xs
                           ${s.status === 'tab_not_found'
                             ? 'bg-yellow-500/6 border-yellow-500/20 text-yellow-500/80'
                             : 'bg-red-500/6 border-red-500/20 text-red-400/80'
                           }`}>
                      <div className="flex items-center gap-2">
                        <span>{s.status === 'tab_not_found' ? '⚠️' : '❌'}</span>
                        <div>
                          <p className="font-medium">
                            {s.status === 'tab_not_found' ? 'Con chưa mở website này' : 'Chụp thất bại'}
                          </p>
                          <p className="text-tx-secondary text-[11px]">
                            {new Date(s.capturedAt).toLocaleString('vi-VN')}
                          </p>
                        </div>
                      </div>
                      <button
                        onClick={() => deleteMutation.mutate(s.id)}
                        className="p-1 rounded hover:bg-white/10 transition-colors opacity-60
                                   hover:opacity-100 shrink-0 ml-2"
                        title="Xóa"
                      >
                        <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24"
                             stroke="currentColor">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                                d="M6 18L18 6M6 6l12 12"/>
                        </svg>
                      </button>
                    </div>
                  ))}
                </div>
              </div>
            )}

            {/* ── Ảnh đã chụp ── */}
            {!isLoading && capturedList.length === 0 && failedAll.length === 0 && (
              <div className="text-center py-16">
                <div className="text-4xl mb-3">📷</div>
                <p className="text-sm text-tx-secondary">Chưa có ảnh trong khoảng thời gian này</p>
                <p className="text-xs text-tx-secondary mt-1 opacity-70">
                  Nhấn "Chụp ngay" hoặc hẹn giờ để chụp ảnh màn hình của con
                </p>
              </div>
            )}

            {capturedList.length > 0 && (
              <div>
                <p className="text-xs font-medium text-tx-secondary uppercase tracking-wide mb-3">
                  Ảnh đã chụp ({capturedList.length})
                </p>
                <div className="grid grid-cols-2 sm:grid-cols-3 gap-3">
                  {capturedList.map(s => (
                    <div
                      key={s.id}
                      className="relative rounded-xl overflow-hidden border border-border-base
                                 cursor-pointer group hover:border-brand-DEFAULT/50
                                 transition-all hover:shadow-lg aspect-video bg-bg-subtle"
                    >
                      {/* Thumbnail */}
                      <img
                        src={s.imageUrl!}
                        alt="Screenshot"
                        onClick={() => setSelectedImage(s)}
                        className="w-full h-full object-cover object-top
                                   group-hover:scale-105 transition-transform duration-300"
                      />

                      {/* Timestamp */}
                      <div className="absolute bottom-0 left-0 right-0
                                      bg-gradient-to-t from-black/70 to-transparent
                                      px-2 py-1.5 pointer-events-none">
                        <p className="text-[11px] text-white/90">
                          {new Date(s.capturedAt).toLocaleString('vi-VN', {
                            hour: '2-digit', minute: '2-digit',
                            day: '2-digit',  month: '2-digit'
                          })}
                        </p>
                      </div>

                      {/* Nút xóa — hiện khi hover */}
                      <button
                        onClick={e => { e.stopPropagation(); deleteMutation.mutate(s.id); }}
                        className="absolute top-1.5 right-1.5 p-1 rounded-lg bg-black/60
                                   text-white opacity-0 group-hover:opacity-100
                                   hover:bg-red-500/80 transition-all"
                        title="Xóa ảnh"
                      >
                        <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24"
                             stroke="currentColor">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                            d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"/>
                        </svg>
                      </button>
                    </div>
                  ))}
                </div>
              </div>
            )}
          </div>
        </div>
      </div>

      {/* ── Lightbox full size ── */}
      {selectedImage && (
        <div
          className="fixed inset-0 z-[60] flex items-center justify-center
                     bg-black/92 backdrop-blur-sm p-4"
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
              <div className="flex items-center gap-2">
                {/* Xóa */}
                <button
                  onClick={() => deleteMutation.mutate(selectedImage.id)}
                  className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg
                             bg-red-500/20 hover:bg-red-500/40 text-white text-xs
                             font-medium transition-colors border border-red-500/30"
                >
                  <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                      d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"/>
                  </svg>
                  Xóa
                </button>
                {/* Tải xuống */}
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

---

## BƯỚC 7 — Extension (chỉ verify, KHÔNG thay đổi)

Mở `background.js`. Verify:
- `screenshot_poll` alarm đang chạy (từ Guide 12) → KHÔNG thay đổi
- `captureScreenshotForDomain`, `uploadScreenshot`, `reportScreenshotResult` → KHÔNG thay đổi

---

## Thứ tự làm việc

```
B1  — Chạy SQL tạo bảng scheduled_screenshots
B2  — Backend: Tạo ScheduledScreenshot.cs entity
B3  — Backend: Thêm DbSet vào AppDbContext
B4  — Backend: Thêm 5 method mới vào IScreenshotService + implement
B5  — Backend: Thêm 4 endpoints vào ChildrenController
B6  — Backend: Tạo ExecuteScheduledScreenshotsJob.cs
B7  — Backend: Đăng ký job trong Program.cs (cron mỗi phút)
B8  — Frontend: Thêm 4 API functions vào childrenApi.ts
B9  — Frontend: Thay toàn bộ ScreenshotModal.tsx bằng file mới
B10 — Test toàn bộ flow
```

---

## Checklist kiểm tra trước khi viết code

### Backend
- [ ] Namespace chính xác?
- [ ] `GetCurrentUserId()` — đúng method trong ChildrenController?
- [ ] Pattern Quartz job trong project (xem `SendScheduledNotificationsJob.cs`)?
- [ ] Cron `"0 * * * * ?"` là mỗi phút — Quartz dùng 6-field cron (có second)?
- [ ] `RequestScreenshotAsync` đã tồn tại từ Guide 11?

### Frontend
- [ ] Import alias `@/` hay relative path?
- [ ] `toast.delete(...)` đúng tên method?
- [ ] `getScreenshots` nhận `limit = 100` được không (kiểm tra API)?
- [ ] `WebsiteCard.tsx` truyền `childId` xuống `ScreenshotModal` đúng chưa?

---

## Test

```
TEST 1 — Chụp ngay
Click "Chụp ngay" → nút đổi thành "Đang chụp..." spinner
→ ~5s sau ảnh xuất hiện trong lưới, nút trở lại bình thường

TEST 2 — Hẹn giờ chụp
Click "Hẹn giờ" → panel mở → chọn ngày + giờ → Xác nhận
→ Toast "Đã hẹn giờ chụp ảnh"
→ Lịch hiện trong danh sách pending
→ Đến giờ đó: ảnh tự xuất hiện trong modal

TEST 3 — Xóa ảnh
Hover vào thumbnail → nút xóa đỏ hiện ở góc phải
→ Click xóa → Toast "Đã xóa ảnh" → ảnh biến khỏi lưới
Hoặc: mở lightbox → click "Xóa" → tự đóng lightbox

TEST 4 — Filter thất bại
Có vài ảnh thất bại → filter chip "Chưa mở tab" → chỉ hiện tab_not_found
→ filter chip "Lỗi khác" → chỉ hiện failed

TEST 5 — Dark mode
Tất cả text, background, border dùng CSS variables
→ Không có màu hardcode nào trông lạ trong dark mode
```

---

## Trả lời câu hỏi

**Ảnh có còn khi xóa và tạo lại website không?**
→ **CÒN.** DB lưu theo `child_id + domain`. Xóa website → `allowed_website_id = NULL` nhưng ảnh không mất. Tạo lại website cùng domain → modal hiện lại ảnh cũ bình thường.

**AI nhận diện 18+ — ý kiến:**
→ Khả thi với **Google Cloud Vision SafeSearch API** (free 1000 ảnh/tháng).
→ Sau khi `SaveScreenshotAsync` lưu ảnh thành công → gọi API → nếu `adult = LIKELY/VERY_LIKELY` → tạo notification đặc biệt cho guardian.
→ Bật/tắt qua config (appsettings.json).
→ Tôi sẽ làm trong Guide 16 khi bạn sẵn sàng.
