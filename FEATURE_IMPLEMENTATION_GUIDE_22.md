# Family Guardian — Xóa hoàn toàn tính năng AI 18+ (Phần 22)

> **Ngày tạo:** 2026-05-27
> Xóa sạch AI moderation khỏi hệ thống, không ảnh hưởng chức năng khác.

---

## ⚠️ Quy tắc

- KHÔNG xóa bảng `website_screenshots` — chỉ xóa các cột AI
- KHÔNG thay đổi logic chụp ảnh, upload, polling
- KHÔNG thay đổi extension
- Sau khi xóa: chức năng chụp ảnh vẫn hoạt động bình thường

---

## SQL cần chạy (đã xong)

```sql
-- Xóa cột AI khỏi website_screenshots
ALTER TABLE website_screenshots
  DROP COLUMN IF EXISTS ai_moderation_status,
  DROP COLUMN IF EXISTS ai_adult_score,
  DROP COLUMN IF EXISTS ai_nude_score,
  DROP COLUMN IF EXISTS ai_checked_at;

-- Xóa bảng app_settings (tạo riêng cho AI toggle)
DROP TABLE IF EXISTS app_settings;
```

> ✅ Chạy SQL trước, note lại "đã chạy" rồi mới làm backend/frontend.

---

## PHẦN A — Backend: Xóa files

### A.1 Xóa hoàn toàn các file sau

```
FamilyGuardian.Api/
├── Services/
│   └── ContentModerationService.cs   ← XÓA FILE NÀY
├── Controllers/
│   └── SettingsController.cs         ← XÓA FILE NÀY (nếu chỉ có moderation endpoints)
└── Models/
    └── AppSetting.cs                 ← XÓA FILE NÀY
```

> ⚠️ Nếu `SettingsController.cs` có endpoints khác ngoài moderation → chỉ xóa các method liên quan AI, không xóa file.

### A.2 Sửa `AppDbContext.cs`

Tìm và xóa dòng:
```csharp
// XÓA:
public DbSet<AppSetting> AppSettings { get; set; }
```

### A.3 Sửa `Models/WebsiteScreenshot.cs`

Tìm và xóa 3 field AI (giữ nguyên tất cả field khác):

```csharp
// XÓA 3 dòng/block này:
[Column("ai_moderation_status")]
[MaxLength(20)]
public string? AiModerationStatus { get; set; }

[Column("ai_adult_score")]
[MaxLength(20)]
public string? AiAdultScore { get; set; }

// Nếu có ai_nude_score thì xóa luôn:
[Column("ai_nude_score")]
public float? AiNudeScore { get; set; }

[Column("ai_checked_at")]
public DateTime? AiCheckedAt { get; set; }
```

### A.4 Sửa `Services/ScreenshotService.cs`

Tìm và xóa đoạn fire-and-forget AI:

```csharp
// XÓA toàn bộ đoạn này:
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

Xóa luôn field `_scopeFactory` và injection nếu không dùng ở chỗ nào khác:

```csharp
// XÓA nếu chỉ dùng cho AI:
private readonly IServiceScopeFactory _scopeFactory;
// và dòng gán trong constructor:
// _scopeFactory = scopeFactory;
```

### A.5 Sửa `Program.cs`

Tìm và xóa các dòng đăng ký AI service:

```csharp
// XÓA:
builder.Services.AddScoped<IContentModerationService, ContentModerationService>();
```

> `builder.Services.AddHttpClient()` — giữ lại nếu có service khác dùng `IHttpClientFactory`. Xóa nếu chỉ dùng cho AI.

### A.6 Sửa `appsettings.json` và `appsettings.Development.json`

Xóa section `ContentModeration` trong cả 2 file:

```json
// XÓA block này:
"ContentModeration": {
  "Enabled": false,
  "Provider": "...",
  ...
}
```

---

## PHẦN B — Frontend: Xóa code AI

### B.1 Xóa hoặc làm trống `src/api/settingsApi.ts`

Nếu file chỉ có moderation functions → **xóa file**.
Nếu có functions khác → chỉ xóa:

```typescript
// XÓA:
export interface ContentModerationUsage { ... }
export const getModerationStatus = ...
export const toggleModeration = ...
```

### B.2 Sửa `ScreenshotModal.tsx`

Tìm và xóa toàn bộ phần AI trong component:

**Xóa imports:**
```typescript
// XÓA:
import { getModerationStatus, toggleModeration } from '@/api/settingsApi';
// hoặc từ childrenApi nếu để ở đó
```

**Xóa query + mutation:**
```typescript
// XÓA:
const { data: moderationStatus, refetch: refetchModeration } = useQuery({
  queryKey: ['moderation-status'],
  ...
});

const toggleModerationMutation = useMutation({
  mutationFn: (enabled: boolean) => toggleModeration(enabled),
  ...
});
```

**Xóa UI trong JSX — tìm và xóa khối AI Toggle:**
```tsx
{/* XÓA toàn bộ block này: */}
{moderationStatus && (
  <div className="flex items-center gap-1.5">
    ...AI toggle button...
  </div>
)}
```

**Xóa badge 18+ trên thumbnail:**
```tsx
{/* XÓA: */}
{s.aiModerationStatus === 'flagged' && (
  <div className="absolute top-1.5 left-1.5 ...">
    ⚠️ 18+
  </div>
)}
```

### B.3 Sửa `ScreenshotDto` trong `childrenApi.ts`

```typescript
// XÓA 2 field này:
aiModerationStatus?: 'safe' | 'flagged' | 'error' | 'skipped' | null;
aiAdultScore?: string | null;
// hoặc:
aiNudeScore?: number | null;
```

### B.4 Sửa SignalR hook Guardian

Tìm và xóa listener `ContentWarning`:

```typescript
// XÓA:
connection.on("ContentWarning", (data: { ... }) => {
  toast.warning(`⚠️ Phát hiện nội dung 18+...`);
  queryClient.invalidateQueries({ ... });
});
```

---

## PHẦN C — NudeNet Service (nếu đã tạo)

Xóa thư mục `nudenet-service/` hoặc giữ lại để dùng sau.

---

## Thứ tự làm việc

```
1 — Chạy SQL (xóa cột AI + drop app_settings)
2 — Xóa ContentModerationService.cs
3 — Xóa AppSetting.cs
4 — Xóa SettingsController.cs (hoặc chỉ xóa moderation methods)
5 — Sửa AppDbContext.cs: xóa DbSet<AppSetting>
6 — Sửa WebsiteScreenshot.cs: xóa 3-4 field AI
7 — Sửa ScreenshotService.cs: xóa Task.Run + IServiceScopeFactory
8 — Sửa Program.cs: xóa AddScoped<IContentModerationService>
9 — Sửa appsettings.json + appsettings.Development.json
10 — Frontend: xóa/sửa settingsApi.ts
11 — Frontend: sửa ScreenshotModal.tsx (xóa toggle + badge)
12 — Frontend: sửa ScreenshotDto
13 — Frontend: sửa SignalR hook
14 — Build + test
```

---

## Checklist sau khi xóa

### Backend build không lỗi
- [ ] Không còn reference đến `IContentModerationService`
- [ ] Không còn reference đến `AppSetting`
- [ ] Không còn reference đến `AiModerationStatus`, `AiAdultScore`, `AiCheckedAt`
- [ ] `dotnet build` → 0 errors

### Frontend build không lỗi
- [ ] Không còn import từ `settingsApi`
- [ ] Không còn `moderationStatus`, `toggleModerationMutation`
- [ ] Không còn `aiModerationStatus` trong ScreenshotDto
- [ ] `npm run build` → 0 errors

### Chức năng chụp ảnh vẫn hoạt động
- [ ] Chụp ảnh → ảnh hiện trong modal bình thường
- [ ] Hẹn giờ chụp vẫn hoạt động
- [ ] Xóa ảnh vẫn hoạt động
- [ ] Filter thời gian vẫn hoạt động
