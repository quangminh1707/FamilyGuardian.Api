# Family Guardian — Đổi sang NudeNet Local (Phần 21)

> **Ngày tạo:** 2026-05-27
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE_20.md (Phần 20)
> **Thay thế:** DeepAI (paid) → NudeNet (free vĩnh viễn, chạy local)

---

## Tại sao NudeNet

| | DeepAI | NudeNet |
|--|--------|---------|
| Giá | ❌ Paid | ✅ Free vĩnh viễn |
| Đăng ký | ❌ Cần | ✅ Không cần |
| Giới hạn | N/A | ✅ Unlimited |
| Internet | ❌ Cần | ✅ Chạy offline |
| Privacy | ❌ Gửi ảnh ra ngoài | ✅ Ảnh không rời máy |
| Setup | Đăng ký → key | `pip install` → chạy |

---

## SQL cần chạy

Không có.

---

## PHẦN A — Tạo NudeNet Python Service

### A.1 Yêu cầu

- Python 3.8+ đã cài (kiểm tra: `python --version`)
- Nếu chưa có: https://python.org/downloads

### A.2 Tạo thư mục service

Tạo thư mục `nudenet-service/` **cùng cấp** với thư mục `FamilyGuardian.Api/`:

```
Hệ thống kiểm soát truy cập/
├── FamilyGuardian.Api/
├── FamilyGuardian.Frontend/
└── nudenet-service/          ← TẠO MỚI
    ├── app.py
    ├── requirements.txt
    └── start.bat
```

### A.3 Tạo `nudenet-service/requirements.txt`

```
nudenet==3.4.2
flask==3.0.3
pillow==10.3.0
```

### A.4 Tạo `nudenet-service/app.py`

```python
from flask import Flask, request, jsonify
from nudenet import NudeDetector
import base64
import io
import os
import tempfile
from PIL import Image

app = Flask(__name__)

# Load model 1 lần khi khởi động (lần đầu tự download ~90MB)
print("[NudeNet] Loading model...")
detector = NudeDetector()
print("[NudeNet] Model loaded. Ready at http://localhost:5001")

# Labels rõ ràng 18+
EXPLICIT_LABELS = {
    "FEMALE_GENITALIA_EXPOSED",
    "MALE_GENITALIA_EXPOSED",
    "FEMALE_BREAST_EXPOSED",
    "ANUS_EXPOSED",
    "BUTTOCKS_EXPOSED",
}

@app.route("/health", methods=["GET"])
def health():
    return jsonify({"status": "ok", "model": "nudenet"})

@app.route("/check", methods=["POST"])
def check():
    try:
        data = request.get_json(force=True)
        if not data or "image" not in data:
            return jsonify({"error": "missing image field"}), 400

        # Decode base64 → PIL Image
        image_bytes = base64.b64decode(data["image"])
        img = Image.open(io.BytesIO(image_bytes)).convert("RGB")

        # Lưu temp file (NudeNet cần file path)
        tmp_path = None
        try:
            with tempfile.NamedTemporaryFile(
                suffix=".jpg", delete=False, dir=tempfile.gettempdir()
            ) as tmp:
                img.save(tmp.name, "JPEG", quality=85)
                tmp_path = tmp.name

            detections = detector.detect(tmp_path)
        finally:
            if tmp_path and os.path.exists(tmp_path):
                os.unlink(tmp_path)

        # Tìm score cao nhất trong explicit labels
        nude_score = 0.0
        for det in detections:
            if det.get("class") in EXPLICIT_LABELS:
                nude_score = max(nude_score, det.get("score", 0.0))

        threshold = float(data.get("threshold", 0.6))
        is_nude   = nude_score >= threshold

        return jsonify({
            "isNude": is_nude,
            "score":  round(nude_score, 4),
        })

    except Exception as e:
        print(f"[NudeNet] Error: {e}")
        return jsonify({"error": str(e)}), 500

if __name__ == "__main__":
    app.run(host="127.0.0.1", port=5001, debug=False)
```

### A.5 Tạo `nudenet-service/start.bat` (chạy trên Windows)

```bat
@echo off
echo Installing dependencies...
pip install -r requirements.txt
echo.
echo Starting NudeNet service on http://localhost:5001
echo First run will download model (~90MB), please wait...
echo.
python app.py
pause
```

### A.6 Chạy service lần đầu

```
Double-click start.bat
```

hoặc trong terminal:
```bash
cd nudenet-service
pip install -r requirements.txt
python app.py
```

Lần đầu chạy: tự download model ~90MB, chờ ~1-2 phút.
Các lần sau: khởi động ngay trong ~3 giây.

**Terminal báo thành công:**
```
[NudeNet] Loading model...
[NudeNet] Model loaded. Ready at http://localhost:5001
 * Running on http://127.0.0.1:5001
```

---

## PHẦN B — Sửa `appsettings.Development.json`

```json
"ContentModeration": {
  "Enabled": false,
  "Provider": "nudenet",
  "NudeNetUrl": "http://localhost:5001",
  "NudityThreshold": 0.6,
  "MonthlyLimit": 2000
}
```

## Sửa `appsettings.json` (file mẫu commit git)

```json
"ContentModeration": {
  "Enabled": false,
  "Provider": "nudenet",
  "NudeNetUrl": "http://localhost:5001",
  "NudityThreshold": 0.6,
  "MonthlyLimit": 2000
}
```

> `NudeNetUrl` không có secret key → an toàn khi commit lên git.

---

## PHẦN C — Sửa `ContentModerationService.cs`

### C.1 Xóa code DeepAI cũ

Xóa toàn bộ đoạn gọi DeepAI trong `CheckAndNotifyAsync` — từ sau dòng đọc file ảnh đến trước phần tạo notification.

### C.2 Thay bằng NudeNet

Thay phần gọi API bằng đoạn sau:

```csharp
var nudeNetUrl = _config["ContentModeration:NudeNetUrl"];
var threshold  = _config.GetValue<float>("ContentModeration:NudityThreshold", 0.6f);

if (string.IsNullOrEmpty(nudeNetUrl))
{
    _logger.LogWarning("NudeNet URL not configured");
    shot.AiModerationStatus = "error";
    shot.AiCheckedAt        = DateTime.Now;
    await _context.SaveChangesAsync();
    return;
}

// Đọc file ảnh → base64
var imageBytes = await File.ReadAllBytesAsync(fullPath);
var base64Str  = Convert.ToBase64String(imageBytes);

var client = _http.CreateClient();
client.Timeout = TimeSpan.FromSeconds(30);

// Gọi NudeNet service
var payload = new { image = base64Str, threshold };
var resp    = await client.PostAsJsonAsync($"{nudeNetUrl}/check", payload);

if (!resp.IsSuccessStatusCode)
{
    var errorBody = await resp.Content.ReadAsStringAsync();
    _logger.LogWarning("NudeNet error: {Status} — {Body}",
        resp.StatusCode, errorBody);
    shot.AiModerationStatus = "error";
    shot.AiCheckedAt        = DateTime.Now;
    await _context.SaveChangesAsync();
    return;
}

var json  = await resp.Content.ReadAsStringAsync();
var doc   = JsonDocument.Parse(json);
var root  = doc.RootElement;

var isNude    = root.GetProperty("isNude").GetBoolean();
var nudeScore = root.GetProperty("score").GetSingle();

var flagged = isNude;

shot.AiModerationStatus = flagged ? "flagged" : "safe";
shot.AiAdultScore       = nudeScore.ToString("F2");
shot.AiCheckedAt        = DateTime.Now;
await _context.SaveChangesAsync();

_logger.LogInformation(
    "NudeNet: Id={Id}, Status={Status}, Score={Score:F2}, Usage={Used}/{Limit}",
    screenshotId, shot.AiModerationStatus, nudeScore, usedThisMonth + 1, limit);

if (usedThisMonth + 1 >= (int)(limit * 0.9))
    _logger.LogWarning("ContentModeration approaching limit: {Used}/{Limit}",
        usedThisMonth + 1, limit);

if (!flagged) return;

// ── Tạo notification + SignalR — GIỮ NGUYÊN ──
var notif = new Notification
{
    GuardianId = shot.RequestedBy,
    ChildId    = shot.ChildId,
    Title      = "⚠️ Nội dung không phù hợp",
    Message    = $"Ảnh chụp từ {shot.Domain} có thể chứa nội dung 18+ (score: {nudeScore:P0}).",
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
    screenshotId, shot.Domain, nudeScore);
```

### C.3 Xử lý khi NudeNet service chưa chạy

Thêm catch cho `HttpRequestException` để báo rõ hơn:

```csharp
catch (HttpRequestException ex)
{
    // NudeNet service chưa chạy hoặc bị tắt
    _logger.LogWarning(
        "NudeNet service unreachable at {Url}. Is nudenet-service running? Error: {Msg}",
        nudeNetUrl, ex.Message);
    shot.AiModerationStatus = "error";
    shot.AiCheckedAt        = DateTime.Now;
    await _context.SaveChangesAsync();
}
catch (Exception ex)
{
    _logger.LogError(ex, "ContentModeration error for screenshot {Id}", screenshotId);
    shot.AiModerationStatus = "error";
    shot.AiCheckedAt        = DateTime.Now;
    await _context.SaveChangesAsync();
}
```

---

## PHẦN D — Workflow khi develop local

Mỗi lần develop, cần chạy **3 service song song**:

| Terminal | Lệnh | Service |
|----------|------|---------|
| Terminal 1 | `dotnet run` trong `FamilyGuardian.Api/` | Backend ASP.NET |
| Terminal 2 | `npm run dev` trong frontend folder | Frontend Vite |
| Terminal 3 | `python app.py` trong `nudenet-service/` | NudeNet AI |

> NudeNet service có thể tắt khi không cần dùng AI — backend sẽ log warning nhưng không crash.

---

## Thứ tự làm việc

```
A1 — Kiểm tra Python đã cài: python --version
A2 — Tạo thư mục nudenet-service/
A3 — Tạo requirements.txt, app.py, start.bat
A4 — Chạy start.bat → đợi download model (~90MB lần đầu)
A5 — Verify: mở browser → http://localhost:5001/health → {"status":"ok"}

B1 — Sửa appsettings.Development.json: đổi sang NudeNet config
B2 — Sửa appsettings.json: placeholder

C1 — Mở ContentModerationService.cs
C2 — Xóa code DeepAI/Sightengine/GoogleVision cũ
C3 — Paste code NudeNet mới vào
C4 — Thêm catch HttpRequestException
C5 — Restart backend .NET

TEST — Bật AI ON → chụp ảnh → xem log
```

---

## Checklist

- [ ] Python 3.8+ đã cài
- [ ] `nudenet-service/` tạo đúng vị trí (cùng cấp với Api folder)
- [ ] `python app.py` đang chạy ở terminal riêng
- [ ] `http://localhost:5001/health` trả `{"status":"ok"}`
- [ ] `NudeNetUrl: "http://localhost:5001"` trong Development.json
- [ ] Code cũ (DeepAI/Google/Sightengine) đã xóa sạch
- [ ] `IServiceScopeFactory` vẫn còn trong ScreenshotService (Guide 18)

---

## Test

```
1. Chạy: python app.py (terminal riêng)
2. Kiểm tra: curl http://localhost:5001/health
3. Restart .NET backend
4. Bật AI ON trong modal
5. Chụp ảnh google.com:
   → Log: "NudeNet: Status=safe, Score=0.00"
   → Số tăng 1
6. Chụp ảnh 18+:
   → Log: "NudeNet: Status=flagged, Score=0.97"
   → Badge ⚠️ 18+ xuất hiện
   → Toast + Notification
```
