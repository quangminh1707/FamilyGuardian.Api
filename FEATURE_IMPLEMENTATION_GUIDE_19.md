# Family Guardian — Đổi sang Sightengine AI (Phần 19)

> **Ngày tạo:** 2026-05-27
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE_18.md (Phần 18)
> **Thay thế:** Google Cloud Vision → Sightengine (dễ setup, không cần billing)

---

## ⚠️ Quy tắc

- KHÔNG thay đổi logic 1.000 ảnh/tháng (chỉ đổi giới hạn thành 2.000)
- KHÔNG thay đổi logic toggle Enabled từ DB
- KHÔNG thay đổi IServiceScopeFactory fix từ Guide 18
- KHÔNG thay đổi extension

---

## SQL cần chạy

Không có SQL mới.

---

## Setup Sightengine (2 phút)

1. Vào https://sightengine.com → **Sign Up** (miễn phí, không cần thẻ)
2. Vào **Dashboard → API Credentials**
3. Copy **api_user** và **api_secret**
4. Xong — không cần enable gì thêm

---

## PHẦN A — Sửa `appsettings.Development.json`

Xóa config Google Vision, thêm Sightengine:

```json
"ContentModeration": {
  "Enabled": false,
  "Provider": "sightengine",
  "SightengineApiUser": "1234567890",
  "SightengineApiSecret": "your-api-secret-here",
  "NudityThreshold": 0.5,
  "MonthlyLimit": 2000
}
```

> `NudityThreshold`: 0.5 (khuyến nghị). Tăng lên 0.7 nếu muốn ít false positive.
> `MonthlyLimit`: 2.000 (Sightengine free tier cho 2.000/tháng).

## Sửa `appsettings.json` (file mẫu — commit lên git)

```json
"ContentModeration": {
  "Enabled": false,
  "Provider": "sightengine",
  "SightengineApiUser": "",
  "SightengineApiSecret": "",
  "NudityThreshold": 0.5,
  "MonthlyLimit": 2000
}
```

---

## PHẦN B — Sửa `ContentModerationService.cs`

### B.1 Kiểm tra trước

Mở file. Đọc toàn bộ method `CheckAndNotifyAsync`. Xác định:
- Phần gọi Google Vision API (dùng `PostAsJsonAsync` với `vision.googleapis.com`)
- Phần parse response (`safeSearchAnnotation`)
- Giữ nguyên: phần check limit, phần đọc Enabled từ DB, phần tạo notification, phần SignalR

### B.2 Sửa interface — thêm `ContentModerationUsageDto` field nếu cần

Không thay đổi interface. Chỉ thay implementation bên trong.

### B.3 Thay phần gọi API và parse response

Tìm đoạn này trong `CheckAndNotifyAsync` (phần gọi Google Vision):

```csharp
// ĐÂY LÀ ĐOẠN CẦN THAY — từ sau dòng đọc file ảnh:
var imageBytes = await File.ReadAllBytesAsync(fullPath);
var base64     = Convert.ToBase64String(imageBytes);

var client = _http.CreateClient();
client.Timeout = TimeSpan.FromSeconds(15);

var payload = new { requests = new[] { ... } };  // Google Vision payload

var resp = await client.PostAsJsonAsync(
    $"https://vision.googleapis.com/v1/images:annotate?key={apiKey}",
    payload);

// ... parse safeSearchAnnotation
var adult    = ss.GetProperty("adult").GetString() ?? "UNKNOWN";
var flagged  = IsFlagged(adult) || IsFlagged(violence) || IsFlagged(racy);
```

**Thay toàn bộ đoạn trên bằng:**

```csharp
var apiUser   = _config["ContentModeration:SightengineApiUser"];
var apiSecret = _config["ContentModeration:SightengineApiSecret"];
var threshold = _config.GetValue<float>("ContentModeration:NudityThreshold", 0.5f);

if (string.IsNullOrEmpty(apiUser) || string.IsNullOrEmpty(apiSecret))
{
    _logger.LogWarning("Sightengine credentials not configured");
    shot.AiModerationStatus = "error";
    shot.AiCheckedAt        = DateTime.Now;
    await _context.SaveChangesAsync();
    return;
}

// Đọc file ảnh
var imageBytes = await File.ReadAllBytesAsync(fullPath);

// Gọi Sightengine API — multipart/form-data
var client = _http.CreateClient();
client.Timeout = TimeSpan.FromSeconds(20);

using var form = new MultipartFormDataContent();
form.Add(new ByteArrayContent(imageBytes), "media", "screenshot.jpg");
form.Add(new StringContent(apiUser),   "api_user");
form.Add(new StringContent(apiSecret), "api_secret");
form.Add(new StringContent("nudity-2.0"), "models");

var resp = await client.PostAsync(
    "https://api.sightengine.com/1.0/check.json",
    form);

if (!resp.IsSuccessStatusCode)
{
    var errorBody = await resp.Content.ReadAsStringAsync();
    _logger.LogWarning("Sightengine API error: {Status} — {Body}",
        resp.StatusCode, errorBody);
    shot.AiModerationStatus = "error";
    shot.AiCheckedAt        = DateTime.Now;
    await _context.SaveChangesAsync();
    return;
}

var json   = await resp.Content.ReadAsStringAsync();
var doc    = JsonDocument.Parse(json);
var root   = doc.RootElement;

// Sightengine trả về nudity scores
// nudity.sexual_activity, nudity.sexual_display, nudity.erotica = nội dung rõ ràng
// nudity.very_suggestive, nudity.suggestive = gợi cảm
var nudity = root.GetProperty("nudity");

var sexualActivity = nudity.GetProperty("sexual_activity").GetSingle();
var sexualDisplay  = nudity.GetProperty("sexual_display").GetSingle();
var erotica        = nudity.GetProperty("erotica").GetSingle();

// Score cao nhất trong các loại rõ ràng
var maxScore = Math.Max(sexualActivity, Math.Max(sexualDisplay, erotica));
var flagged  = maxScore >= threshold;

shot.AiModerationStatus = flagged ? "flagged" : "safe";
shot.AiAdultScore       = maxScore.ToString("F2"); // ví dụ "0.92"
shot.AiCheckedAt        = DateTime.Now;
await _context.SaveChangesAsync();

_logger.LogInformation(
    "Sightengine: Id={Id}, Status={Status}, MaxScore={Score:F2}, Usage={Used}/{Limit}",
    screenshotId, shot.AiModerationStatus, maxScore, usedThisMonth + 1, limit);

// Cảnh báo 90% quota
if (usedThisMonth + 1 >= (int)(limit * 0.9))
{
    _logger.LogWarning(
        "ContentModeration approaching monthly limit: {Used}/{Limit}",
        usedThisMonth + 1, limit);
}

if (!flagged) return;

// ── Tạo notification + SignalR — GIỮ NGUYÊN từ code cũ ──
var notif = new Notification
{
    GuardianId = shot.RequestedBy,
    ChildId    = shot.ChildId,
    Title      = "⚠️ Nội dung không phù hợp",
    Message    = $"Ảnh chụp từ {shot.Domain} có thể chứa nội dung 18+ (score: {maxScore:P0}).",
    Type       = "content_warning",
    IsRead     = false,
    CreatedAt  = DateTime.Now,
    SentAt     = DateTime.Now
};
_context.Notifications.Add(notif);
await _context.SaveChangesAsync();

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
    screenshotId, shot.Domain, maxScore);
```

### B.4 Xóa đoạn check apiKey cũ

Tìm và xóa đoạn check Google Vision API key:
```csharp
// XÓA đoạn này (không còn dùng):
var apiKey = _config["ContentModeration:GoogleVisionApiKey"];
if (string.IsNullOrEmpty(apiKey)) return;
```

Vì logic check credentials đã được xử lý trong đoạn mới ở B.3.

### B.5 Xóa `IsFlagged` helper (không còn dùng)

Xóa method `IsFlagged` nếu chỉ dùng cho Google Vision:
```csharp
// XÓA nếu chỉ dùng cho Google Vision:
bool IsFlagged(string level) =>
    Array.IndexOf(Levels, level) >= Array.IndexOf(Levels, threshold);
```

Xóa luôn `private static readonly string[] Levels = [...]` nếu không dùng ở chỗ khác.

---

## PHẦN C — Verify IServiceScopeFactory (từ Guide 18)

Mở `ScreenshotService.cs`. Đảm bảo đã có fix từ Guide 18:

```csharp
// PHẢI có dạng này (không phải _ = Task.Run(() => _moderation.CheckAndNotify...)):
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
        _logger.LogError(ex, "Background AI moderation failed for screenshot {Id}", shotId);
    }
});
```

Nếu chưa có → làm theo Guide 18 Phần A trước.

---

## PHẦN D — Frontend: Không thay đổi gì

Tất cả frontend từ Guide 17 giữ nguyên:
- Toggle AI ON/OFF
- Hiển thị `X/2000` (số tự cập nhật theo `monthlyLimit` từ API)
- Badge ⚠️ 18+ trên thumbnail
- Toast + SignalR listener

Chỉ cần đảm bảo `ContentModerationUsageDto.MonthlyLimit` trả về `2000` → UI tự hiện đúng.

---

## Extension — Không thay đổi

---

## Thứ tự làm việc

```
SETUP  — Đăng ký sightengine.com → lấy api_user + api_secret (~2 phút)

A1  — Sửa appsettings.Development.json: thêm SightengineApiUser/Secret, MonthlyLimit=2000
A2  — Sửa appsettings.json: thêm placeholder rỗng

B1  — Mở ContentModerationService.cs — đọc toàn bộ CheckAndNotifyAsync
B2  — Xóa đoạn check apiKey Google Vision
B3  — Xóa string[] Levels và IsFlagged helper (nếu chỉ dùng cho Google Vision)
B4  — Thay phần gọi API + parse response bằng đoạn Sightengine mới
B5  — Giữ nguyên: check limit, check Enabled DB, notification, SignalR

C1  — Verify IServiceScopeFactory đã có trong ScreenshotService.cs

REBUILD → Restart backend → Test
```

---

## Checklist

- [ ] `SightengineApiUser` và `SightengineApiSecret` có giá trị thật trong Development.json
- [ ] `MonthlyLimit: 2000` trong cả 2 file appsettings
- [ ] Xóa sạch code Google Vision (apiKey, Levels, IsFlagged)
- [ ] Đoạn Sightengine mới dùng `MultipartFormDataContent`
- [ ] Phần tạo notification + SignalR giữ nguyên
- [ ] `IServiceScopeFactory` đã có trong ScreenshotService (Guide 18)
- [ ] Frontend không thay đổi gì

---

## Test

```
1. Đăng ký sightengine.com → lấy credentials → thêm vào Development.json
2. Restart backend
3. Bật AI ON trong modal
4. Chụp ảnh bình thường (google.com):
   → Log: "Sightengine: Status=safe, MaxScore=0.01"
   → Số tăng: 0/2000 → 1/2000
   → Không có badge, không có toast

5. Chụp ảnh có nội dung 18+:
   → Log: "Sightengine: Status=flagged, MaxScore=0.95"
   → Log: "Content flagged: ..."
   → Badge ⚠️ 18+ xuất hiện trên thumbnail
   → Toast: "⚠️ Phát hiện nội dung 18+ trong ảnh từ [domain]!"
   → Notification trong hệ thống
```

---

## Sightengine Response Format (tham khảo)

```json
{
  "status": "success",
  "request": { "id": "...", "timestamp": 1234567890, "operations": 1 },
  "nudity": {
    "sexual_activity": 0.01,
    "sexual_display": 0.02,
    "erotica": 0.03,
    "very_suggestive": 0.05,
    "suggestive": 0.10,
    "mildly_suggestive": 0.15,
    "suggestive_classes": {},
    "none": 0.85
  },
  "media": { "id": "...", "uri": "screenshot.jpg" }
}
```

`sexual_activity` + `sexual_display` + `erotica` = nội dung rõ ràng 18+.
Threshold 0.5 nghĩa là: score >= 0.5 ở bất kỳ loại nào → flagged.
