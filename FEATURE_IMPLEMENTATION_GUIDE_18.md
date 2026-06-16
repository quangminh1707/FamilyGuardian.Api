# Family Guardian — Fix AI Moderation không chạy (Phần 18)

> **Ngày tạo:** 2026-05-27
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE_17.md (Phần 17)

---

## ⚠️ Quy tắc bất di bất dịch

| Quy tắc | Chi tiết |
|---------|---------|
| Logic giới hạn 1.000 ảnh/tháng | KHÔNG thay đổi |
| Logic toggle Enabled từ DB | KHÔNG thay đổi |
| Logic block/heartbeat extension | KHÔNG thay đổi |

---

## SQL cần chạy

Không có SQL mới.

---

## Phân tích nguyên nhân

### Nguyên nhân chính — DbContext bị disposed

```csharp
// Trong SaveScreenshotAsync — cách hiện tại (SAI):
_ = Task.Run(() => _moderation.CheckAndNotifyAsync(screenshot.Id));
```

**Vấn đề:** `ContentModerationService` là **Scoped service** — được tạo và gắn với HTTP request.
Khi request kết thúc (trả response về extension), ASP.NET Core **dispose toàn bộ scoped services** — bao gồm `AppDbContext` bên trong `ContentModerationService`.

Nhưng `Task.Run()` vẫn đang chạy ngầm → `_context` đã disposed → `await _context.WebsiteScreenshots.FindAsync(...)` throw `ObjectDisposedException` → **silent fail, không log ra console**.

### Nguyên nhân phụ — Có thể kèm theo

| Nguyên nhân | Triệu chứng |
|------------|------------|
| `wwwroot` chưa có / path sai | `File.Exists` = false → `status = "error"` → không đếm |
| API key chưa đúng trong Development.json | Google Vision trả 403/400 → `status = "error"` |
| `ContentModeration:Enabled` trong DB = "false" dù UI hiện ON | Toggle chưa lưu xuống DB |

---

## PHẦN A — Fix chính: IServiceScopeFactory

### A.1 Kiểm tra trước

Mở `ScreenshotService.cs`. Tìm dòng:
```csharp
_ = Task.Run(() => _moderation.CheckAndNotifyAsync(screenshot.Id));
```

Tìm constructor của `ScreenshotService`. Xem có inject `IServiceScopeFactory` chưa.

### A.2 Sửa `ScreenshotService.cs`

**Thêm field + inject `IServiceScopeFactory`** vào constructor:

```csharp
// Thêm field (KHÔNG thay đổi các field hiện có):
private readonly IServiceScopeFactory _scopeFactory;

// Thêm vào constructor parameter:
// IServiceScopeFactory scopeFactory
// Gán: _scopeFactory = scopeFactory;
```

**Sửa dòng fire-and-forget** trong `SaveScreenshotAsync`:

```csharp
// ❌ CŨ — bị disposed:
_ = Task.Run(() => _moderation.CheckAndNotifyAsync(screenshot.Id));

// ✅ MỚI — tạo scope riêng, không bị disposed:
var shotId = screenshot.Id;
_ = Task.Run(async () =>
{
    try
    {
        using var scope = _scopeFactory.CreateScope();
        var moderationService = scope.ServiceProvider
            .GetRequiredService<IContentModerationService>();
        await moderationService.CheckAndNotifyAsync(shotId);
    }
    catch (Exception ex)
    {
        // Log lỗi — dùng ILogger của ScreenshotService
        _logger.LogError(ex, "Background AI moderation failed for screenshot {Id}", shotId);
    }
});
```

> ⚠️ `_scopeFactory` là `IServiceScopeFactory` — đây là **Singleton** nên an toàn để inject vào bất kỳ service nào.
> ⚠️ `IServiceScopeFactory` không cần đăng ký thêm — đã có sẵn trong ASP.NET Core DI.

---

## PHẦN B — Verify API Key và Path

### B.1 Kiểm tra API key

Mở `appsettings.Development.json`. Kiểm tra:

```json
"ContentModeration": {
  "Enabled": false,
  "GoogleVisionApiKey": "AIzaSy...",  ← PHẢI có giá trị thật ở đây
  "FlagThreshold": "LIKELY",
  "MonthlyLimit": 1000
}
```

> ⚠️ Nếu `GoogleVisionApiKey` là `""` hoặc `"YOUR_API_KEY_HERE"` → Google Vision trả 400 → silent fail.

**Test API key nhanh** — chạy trong PowerShell:
```powershell
curl -X POST "https://vision.googleapis.com/v1/images:annotate?key=YOUR_KEY" `
  -H "Content-Type: application/json" `
  -d '{"requests":[{"image":{"source":{"imageUri":"https://www.google.com/images/branding/googlelogo/1x/googlelogo_color_272x92dp.png"}},"features":[{"type":"SAFE_SEARCH_DETECTION"}]}]}'
```
→ Nếu trả về JSON có `safeSearchAnnotation` → key đúng.
→ Nếu trả về `403` hoặc `400` → key sai hoặc chưa enable Cloud Vision API.

### B.2 Kiểm tra thư mục wwwroot

Backend lưu ảnh vào `wwwroot/screenshots/`. Kiểm tra thư mục có tồn tại không:

```
FamilyGuardian.Api/
├── wwwroot/
│   └── screenshots/
│       └── {childId}/
│           └── {screenshotId}_{timestamp}.jpg
```

Nếu chưa có thư mục `wwwroot` → tạo thủ công hoặc kiểm tra `Program.cs` có `app.UseStaticFiles()` không.

**Kiểm tra `Program.cs`:**
```csharp
// PHẢI có dòng này:
app.UseStaticFiles();
```

### B.3 Kiểm tra DB — content_moderation_enabled có đúng "true" không

Chạy trong MySQL Workbench:
```sql
SELECT * FROM app_settings WHERE `key` = 'content_moderation_enabled';
```

Kết quả mong đợi: `value = 'true'`

Nếu `value = 'false'` dù UI hiện "AI ON" → toggle API chưa gọi được → kiểm tra endpoint `PATCH /api/settings/content-moderation`.

---

## PHẦN C — Thêm logging tạm để debug

### C.1 Thêm log trong `ContentModerationService.CheckAndNotifyAsync`

Mở file. Thêm log ngay đầu method (KHÔNG thay đổi logic):

```csharp
public async Task CheckAndNotifyAsync(int screenshotId)
{
    // ── DEBUG LOG — xóa sau khi fix xong ──
    _logger.LogInformation("[AI-DEBUG] CheckAndNotifyAsync called for screenshot {Id}", screenshotId);

    // Đọc Enabled từ DB
    var dbSetting = await _context.AppSettings
        .AsNoTracking()
        .FirstOrDefaultAsync(s => s.Key == "content_moderation_enabled");

    var enabled = dbSetting != null
        ? dbSetting.Value == "true"
        : _config.GetValue<bool>("ContentModeration:Enabled");

    // ── DEBUG LOG ──
    _logger.LogInformation("[AI-DEBUG] Enabled={Enabled}, DbSetting={DbValue}",
        enabled, dbSetting?.Value ?? "null");

    if (!enabled) return;

    var apiKey = _config["ContentModeration:GoogleVisionApiKey"];
    // ── DEBUG LOG ──
    _logger.LogInformation("[AI-DEBUG] ApiKey={HasKey}", !string.IsNullOrEmpty(apiKey) ? "SET" : "EMPTY");

    if (string.IsNullOrEmpty(apiKey)) return;

    var shot = await _context.WebsiteScreenshots.FindAsync(screenshotId);
    // ── DEBUG LOG ──
    _logger.LogInformation("[AI-DEBUG] Screenshot found={Found}, Status={Status}, Path={Path}",
        shot != null, shot?.Status, shot?.ImagePath);

    if (shot == null || shot.Status != "captured") return;

    // ... giữ nguyên phần còn lại
```

Sau khi restart backend và chụp ảnh → xem terminal logs. Tìm `[AI-DEBUG]` để biết dừng ở đâu.

---

## PHẦN D — Fix đếm usage khi có error

### D.1 Vấn đề hiện tại

Nếu ảnh bị `status = "error"` (file không tìm thấy hoặc API lỗi), nó bị loại khỏi count:
```csharp
&& s.AiModerationStatus != "error"
```

→ Kể cả khi Google Vision đã được gọi nhưng trả lỗi, count không tăng → không bảo vệ được quota.

### D.2 Fix — đếm TẤT CẢ lần đã cố gọi (trừ skipped)

Mở `ContentModerationService.cs`. Tìm `GetMonthlyCheckCountAsync`. Sửa:

```csharp
private async Task<int> GetMonthlyCheckCountAsync()
{
    var now        = DateTime.Now;
    var monthStart = new DateTime(now.Year, now.Month, 1);
    var monthEnd   = monthStart.AddMonths(1);

    // Đếm TẤT CẢ lần đã set ai_checked_at (bao gồm error)
    // Chỉ loại "skipped" (đã bị khóa trước đó)
    return await _context.WebsiteScreenshots
        .CountAsync(s => s.AiCheckedAt >= monthStart
                      && s.AiCheckedAt <  monthEnd
                      && s.AiModerationStatus != "skipped");
}
```

> Lý do: `error` nghĩa là đã cố gọi hoặc cố xử lý → nên đếm vào quota để an toàn.
> `skipped` nghĩa là đã bị chặn trước → không đếm.

---

## Thứ tự làm việc

```
A1 — Mở ScreenshotService.cs
A2 — Thêm IServiceScopeFactory vào constructor
A3 — Sửa Task.Run() thành scope-based fire-and-forget
A4 — Restart backend

B1 — Verify GoogleVisionApiKey có giá trị thật trong appsettings.Development.json
B2 — Test API key bằng curl hoặc PowerShell
B3 — Kiểm tra thư mục wwwroot/screenshots/ tồn tại
B4 — Kiểm tra app.UseStaticFiles() trong Program.cs
B5 — Kiểm tra DB: SELECT * FROM app_settings

C1 — Thêm [AI-DEBUG] logs vào CheckAndNotifyAsync
C2 — Restart backend → chụp ảnh → xem terminal logs
C3 — Xác định dừng ở đâu → fix điểm đó
C4 — Xóa [AI-DEBUG] logs sau khi xác nhận chạy đúng

D1 — Sửa GetMonthlyCheckCountAsync: đếm cả "error", loại "skipped"

TEST — Chụp ảnh 18+ → chờ ~3s → badge ⚠️ hiện + toast + số tăng
```

---

## Checklist

- [ ] `IServiceScopeFactory` inject vào `ScreenshotService` constructor
- [ ] `Task.Run()` dùng `using var scope = _scopeFactory.CreateScope()`
- [ ] `GoogleVisionApiKey` có giá trị thật trong Development.json
- [ ] `app.UseStaticFiles()` có trong Program.cs
- [ ] `wwwroot/screenshots/` tồn tại sau khi chụp ảnh đầu tiên
- [ ] DB `app_settings`: `content_moderation_enabled = 'true'`
- [ ] Log `[AI-DEBUG]` xuất hiện trong terminal sau khi chụp

---

## Test sau khi fix

```
1. Restart backend (QUAN TRỌNG — phải restart sau khi sửa)
2. Bật AI ON trong modal (nếu chưa bật)
3. Chụp ảnh bằng extension
4. Xem terminal → phải thấy [AI-DEBUG] logs
5. Chờ ~3-5 giây
6. Nếu ảnh có nội dung 18+:
   → Badge ⚠️ 18+ hiện trên thumbnail
   → Toast warning xuất hiện
   → Notification trong hệ thống
7. Số usage tăng: 0/1000 → 1/1000
8. Chụp ảnh Google.com (safe):
   → Không có badge
   → Không có toast
   → Số vẫn tăng: 1/1000 → 2/1000
```
