# Family Guardian — Chụp ảnh Website Con đang dùng (Phần 11)

> **Ngày tạo:** 2026-05-20
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE_10.md (Phần 10)

---

## ⚠️ Quy tắc bất di bất dịch

| Quy tắc | Chi tiết |
|---------|---------|
| `sp_ExtensionCheckAccess` | Tuyệt đối KHÔNG sửa |
| `background.js` block/heartbeat logic | KHÔNG thay đổi — chỉ THÊM SignalR listener mới |
| Logic hiện tại | KHÔNG thay đổi, chỉ bổ sung |
| Dark mode | Dùng CSS variables: `bg-bg-surface`, `text-tx-primary`, `border-border-base`, `brand-DEFAULT` |
| Sidebar | Luôn tối ở cả 2 mode |

---

## ⚠️ BUG CẦN SỬA TRƯỚC — `reason` bị truncate khi con gửi yêu cầu

### Nguyên nhân gốc rễ

ENUM trong DB chỉ có 3 giá trị ban đầu (`not_in_whitelist`, `time_limit_exceeded`, `internet_paused`).
Extension đang gửi một giá trị thứ 4 (liên quan đến `outside_time_window`) nhưng string đó không khớp chính xác với bất kỳ ENUM value nào → MySQL reject → crash.

### SQL cần chạy ✅ (đã chạy xong)

```sql
ALTER TABLE access_requests
MODIFY COLUMN reason ENUM('not_in_whitelist','time_limit_exceeded','internet_paused','outside_time_window')
NOT NULL DEFAULT 'not_in_whitelist';
```

> ✅ SQL này đã chạy xong. Nhưng vẫn còn crash vì extension gửi string không khớp với ENUM.

### Bước tiếp theo — tìm string thực tế extension gửi

Mở `background.js` hoặc `blocked.html` trong extension. Tìm chỗ gọi API `/api/extension/request-access` (hoặc tương tự). Xem `reason` đang được gán giá trị gì khi block do `outside_time_window`.

Ví dụ có thể là: `"outsideTimeWindow"`, `"outside-time-window"`, `"time_window"`, `"outside_time_window"`, v.v.

### Sửa trong `AccessRequestService.cs` (dòng 33)

Sau khi biết chính xác string extension gửi, sửa dòng 33 thành:

```csharp
reason = string.IsNullOrWhiteSpace(reason) ? "not_in_whitelist" : reason.Trim().ToLowerInvariant();

// Map về đúng ENUM value — thay "EXTENSION_GỬI_GÌ" bằng string thực tế tìm được ở trên
reason = reason switch
{
    "time_limit_exceeded"  => "time_limit_exceeded",
    "internet_paused"      => "internet_paused",
    "outside_time_window"  => "outside_time_window",   // hoặc "EXTENSION_GỬI_GÌ" => "outside_time_window"
    _                      => "not_in_whitelist"
};
```

> ⚠️ KHÔNG thay đổi các logic khác trong file — chỉ thêm đoạn mapping này sau dòng 33.
> ⚠️ Giữ nguyên title/message switch bên dưới — logic đó vẫn chạy đúng.

### Cách debug nhanh nếu không tìm được trong code

Thêm log tạm vào `AccessRequestService.cs` dòng 34:

```csharp
_logger.LogInformation("AccessRequest reason received: '{Reason}'", reason);
```

Sau đó test gửi yêu cầu từ extension → xem log terminal → biết chính xác string → sửa mapping → xóa log tạm.

---

## SQL cần chạy (2 câu — chạy trước khi làm backend/frontend)

```sql
-- Fix bug: Đổi ENUM → VARCHAR(50) để chấp nhận bất kỳ reason string nào từ extension
-- (ENUM bị reject nếu extension gửi value không khớp chính xác)
-- Chạy câu này, BỎ QUA ALTER ENUM đã chạy trước đó
ALTER TABLE access_requests
MODIFY COLUMN reason VARCHAR(50) NOT NULL DEFAULT 'not_in_whitelist';

-- Bảng lưu screenshots
CREATE TABLE website_screenshots (
  id                 INT AUTO_INCREMENT PRIMARY KEY,
  child_id           INT NOT NULL,
  allowed_website_id INT NULL,
  domain             VARCHAR(255) NOT NULL,
  image_path         VARCHAR(500) NOT NULL DEFAULT '',
  captured_at        DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  requested_by       INT NOT NULL,
  status             ENUM('pending','captured','failed','tab_not_found') NOT NULL DEFAULT 'pending',
  error_message      VARCHAR(500) NULL,
  CONSTRAINT fk_ws_child    FOREIGN KEY (child_id)           REFERENCES users(id) ON DELETE CASCADE,
  CONSTRAINT fk_ws_website  FOREIGN KEY (allowed_website_id) REFERENCES allowed_websites(id) ON DELETE SET NULL,
  CONSTRAINT fk_ws_guardian FOREIGN KEY (requested_by)       REFERENCES users(id),
  INDEX idx_ws_child_domain (child_id, domain),
  INDEX idx_ws_child_time   (child_id, captured_at DESC)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
```

> ✅ Sau khi chạy xong 2 câu SQL này → note lại "SQL đã chạy" rồi mới làm backend/frontend.

---

## Phân tích kỹ thuật — Các trường hợp chụp ảnh

| Trường hợp | Kết quả | Cách xử lý |
|------------|---------|------------|
| Tab đang active trong cửa sổ Chrome thường | ✅ Chụp ngay | `captureVisibleTab(windowId)` |
| Tab đang ở background trong cửa sổ thường | ✅ Chụp được | Activate tab → chờ 600ms → capture |
| Tab active trong incognito (extension được Allow in Incognito) | ✅ Chụp được | `captureVisibleTab` hoạt động nếu extension được allow |
| Tab trong incognito nhưng extension KHÔNG được allow | ❌ Không được | Chrome block — báo failed |
| Domain không mở tab nào | ❌ Không được | Báo `tab_not_found` về backend |

**Về Incognito:** Extension với "Allow in Incognito" bật → chặn web được VÀ chụp ảnh được. Chrome cho phép `captureVisibleTab` trong incognito nếu extension được grant quyền đó.

---

## Tổng quan các thay đổi

| # | Phần | Thay đổi |
|---|------|----------|
| 1 | Backend Entity | Thêm `WebsiteScreenshot.cs` |
| 2 | Backend Service | `IScreenshotService` + `ScreenshotService` |
| 3 | Backend Controllers | Thêm endpoints vào `ChildrenController` + `ExtensionController` |
| 4 | Backend Hub | Kiểm tra group `child_{id}` và `guardian_{id}` trong `NotificationHub` |
| 5 | Extension `background.js` | Thêm SignalR listener + hàm capture/upload |
| 6 | Frontend API | Thêm screenshot functions vào `childrenApi.ts` |
| 7 | Frontend `WebsiteCard.tsx` | Nút 📷, hiển thị ảnh, modal xem full size |
| 8 | Frontend SignalR hook | Thêm listener `ScreenshotReady` |

---

## BƯỚC 1 — Backend: Entity

### 1.1 Kiểm tra trước
Mở `Models/` (hoặc `Entities/`). Xem có file nào liên quan screenshot chưa.
Mở `Data/AppDbContext.cs` xem `DbSet<WebsiteScreenshot>` đã có chưa.

### 1.2 Tạo `Models/WebsiteScreenshot.cs`

```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FamilyGuardian.Api.Models;

[Table("website_screenshots")]
public class WebsiteScreenshot
{
    [Column("id")]
    public int Id { get; set; }

    [Column("child_id")]
    public int ChildId { get; set; }

    [Column("allowed_website_id")]
    public int? AllowedWebsiteId { get; set; }

    [Column("domain")]
    [MaxLength(255)]
    public string Domain { get; set; } = string.Empty;

    [Column("image_path")]
    [MaxLength(500)]
    public string ImagePath { get; set; } = string.Empty;

    [Column("captured_at")]
    public DateTime CapturedAt { get; set; } = DateTime.Now;

    [Column("requested_by")]
    public int RequestedBy { get; set; }

    [Column("status")]
    [MaxLength(20)]
    public string Status { get; set; } = "pending";
    // Values: pending | captured | failed | tab_not_found

    [Column("error_message")]
    [MaxLength(500)]
    public string? ErrorMessage { get; set; }

    [ForeignKey("ChildId")]
    public User? Child { get; set; }

    [ForeignKey("AllowedWebsiteId")]
    public AllowedWebsite? AllowedWebsite { get; set; }
}
```

### 1.3 Thêm vào `AppDbContext.cs`

Tìm danh sách các `DbSet<>`. Thêm vào cuối (KHÔNG thay đổi gì khác):

```csharp
public DbSet<WebsiteScreenshot> WebsiteScreenshots { get; set; }
```

---

## BƯỚC 2 — Backend: Service

### 2.1 Tạo `Services/IScreenshotService.cs`

```csharp
namespace FamilyGuardian.Api.Services;

public interface IScreenshotService
{
    Task<ScreenshotRequestResult> RequestScreenshotAsync(int guardianId, int childId, string domain);
    Task<bool> SaveScreenshotAsync(int screenshotId, IFormFile imageFile);
    Task UpdateScreenshotStatusAsync(int screenshotId, string status, string? errorMessage = null);
    Task<List<ScreenshotDto>> GetScreenshotsAsync(int guardianId, int childId, string domain, int limit = 10);
}

public class ScreenshotRequestResult
{
    public bool Success { get; set; }
    public int? ScreenshotId { get; set; }
    public string? Error { get; set; }
}

public class ScreenshotDto
{
    public int Id { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public DateTime CapturedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
```

### 2.2 Tạo `Services/ScreenshotService.cs`

> ⚠️ Kiểm tra namespace của project trước khi copy. Kiểm tra `IHubContext<NotificationHub>` đang dùng trong project.

```csharp
using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Hubs;
using FamilyGuardian.Api.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Services;

public class ScreenshotService : IScreenshotService
{
    private readonly AppDbContext _context;
    private readonly IHubContext<NotificationHub> _hub;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ScreenshotService> _logger;

    public ScreenshotService(
        AppDbContext context,
        IHubContext<NotificationHub> hub,
        IWebHostEnvironment env,
        ILogger<ScreenshotService> logger)
    {
        _context = context;
        _hub = hub;
        _env = env;
        _logger = logger;
    }

    public async Task<ScreenshotRequestResult> RequestScreenshotAsync(
        int guardianId, int childId, string domain)
    {
        // Verify guardian có quyền với child
        var hasRelation = await _context.GuardianChildRelationships
            .AsNoTracking()
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);

        if (!hasRelation)
            return new ScreenshotRequestResult { Success = false, Error = "Không có quyền" };

        // Tìm allowed_website_id nếu có
        var website = await _context.AllowedWebsites
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.ChildId == childId && w.Domain == domain && w.IsActive);

        var screenshot = new WebsiteScreenshot
        {
            ChildId = childId,
            AllowedWebsiteId = website?.Id,
            Domain = domain,
            RequestedBy = guardianId,
            Status = "pending",
            CapturedAt = DateTime.Now,
            ImagePath = ""
        };

        _context.WebsiteScreenshots.Add(screenshot);
        await _context.SaveChangesAsync();

        // Gửi SignalR tới extension của con
        await _hub.Clients.Group($"child_{childId}")
            .SendAsync("CaptureScreenshot", new
            {
                screenshotId = screenshot.Id,
                domain = domain,
                allowedWebsiteId = website?.Id
            });

        _logger.LogInformation(
            "Screenshot requested: Id={Id}, ChildId={ChildId}, Domain={Domain}",
            screenshot.Id, childId, domain);

        return new ScreenshotRequestResult { Success = true, ScreenshotId = screenshot.Id };
    }

    public async Task<bool> SaveScreenshotAsync(int screenshotId, IFormFile imageFile)
    {
        var screenshot = await _context.WebsiteScreenshots.FindAsync(screenshotId);
        if (screenshot == null) return false;

        try
        {
            // Tạo thư mục lưu file
            var baseDir = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
            var screenshotsDir = Path.Combine(baseDir, "screenshots", screenshot.ChildId.ToString());
            Directory.CreateDirectory(screenshotsDir);

            var fileName = $"{screenshotId}_{DateTime.Now:yyyyMMddHHmmss}.jpg";
            var filePath = Path.Combine(screenshotsDir, fileName);

            await using var stream = File.Create(filePath);
            await imageFile.CopyToAsync(stream);

            screenshot.ImagePath = $"screenshots/{screenshot.ChildId}/{fileName}";
            screenshot.Status = "captured";
            screenshot.CapturedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            // Thông báo guardian ảnh đã sẵn sàng
            await _hub.Clients.Group($"guardian_{screenshot.RequestedBy}")
                .SendAsync("ScreenshotReady", new
                {
                    screenshotId = screenshot.Id,
                    childId = screenshot.ChildId,
                    domain = screenshot.Domain,
                    imageUrl = $"/{screenshot.ImagePath}",
                    capturedAt = screenshot.CapturedAt,
                    status = "captured"
                });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save screenshot {Id}", screenshotId);
            screenshot.Status = "failed";
            screenshot.ErrorMessage = ex.Message[..Math.Min(ex.Message.Length, 490)];
            await _context.SaveChangesAsync();

            await _hub.Clients.Group($"guardian_{screenshot.RequestedBy}")
                .SendAsync("ScreenshotReady", new
                {
                    screenshotId = screenshot.Id,
                    childId = screenshot.ChildId,
                    domain = screenshot.Domain,
                    status = "failed",
                    errorMessage = screenshot.ErrorMessage
                });

            return false;
        }
    }

    public async Task UpdateScreenshotStatusAsync(
        int screenshotId, string status, string? errorMessage = null)
    {
        var screenshot = await _context.WebsiteScreenshots.FindAsync(screenshotId);
        if (screenshot == null) return;

        screenshot.Status = status;
        screenshot.ErrorMessage = errorMessage;
        await _context.SaveChangesAsync();

        await _hub.Clients.Group($"guardian_{screenshot.RequestedBy}")
            .SendAsync("ScreenshotReady", new
            {
                screenshotId = screenshot.Id,
                childId = screenshot.ChildId,
                domain = screenshot.Domain,
                status = status,
                errorMessage = errorMessage,
                capturedAt = DateTime.Now
            });
    }

    public async Task<List<ScreenshotDto>> GetScreenshotsAsync(
        int guardianId, int childId, string domain, int limit = 10)
    {
        var hasRelation = await _context.GuardianChildRelationships
            .AsNoTracking()
            .AnyAsync(r => r.GuardianId == guardianId && r.ChildId == childId);

        if (!hasRelation) return [];

        return await _context.WebsiteScreenshots
            .AsNoTracking()
            .Where(s => s.ChildId == childId && s.Domain == domain)
            .OrderByDescending(s => s.CapturedAt)
            .Take(limit)
            .Select(s => new ScreenshotDto
            {
                Id = s.Id,
                Domain = s.Domain,
                Status = s.Status,
                ImageUrl = s.Status == "captured" && s.ImagePath != ""
                    ? $"/{s.ImagePath}"
                    : null,
                CapturedAt = s.CapturedAt,
                ErrorMessage = s.ErrorMessage
            })
            .ToListAsync();
    }
}
```

### 2.3 Đăng ký trong `Program.cs`

Tìm đoạn `builder.Services.AddScoped<...>`. Thêm vào:

```csharp
builder.Services.AddScoped<IScreenshotService, ScreenshotService>();
```

Tìm `app.UseStaticFiles()`. Nếu chưa có → thêm (để serve file ảnh):

```csharp
app.UseStaticFiles();
```

---

## BƯỚC 3 — Backend: Endpoints mới

### 3.1 Thêm vào `ChildrenController.cs`

> ⚠️ Đọc constructor hiện tại trước. Thêm `IScreenshotService` vào constructor (KHÔNG xóa gì cũ).
> ⚠️ Kiểm tra cách lấy userId từ JWT trong controller này — dùng đúng cách đó cho `guardianId`.

```csharp
// Thêm field:
private readonly IScreenshotService _screenshotService;

// Thêm vào constructor parameter:
// IScreenshotService screenshotService
// Gán: _screenshotService = screenshotService;

// ── Endpoint 1: Guardian yêu cầu chụp ──
[HttpPost("{childId}/request-screenshot")]
[Authorize(Roles = "guardian,admin")]
public async Task<IActionResult> RequestScreenshot(
    int childId, [FromQuery] string domain)
{
    if (string.IsNullOrWhiteSpace(domain))
        return BadRequest("domain required");

    // Dùng đúng cách lấy guardianId đang có trong project
    var guardianId = GetCurrentUserId();

    var result = await _screenshotService.RequestScreenshotAsync(guardianId, childId, domain);

    if (!result.Success)
        return BadRequest(new { error = result.Error });

    return Ok(new { screenshotId = result.ScreenshotId, message = "Đã gửi yêu cầu chụp ảnh" });
}

// ── Endpoint 2: Lấy danh sách ảnh ──
[HttpGet("{childId}/screenshots")]
[Authorize(Roles = "guardian,admin")]
public async Task<IActionResult> GetScreenshots(
    int childId, [FromQuery] string domain, [FromQuery] int limit = 10)
{
    if (string.IsNullOrWhiteSpace(domain))
        return BadRequest("domain required");

    var guardianId = GetCurrentUserId();
    var screenshots = await _screenshotService.GetScreenshotsAsync(guardianId, childId, domain, limit);
    return Ok(screenshots);
}
```

### 3.2 Thêm vào `ExtensionController.cs`

> ⚠️ Đọc constructor hiện tại. Thêm `IScreenshotService` tương tự.
> ⚠️ Kiểm tra cách lấy Google ID trong ExtensionController hiện tại (thường từ token verified) — dùng đúng pattern đó.
> ⚠️ Extension gọi các endpoint này với Google Token (không phải JWT) — đúng như các endpoint `/api/extension/` khác.

```csharp
// Thêm field + inject IScreenshotService (tương tự Bước 3.1)

// ── Endpoint 3: Extension upload ảnh ──
[HttpPost("upload-screenshot")]
public async Task<IActionResult> UploadScreenshot(
    [FromQuery] int screenshotId,
    IFormFile image)
{
    if (image == null || image.Length == 0)
        return BadRequest("No image");

    // Dùng đúng cách lấy child hiện tại đang dùng trong ExtensionController
    // (Thường là verify Google token → lấy user)
    var child = await GetCurrentChildAsync(); // thay bằng cách project đang dùng
    if (child == null) return Unauthorized();

    var screenshot = await _context.WebsiteScreenshots.FindAsync(screenshotId);
    if (screenshot == null || screenshot.ChildId != child.Id)
        return Forbid();

    var saved = await _screenshotService.SaveScreenshotAsync(screenshotId, image);
    return saved ? Ok("saved") : StatusCode(500, "save failed");
}

// ── Endpoint 4: Extension báo tab_not_found hoặc failed ──
[HttpPost("screenshot-result")]
public async Task<IActionResult> ScreenshotResult([FromBody] ScreenshotResultDto dto)
{
    var child = await GetCurrentChildAsync(); // thay bằng cách project đang dùng
    if (child == null) return Unauthorized();

    var screenshot = await _context.WebsiteScreenshots.FindAsync(dto.ScreenshotId);
    if (screenshot == null || screenshot.ChildId != child.Id)
        return Forbid();

    await _screenshotService.UpdateScreenshotStatusAsync(
        dto.ScreenshotId, dto.Status, dto.ErrorMessage);

    return Ok();
}
```

Thêm DTO class (trong cùng file hoặc file DTO riêng):

```csharp
public class ScreenshotResultDto
{
    public int ScreenshotId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}
```

---

## BƯỚC 4 — Backend: Kiểm tra NotificationHub

Mở `Hubs/NotificationHub.cs`. Tìm `OnConnectedAsync`. Kiểm tra:

**Con (child) có join group `child_{childId}` không?**
Nếu chưa → thêm (KHÔNG thay đổi logic hiện tại):
```csharp
if (user.Role == "child")
    await Groups.AddToGroupAsync(Context.ConnectionId, $"child_{user.Id}");
```

**Guardian có join group `guardian_{guardianId}` không?**
Nếu chưa → thêm:
```csharp
if (user.Role == "guardian")
    await Groups.AddToGroupAsync(Context.ConnectionId, $"guardian_{user.Id}");
```

> ⚠️ Chỉ thêm nếu thiếu. Nếu đã có → bỏ qua bước này.

---

## BƯỚC 5 — Extension: Sửa `background.js`

### 5.1 Kiểm tra trước (QUAN TRỌNG — đọc file trước khi sửa)

Mở `background.js`. Ghi lại:
- Tên biến SignalR connection (ví dụ: `connection`, `hubConnection`, ...)
- Tên hàm lấy Google token (ví dụ: `getGoogleToken()`, `getAuthToken()`, ...)
- Cách import/dùng `config.API_BASE` hoặc tên biến config tương đương
- Vị trí các `connection.on(...)` hiện có — sẽ thêm ngay bên dưới
- Kiểm tra `chrome.tabs` permissions đã có chưa

### 5.2 Thêm listener (CHỈ THÊM, KHÔNG sửa gì cũ)

Tìm đoạn cuối cùng của các `connection.on(...)`. Thêm ngay bên dưới:

```javascript
// ── THÊM MỚI: Screenshot ──
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
// ── KẾT THÚC THÊM ──
```

### 5.3 Thêm các hàm helper (thêm vào CUỐI file, trước dòng đóng IIFE nếu có)

> ⚠️ Thay `connection` bằng tên biến thực tế nếu khác.
> ⚠️ Thay `getGoogleToken()` bằng hàm lấy token thực tế.
> ⚠️ Thay `config.API_BASE` bằng cách truy cập API_BASE thực tế.

```javascript
// ── THÊM MỚI: Screenshot helpers ──

async function captureScreenshotForDomain(screenshotId, domain) {
  // Tìm tất cả tab có URL khớp domain (bao gồm cả incognito nếu extension được allow)
  const allTabs = await chrome.tabs.query({});

  const matchingTabs = allTabs.filter(tab => {
    if (!tab.url) return false;
    try {
      const hostname = new URL(tab.url).hostname.replace(/^www\./, '');
      const target   = domain.replace(/^www\./, '');
      return hostname === target || hostname.endsWith('.' + target);
    } catch {
      return false;
    }
  });

  if (matchingTabs.length === 0) {
    console.log("[FamilyGuardian] No tab found for:", domain);
    await reportScreenshotResult(
      screenshotId, "tab_not_found",
      "Không có tab nào đang mở " + domain
    );
    return;
  }

  // Ưu tiên tab đang active; nếu không có thì lấy tab đầu tiên
  let targetTab = matchingTabs.find(t => t.active) || matchingTabs[0];

  // Nếu tab chưa active → activate để capture được
  if (!targetTab.active) {
    await chrome.tabs.update(targetTab.id, { active: true });
    await new Promise(r => setTimeout(r, 600)); // chờ tab render
    targetTab = await chrome.tabs.get(targetTab.id);
  }

  // Chụp ảnh tab trong window đó
  const dataUrl = await chrome.tabs.captureVisibleTab(targetTab.windowId, {
    format: "jpeg",
    quality: 60
  });

  // dataUrl → Blob → upload
  const fetchRes  = await fetch(dataUrl);
  const blob      = await fetchRes.blob();
  await uploadScreenshot(screenshotId, blob);
}

async function uploadScreenshot(screenshotId, blob) {
  const token = await getGoogleToken(); // ← thay bằng tên hàm thực tế
  if (!token) throw new Error("No auth token");

  const formData = new FormData();
  formData.append("image", blob, "screenshot.jpg");

  const res = await fetch(
    `${config.API_BASE}/api/extension/upload-screenshot?screenshotId=${screenshotId}`,
    // ↑ thay config.API_BASE nếu project dùng cách khác
    {
      method: "POST",
      headers: { "Authorization": `Bearer ${token}` },
      body: formData
    }
  );

  if (!res.ok) {
    const text = await res.text();
    throw new Error(`Upload failed ${res.status}: ${text}`);
  }

  console.log("[FamilyGuardian] Screenshot uploaded:", screenshotId);
}

async function reportScreenshotResult(screenshotId, status, errorMessage) {
  try {
    const token = await getGoogleToken(); // ← thay tên hàm thực tế
    if (!token) return;

    await fetch(`${config.API_BASE}/api/extension/screenshot-result`, {
      method: "POST",
      headers: {
        "Authorization": `Bearer ${token}`,
        "Content-Type": "application/json"
      },
      body: JSON.stringify({ screenshotId, status, errorMessage })
    });
  } catch (e) {
    console.error("[FamilyGuardian] reportScreenshotResult error:", e);
  }
}

// ── KẾT THÚC THÊM ──
```

### 5.4 Kiểm tra `manifest.template.json`

Mở file. Tìm `"permissions"`. Đảm bảo có `"tabs"`:

```json
"permissions": ["tabs", "activeTab", ...]
```

Nếu chưa có `"tabs"` → thêm vào. Sau đó chạy lại `node build-config.js`.

---

## BƯỚC 6 — Frontend: API

### 6.1 Kiểm tra `src/api/childrenApi.ts` (hoặc file tương đương)

Xem axios instance đang import tên gì (thường là `api` hoặc `axiosInstance`).

### 6.2 Thêm vào cuối file (KHÔNG thay đổi gì cũ)

```typescript
// ── Screenshot ──

export interface ScreenshotDto {
  id: number;
  domain: string;
  status: 'pending' | 'captured' | 'failed' | 'tab_not_found';
  imageUrl: string | null;
  capturedAt: string;
  errorMessage: string | null;
}

export const requestScreenshot = async (childId: number, domain: string) => {
  const res = await api.post<{ screenshotId: number; message: string }>(
    `/children/${childId}/request-screenshot`,
    null,
    { params: { domain } }
  );
  return res.data;
};

export const getScreenshots = async (
  childId: number, domain: string, limit = 10
): Promise<ScreenshotDto[]> => {
  const res = await api.get<ScreenshotDto[]>(`/children/${childId}/screenshots`, {
    params: { domain, limit }
  });
  return res.data;
};
```

---

## BƯỚC 7 — Frontend: `WebsiteCard.tsx`

### 7.1 Kiểm tra code hiện tại (QUAN TRỌNG)

Đọc toàn bộ file. Ghi lại:
- Props interface — có `childId` chưa? Nếu chưa → thêm
- Import hiện tại (tránh duplicate import)
- Cách dùng `toast.*`
- CSS class pattern đang dùng (dark mode variables)
- Vị trí các nút action (Sửa, Xóa, Gia hạn...) — thêm nút 📷 vào cạnh đó

Kiểm tra trang cha render `<WebsiteCard>` — truyền thêm `childId` nếu component chưa có.

### 7.2 Thêm imports (gộp vào import hiện có, KHÔNG duplicate)

```typescript
import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { requestScreenshot, getScreenshots, type ScreenshotDto } from '@/api/childrenApi';
```

### 7.3 Thêm state và logic trong component

Thêm sau phần state/mutation hiện có (KHÔNG đụng vào gì cũ):

```typescript
// ── Screenshot ──
const queryClient = useQueryClient();
const [showScreenshots, setShowScreenshots] = useState(false);
const [selectedImageUrl, setSelectedImageUrl] = useState<string | null>(null);

const { data: screenshots, isLoading: screenshotsLoading } = useQuery({
  queryKey: ['screenshots', childId, website.domain],
  queryFn: () => getScreenshots(childId, website.domain, 5),
  enabled: showScreenshots,
  refetchInterval: showScreenshots ? 5000 : false,
});

const requestScreenshotMutation = useMutation({
  mutationFn: () => requestScreenshot(childId, website.domain),
  onSuccess: () => {
    toast.success('Đã gửi yêu cầu chụp ảnh');
    setShowScreenshots(true);
    queryClient.invalidateQueries({ queryKey: ['screenshots', childId, website.domain] });
  },
  onError: () => toast.error('Không thể gửi yêu cầu chụp ảnh'),
});
```

### 7.4 Thêm nút 📷 vào khu vực action buttons

Tìm khu vực render các nút (Sửa, Xóa...). Thêm 2 nút vào cạnh (KHÔNG thay đổi nút hiện tại):

```tsx
{/* Nút chụp ảnh */}
<button
  onClick={() => requestScreenshotMutation.mutate()}
  disabled={requestScreenshotMutation.isPending}
  title="Chụp ảnh màn hình"
  className="p-1.5 rounded-md text-tx-secondary hover:text-brand-DEFAULT
             hover:bg-brand-DEFAULT/10 transition-colors disabled:opacity-50"
>
  {requestScreenshotMutation.isPending ? (
    <svg className="w-4 h-4 animate-spin" fill="none" viewBox="0 0 24 24">
      <circle className="opacity-25" cx="12" cy="12" r="10"
              stroke="currentColor" strokeWidth="4"/>
      <path className="opacity-75" fill="currentColor"
            d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"/>
    </svg>
  ) : (
    <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
        d="M3 9a2 2 0 012-2h.93a2 2 0 001.664-.89l.812-1.22A2 2 0 0110.07 4h3.86a2 2 0 011.664.89l.812 1.22A2 2 0 0018.07 7H19a2 2 0 012 2v9a2 2 0 01-2 2H5a2 2 0 01-2-2V9z"/>
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
        d="M15 13a3 3 0 11-6 0 3 3 0 016 0z"/>
    </svg>
  )}
</button>

{/* Nút xem lịch sử ảnh */}
<button
  onClick={() => setShowScreenshots(s => !s)}
  title={showScreenshots ? 'Ẩn ảnh' : 'Xem ảnh đã chụp'}
  className={`p-1.5 rounded-md transition-colors
    ${showScreenshots
      ? 'text-brand-DEFAULT bg-brand-DEFAULT/10'
      : 'text-tx-secondary hover:text-brand-DEFAULT hover:bg-brand-DEFAULT/10'
    }`}
>
  <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
      d="M4 16l4.586-4.586a2 2 0 012.828 0L16 16m-2-2l1.586-1.586a2 2 0 012.828 0L20 14m-6-6h.01M6 20h12a2 2 0 002-2V6a2 2 0 00-2-2H6a2 2 0 00-2 2v12a2 2 0 002 2z"/>
  </svg>
</button>
```

### 7.5 Thêm phần hiển thị ảnh (sau phần bonus/progress hiện tại)

```tsx
{/* Khu vực ảnh — hiện khi showScreenshots */}
{showScreenshots && (
  <div className="mt-3 pt-3 border-t border-border-base">
    <p className="text-xs font-medium text-tx-secondary uppercase tracking-wide mb-2">
      Ảnh màn hình
    </p>

    {screenshotsLoading && (
      <p className="text-xs text-tx-secondary animate-pulse">Đang tải...</p>
    )}

    {!screenshotsLoading && screenshots?.length === 0 && (
      <p className="text-xs text-tx-secondary italic">Chưa có ảnh nào. Nhấn 📷 để chụp.</p>
    )}

    <div className="flex flex-col gap-2">
      {screenshots?.map(shot => (
        <ScreenshotItem
          key={shot.id}
          screenshot={shot}
          onViewFull={setSelectedImageUrl}
        />
      ))}
    </div>
  </div>
)}

{/* Modal xem full size */}
{selectedImageUrl && (
  <div
    className="fixed inset-0 z-50 flex items-center justify-center bg-black/80 p-4"
    onClick={() => setSelectedImageUrl(null)}
  >
    <div
      className="relative max-w-4xl max-h-[90vh] rounded-xl overflow-hidden shadow-2xl"
      onClick={e => e.stopPropagation()}
    >
      <img
        src={selectedImageUrl}
        alt="Screenshot"
        className="max-w-full max-h-[90vh] object-contain"
      />
      <button
        onClick={() => setSelectedImageUrl(null)}
        className="absolute top-3 right-3 p-2 rounded-full bg-black/60 text-white
                   hover:bg-black/80 transition-colors"
      >
        <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                d="M6 18L18 6M6 6l12 12"/>
        </svg>
      </button>
    </div>
  </div>
)}
```

### 7.6 Thêm component `ScreenshotItem`

Thêm bên ngoài component `WebsiteCard` (cùng file hoặc tách riêng):

```tsx
interface ScreenshotItemProps {
  screenshot: ScreenshotDto;
  onViewFull: (url: string) => void;
}

function ScreenshotItem({ screenshot, onViewFull }: ScreenshotItemProps) {
  const timeStr = new Date(screenshot.capturedAt).toLocaleString('vi-VN');

  if (screenshot.status === 'pending') {
    return (
      <div className="flex items-center gap-2 p-2 rounded-lg bg-bg-subtle border border-border-base">
        <svg className="w-4 h-4 animate-spin text-brand-DEFAULT shrink-0"
             fill="none" viewBox="0 0 24 24">
          <circle className="opacity-25" cx="12" cy="12" r="10"
                  stroke="currentColor" strokeWidth="4"/>
          <path className="opacity-75" fill="currentColor"
                d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"/>
        </svg>
        <span className="text-xs text-tx-secondary">Đang chụp... ({timeStr})</span>
      </div>
    );
  }

  if (screenshot.status === 'tab_not_found') {
    return (
      <div className="flex items-center gap-2 p-2 rounded-lg
                      bg-yellow-500/8 border border-yellow-500/20">
        <svg className="w-4 h-4 text-yellow-500 shrink-0" fill="none"
             viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
            d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z"/>
        </svg>
        <div>
          <p className="text-xs font-medium text-yellow-600 dark:text-yellow-400">
            Con chưa mở website này
          </p>
          <p className="text-xs text-tx-secondary">{timeStr}</p>
        </div>
      </div>
    );
  }

  if (screenshot.status === 'failed') {
    return (
      <div className="flex items-center gap-2 p-2 rounded-lg
                      bg-red-500/8 border border-red-500/20">
        <svg className="w-4 h-4 text-red-500 shrink-0" fill="none"
             viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                d="M6 18L18 6M6 6l12 12"/>
        </svg>
        <div>
          <p className="text-xs font-medium text-red-600 dark:text-red-400">Chụp thất bại</p>
          <p className="text-xs text-tx-secondary">{timeStr}</p>
        </div>
      </div>
    );
  }

  if (screenshot.status === 'captured' && screenshot.imageUrl) {
    return (
      <div
        className="relative rounded-lg overflow-hidden border border-border-base
                   cursor-pointer hover:border-brand-DEFAULT/50 transition-colors group"
        onClick={() => onViewFull(screenshot.imageUrl!)}
      >
        <img
          src={screenshot.imageUrl}
          alt="Screenshot"
          className="w-full h-32 object-cover object-top"
        />
        {/* Hover overlay */}
        <div className="absolute inset-0 bg-black/0 group-hover:bg-black/20
                        transition-colors flex items-center justify-center">
          <svg className="w-8 h-8 text-white opacity-0 group-hover:opacity-100 transition-opacity"
               fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
              d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0zM10 7v3m0 0v3m0-3h3m-3 0H7"/>
          </svg>
        </div>
        {/* Timestamp */}
        <div className="absolute bottom-0 left-0 right-0 bg-gradient-to-t
                        from-black/60 to-transparent px-2 py-1.5">
          <p className="text-xs text-white">{timeStr}</p>
        </div>
      </div>
    );
  }

  return null;
}
```

---

## BƯỚC 8 — Frontend: SignalR listener `ScreenshotReady`

### 8.1 Kiểm tra `useSignalR.ts` hoặc `useExtensionMonitor.ts`

Mở hook Guardian. Tìm các `connection.on(...)` hoặc `hubConnection.on(...)` hiện có.
Xác định `queryClient` đang được lấy từ đâu trong hook đó.

### 8.2 Thêm listener (KHÔNG thay đổi gì cũ)

```typescript
// Thêm vào cạnh các .on() hiện có:
connection.on("ScreenshotReady", (data: {
  screenshotId: number;
  childId: number;
  domain: string;
  status?: string;
  imageUrl?: string;
  errorMessage?: string;
}) => {
  // Invalidate để WebsiteCard tự refresh
  queryClient.invalidateQueries({
    queryKey: ['screenshots', data.childId, data.domain]
  });

  if (data.status === 'tab_not_found') {
    toast.warning('Con chưa mở website này, không chụp được');
  } else if (data.status === 'failed') {
    toast.error('Chụp ảnh thất bại');
  } else if (data.imageUrl || data.status === 'captured') {
    toast.success('Đã chụp ảnh thành công!');
  }
});
```

---

## Thứ tự làm việc

```
B1  — Chạy 2 câu SQL → note "SQL đã chạy"
B2  — Backend: Tạo WebsiteScreenshot.cs
B3  — Backend: Thêm DbSet vào AppDbContext
B4  — Backend: Tạo IScreenshotService.cs + ScreenshotService.cs
B5  — Backend: Đăng ký service trong Program.cs + kiểm tra UseStaticFiles
B6  — Backend: Kiểm tra NotificationHub có group child_{id} + guardian_{id}
B7  — Backend: Thêm 2 endpoint vào ChildrenController
B8  — Backend: Thêm 2 endpoint vào ExtensionController + DTO class
B9  — Extension: Kiểm tra manifest.template.json có "tabs" permission
B10 — Extension: Thêm connection.on("CaptureScreenshot") vào background.js
B11 — Extension: Thêm 3 hàm helper (capture, upload, report)
B12 — Extension: node build-config.js → reload extension
B13 — Frontend: Thêm 2 API functions vào childrenApi.ts
B14 — Frontend: Thêm state + nút 📷 + ảnh section vào WebsiteCard.tsx
B15 — Frontend: Thêm ScreenshotItem component
B16 — Frontend: Thêm listener ScreenshotReady vào SignalR hook
B17 — Test toàn bộ flow
```

---

## Checklist kiểm tra trước khi viết code

### Backend
- [ ] Namespace chính xác của project?
- [ ] Cách lấy `userId` từ JWT trong ChildrenController — dùng method nào?
- [ ] Cách lấy child từ Google token trong ExtensionController — pattern nào?
- [ ] NotificationHub: con join `child_{id}`, guardian join `guardian_{id}` chưa?
- [ ] `wwwroot/` folder có tồn tại không? Nếu không → tạo hoặc dùng `ContentRootPath`
- [ ] `app.UseStaticFiles()` có trong Program.cs chưa?

### Extension
- [ ] Tên biến SignalR connection (`connection` hay khác)?
- [ ] Tên hàm lấy Google token?
- [ ] Cách truy cập API_BASE?
- [ ] `manifest.template.json` có `"tabs"` trong permissions?

### Frontend
- [ ] `WebsiteCard` nhận `childId` prop chưa? Trang cha truyền xuống chưa?
- [ ] Import axios instance đúng tên?
- [ ] `useQueryClient` có thể dùng trong hook SignalR không (cần `QueryClientProvider` bao ngoài)?
- [ ] `toast` import từ đúng store?

---

## Test

```
TEST 1 — Tab đang active
Con mở youtube.com ở tab đang nhìn
→ Guardian nhấn 📷 trên WebsiteCard youtube.com
→ Toast "Đã gửi yêu cầu"
→ ~2s: toast "Đã chụp ảnh thành công!"
→ Thumbnail xuất hiện trong card, click mở full size

TEST 2 — Tab ở background
Con mở youtube.com (tab 1) + google.com (tab 2, đang active)
→ Guardian chụp youtube.com
→ Extension tự activate tab youtube, capture, không cần con làm gì
→ Ảnh xuất hiện bình thường

TEST 3 — Website chưa mở tab nào
Con chưa mở youtube.com
→ Guardian chụp
→ Toast warning "Con chưa mở website này"
→ Card hiện trạng thái vàng "Con chưa mở website này"

TEST 4 — Incognito (extension đã Allow in Incognito)
Con mở website trong incognito
→ Guardian chụp → Ảnh chụp được bình thường

TEST 5 — Dark mode
Bật dark mode → nút 📷, thumbnail, modal đều dùng đúng CSS variables
→ Không có màu hardcode
```

---

## Lưu ý Deploy (Render)

Render filesystem là ephemeral — ảnh mất khi restart. Cho production cần dùng cloud storage (Cloudinary/S3). Cho dev/local: file system là đủ.
