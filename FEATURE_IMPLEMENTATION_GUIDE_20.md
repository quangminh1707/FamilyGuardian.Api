# Family Guardian — Đổi sang DeepAI (Phần 20)

> **Ngày tạo:** 2026-05-27
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE_19.md (Phần 19)
> **Thay thế:** Sightengine → DeepAI

---

## Setup DeepAI (1 phút) (đã xong)

1. Vào https://deepai.org → **Sign Up** (email + password, không cần thẻ)
2. Vào **Profile → API Keys**
3. Copy API key
4. Xong

**DeepAI Nudity Detection:**
- Endpoint: `POST https://api.deepai.org/api/nsfw-detector`
- Header: `api-key: YOUR_KEY`
- Body: multipart/form-data với file ảnh
- Trả về: `output.nsfw_score` từ 0.0 đến 1.0

---

## SQL cần chạy

Không có.

---

## PHẦN A — Sửa appsettings

**`appsettings.Development.json`:**

```json
"ContentModeration": {
  "Enabled": false,
  "Provider": "deepai",
  "DeepAiApiKey": "e4d6eb13-c907-4837-ac86-08c29770fc05",
  "NudityThreshold": 0.6,
  "MonthlyLimit": 2000
}
```

**`appsettings.json`** (file mẫu commit git):

```json
"ContentModeration": {
  "Enabled": false,
  "Provider": "deepai",
  "DeepAiApiKey": "",
  "NudityThreshold": 0.6,
  "MonthlyLimit": 2000
}
```

---

## PHẦN B — Sửa `ContentModerationService.cs`

### B.1 Xóa code cũ (Sightengine hoặc Google Vision)

Tìm và xóa toàn bộ đoạn gọi API cũ trong `CheckAndNotifyAsync` — từ sau dòng đọc file ảnh đến trước phần tạo notification.

Xóa các helper không còn dùng:
- `string[] Levels` (nếu còn)
- `IsFlagged()` (nếu còn)
- Credential check của provider cũ

### B.2 Thay bằng DeepAI

```csharp
public async Task CheckAndNotifyAsync(int screenshotId)
{
    // ── Kiểm tra Enabled từ DB (GIỮ NGUYÊN từ Guide 17) ──
    var dbSetting = await _context.AppSettings
        .AsNoTracking()
        .FirstOrDefaultAsync(s => s.Key == "content_moderation_enabled");

    var enabled = dbSetting != null
        ? dbSetting.Value == "true"
        : _config.GetValue<bool>("ContentModeration:Enabled");

    if (!enabled) return;

    var apiKey = _config["ContentModeration:DeepAiApiKey"];
    if (string.IsNullOrEmpty(apiKey))
    {
        _logger.LogWarning("DeepAI API key not configured");
        return;
    }

    var shot = await _context.WebsiteScreenshots.FindAsync(screenshotId);
    if (shot == null || shot.Status != "captured") return;

    // ── Kiểm tra giới hạn tháng (GIỮ NGUYÊN từ Guide 16) ──
    var limit         = _config.GetValue<int>("ContentModeration:MonthlyLimit", 2000);
    var threshold     = _config.GetValue<float>("ContentModeration:NudityThreshold", 0.6f);
    var usedThisMonth = await GetMonthlyCheckCountAsync();

    if (usedThisMonth >= limit)
    {
        var reset = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1).AddMonths(1);
        _logger.LogWarning(
            "ContentModeration limit reached ({Used}/{Limit}). Screenshot {Id} skipped. Resets {Reset:dd/MM/yyyy}.",
            usedThisMonth, limit, screenshotId, reset);

        shot.AiModerationStatus = "skipped";
        shot.AiCheckedAt        = DateTime.Now;
        await _context.SaveChangesAsync();
        return;
    }

    // ── Đọc file ảnh ──
    var baseDir  = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
    var fullPath = Path.Combine(baseDir, shot.ImagePath.TrimStart('/'));

    if (!File.Exists(fullPath))
    {
        _logger.LogWarning("Screenshot file not found: {Path}", fullPath);
        shot.AiModerationStatus = "error";
        shot.AiCheckedAt        = DateTime.Now;
        await _context.SaveChangesAsync();
        return;
    }

    var imageBytes = await File.ReadAllBytesAsync(fullPath);

    try
    {
        // ── Gọi DeepAI NSFW Detector ──
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.Add("api-key", apiKey);

        using var form = new MultipartFormDataContent();
        var imageContent = new ByteArrayContent(imageBytes);
        imageContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(imageContent, "image", "screenshot.jpg");

        var resp = await client.PostAsync(
            "https://api.deepai.org/api/nsfw-detector",
            form);

        if (!resp.IsSuccessStatusCode)
        {
            var errorBody = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("DeepAI API error: {Status} — {Body}",
                resp.StatusCode, errorBody);
            shot.AiModerationStatus = "error";
            shot.AiCheckedAt        = DateTime.Now;
            await _context.SaveChangesAsync();
            return;
        }

        var json  = await resp.Content.ReadAsStringAsync();
        var doc   = JsonDocument.Parse(json);
        var root  = doc.RootElement;

        // DeepAI trả về: { "output": { "nsfw_score": 0.95, "detections": [...] } }
        var nsfwScore = root
            .GetProperty("output")
            .GetProperty("nsfw_score")
            .GetSingle();

        var flagged = nsfwScore >= threshold;

        shot.AiModerationStatus = flagged ? "flagged" : "safe";
        shot.AiAdultScore       = nsfwScore.ToString("F2");
        shot.AiCheckedAt        = DateTime.Now;
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "DeepAI: Id={Id}, Status={Status}, Score={Score:F2}, Usage={Used}/{Limit}",
            screenshotId, shot.AiModerationStatus, nsfwScore, usedThisMonth + 1, limit);

        // Cảnh báo 90% quota
        if (usedThisMonth + 1 >= (int)(limit * 0.9))
            _logger.LogWarning("ContentModeration approaching monthly limit: {Used}/{Limit}",
                usedThisMonth + 1, limit);

        if (!flagged) return;

        // ── Tạo notification (GIỮ NGUYÊN) ──
        var notif = new Notification
        {
            GuardianId = shot.RequestedBy,
            ChildId    = shot.ChildId,
            Title      = "⚠️ Nội dung không phù hợp",
            Message    = $"Ảnh chụp từ {shot.Domain} có thể chứa nội dung 18+ (score: {nsfwScore:P0}).",
            Type       = "content_warning",
            IsRead     = false,
            CreatedAt  = DateTime.Now,
            SentAt     = DateTime.Now
        };
        _context.Notifications.Add(notif);
        await _context.SaveChangesAsync();

        // ── SignalR (GIỮ NGUYÊN) ──
        await _hub.Clients.Group($"guardian_{shot.RequestedBy}")
            .SendAsync("ContentWarning", new
            {
                screenshotId = shot.Id,
                childId      = shot.ChildId,
                domain       = shot.Domain,
                adultScore   = shot.AiAdultScore,
                imageUrl     = $"/{shot.ImagePath}"
            });

        _logger.LogWarning(
            "Content flagged: Id={Id}, Domain={Domain}, Score={Score:F2}",
            screenshotId, shot.Domain, nsfwScore);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "DeepAI moderation error for screenshot {Id}", screenshotId);
        shot.AiModerationStatus = "error";
        shot.AiCheckedAt        = DateTime.Now;
        await _context.SaveChangesAsync();
    }
}
```

---

## PHẦN C — Verify không thay đổi

Kiểm tra những phần này **còn nguyên**:

| Phần | Trạng thái |
|------|-----------|
| `GetMonthlyCheckCountAsync()` | ✅ Giữ nguyên |
| `GetMonthlyUsageAsync()` | ✅ Giữ nguyên |
| `ToggleAsync()` | ✅ Giữ nguyên |
| `IServiceScopeFactory` trong ScreenshotService | ✅ Giữ nguyên |
| Frontend toggle UI | ✅ Không thay đổi |
| SignalR listener frontend | ✅ Không thay đổi |

---

## Thứ tự làm việc

```
1 — Đăng ký deepai.org → lấy API key (~1 phút)
2 — Sửa appsettings.Development.json: thêm DeepAiApiKey
3 — Sửa appsettings.json: thêm placeholder rỗng
4 — Mở ContentModerationService.cs
5 — Xóa toàn bộ code provider cũ (Sightengine/Google Vision)
6 — Paste code DeepAI mới vào
7 — Restart backend → test
```

---

## Test nhanh API key (PowerShell)

```powershell
$key = "your-deepai-key"
$response = Invoke-RestMethod `
  -Uri "https://api.deepai.org/api/nsfw-detector" `
  -Method POST `
  -Headers @{"api-key" = $key} `
  -Form @{image = "https://upload.wikimedia.org/wikipedia/commons/thumb/4/47/PNG_transparency_demonstration_1.png/200px-PNG_transparency_demonstration_1.png"}

$response.output
```

Kết quả mong đợi:
```
nsfw_score detections
---------- ----------
      0.02 {}
```

---

## Test sau khi deploy

```
1. Restart backend
2. Bật AI ON trong modal
3. Chụp ảnh google.com:
   → Log: "DeepAI: Status=safe, Score=0.02"
   → Số tăng: 0/2000 → 1/2000

4. Chụp ảnh có nội dung 18+:
   → Log: "DeepAI: Status=flagged, Score=0.95"
   → Badge ⚠️ 18+ trên thumbnail
   → Toast cảnh báo
   → Notification trong hệ thống
```
