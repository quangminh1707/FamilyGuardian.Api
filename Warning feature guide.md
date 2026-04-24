# 🔔 Tính năng: Cảnh báo thời gian sử dụng web

> **Lưu ý:** Migration SQL đã chạy xong. Chỉ cần làm các bước Backend và Frontend bên dưới.

---

## Tổng quan tính năng

Guardian có thể cấu hình **tối đa 2 mốc cảnh báo** (theo % thời gian đã dùng) cho từng website có giới hạn thời gian của con. Khi con dùng web đến mốc đó, hệ thống tự động gửi thông báo cho guardian qua SignalR và lưu vào DB. Nội dung thông báo do guardian tự viết.

**Ví dụ:** Limit = 10 phút, Mốc 1 = 80% → cảnh báo khi con đã dùng 8 phút (còn 2 phút).

---

## Database (đã xong — chỉ để tham khảo)

```sql
-- Bảng đã tạo sẵn
CREATE TABLE website_warning_configs (
    id                  INT AUTO_INCREMENT PRIMARY KEY,
    allowed_website_id  INT          NOT NULL UNIQUE,
    threshold1_percent  INT          NOT NULL DEFAULT 80,
    threshold1_message  VARCHAR(500) NOT NULL,
    threshold2_percent  INT          NULL,
    threshold2_message  VARCHAR(500) NULL,
    is_active           TINYINT(1)   NOT NULL DEFAULT 1,
    created_at          TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    FOREIGN KEY (allowed_website_id) REFERENCES allowed_websites(id) ON DELETE CASCADE
);

-- Cột đã thêm vào daily_usage_stats
ALTER TABLE daily_usage_stats
    ADD COLUMN warning1_sent TINYINT(1) NOT NULL DEFAULT 0,
    ADD COLUMN warning2_sent TINYINT(1) NOT NULL DEFAULT 0;
```

---

## Backend — Những việc cần làm

### 1. Tạo Entity `WebsiteWarningConfig`

**File:** `Models/Entities/WebsiteWarningConfig.cs`

```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FamilyGuardian.Api.Models.Entities;

[Table("website_warning_configs")]
public class WebsiteWarningConfig
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("allowed_website_id")]
    public int AllowedWebsiteId { get; set; }

    [Column("threshold1_percent")]
    public int Threshold1Percent { get; set; } = 80;

    [Column("threshold1_message")]
    public string Threshold1Message { get; set; } = string.Empty;

    [Column("threshold2_percent")]
    public int? Threshold2Percent { get; set; }

    [Column("threshold2_message")]
    public string? Threshold2Message { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public AllowedWebsite? AllowedWebsite { get; set; }
}
```

---

### 2. Cập nhật Entity `DailyUsageStat`

**File:** `Models/Entities/DailyUsageStat.cs`

Thêm 2 property vào cuối class:

```csharp
[Column("warning1_sent")]
public bool Warning1Sent { get; set; } = false;

[Column("warning2_sent")]
public bool Warning2Sent { get; set; } = false;
```

---

### 3. Cập nhật `AppDbContext`

**File:** `Data/AppDbContext.cs`

Thêm DbSet vào class:

```csharp
public DbSet<WebsiteWarningConfig> WebsiteWarningConfigs { get; set; }
```

---

### 4. Tạo `WarningConfigController`

**File:** `Controllers/WarningConfigController.cs`

Các endpoint cần có:

| Method | Route | Mô tả |
|--------|-------|-------|
| GET | `/api/warning-configs?allowedWebsiteId={id}` | Lấy config của 1 website, trả 204 nếu chưa có |
| GET | `/api/warning-configs/by-child/{childId}` | Lấy tất cả config của các web thuộc 1 con (join với `AllowedWebsite` để lấy `domain`) |
| POST | `/api/warning-configs` | Tạo hoặc cập nhật (upsert) — nhận body bên dưới |
| DELETE | `/api/warning-configs/{id}` | Xoá config |

**Request body cho POST:**
```json
{
  "allowedWebsiteIds": [1, 2, 3],
  "threshold1Percent": 80,
  "threshold1Message": "Con sắp hết giờ rồi!",
  "threshold2Percent": 95,
  "threshold2Message": "Chỉ còn vài phút nữa!"
}
```

**Validation:**
- `allowedWebsiteIds` không được rỗng
- `threshold1Percent` phải từ 1–99
- Nếu có `threshold2Percent`: phải từ 1–99 và **lớn hơn** `threshold1Percent`
- Nếu có `threshold2Percent` thì bắt buộc có `threshold2Message`
- Logic upsert: nếu đã có config cho `allowedWebsiteId` đó thì update, chưa có thì insert mới

---

### 5. Cập nhật `ExtensionService` — method `UpdateHeartbeatAsync`

**File:** `Services/ExtensionService.cs`

Sau khi upsert `daily_usage_stats` như cũ, **thêm block kiểm tra warning** trước khi return:

```
Lấy website (AllowedWebsite) theo allowedWebsiteId
Nếu website.TimeLimitMinutes != null:
    limitSeconds = TimeLimitMinutes * 60
    limitExceeded = stat.TotalSeconds >= limitSeconds
    
    Nếu KHÔNG limitExceeded:
        Lấy WebsiteWarningConfig theo allowedWebsiteId (IsActive = true)
        Nếu config != null:
            usedPercent = (stat.TotalSeconds / limitSeconds) * 100
            
            Nếu !stat.Warning1Sent AND usedPercent >= config.Threshold1Percent:
                stat.Warning1Sent = true
                SaveChanges()
                Gọi SendWarningNotificationAsync(child, website, config.Threshold1Message, remainingSeconds)
            
            Else nếu config.Threshold2Percent != null
                AND !stat.Warning2Sent
                AND usedPercent >= config.Threshold2Percent:
                stat.Warning2Sent = true
                SaveChanges()
                Gọi SendWarningNotificationAsync(child, website, config.Threshold2Message, remainingSeconds)

Return limitExceeded
```

**Helper method `SendWarningNotificationAsync` cần tạo thêm trong cùng service:**
- Tìm tất cả `guardianIds` của con (`GuardianChildRelationships`)
- Format `remainingText`: nếu >= 60 giây thì hiện phút, không thì hiện giây
- Với mỗi guardian:
  - Insert `Notification` vào DB với:
    - `Title` = `"⏰ Cảnh báo thời gian — {domain}"`
    - `Message` = `"[{childName}] {customMessage} (Còn lại: {remainingText})"`
    - `Type` = `NotificationType.Warning`
    - `CreatedAt` = `DateTime.Now`
  - Push SignalR event `"TimeWarning"` tới group `"guardian_{guardianId}"` với payload:
    ```json
    {
      "childId": 1,
      "childName": "Minh Dương",
      "domain": "youtube.com",
      "message": "nội dung guardian đã viết",
      "remainingSeconds": 120,
      "notificationId": 42
    }
    ```

---

## Frontend — Những việc cần làm

### 1. Tạo API file

**File:** `src/api/warningConfig.api.ts`

```typescript
import axios from './axios';

export interface WarningConfig {
  id: number;
  allowedWebsiteId: number;
  domain?: string;
  threshold1Percent: number;
  threshold1Message: string;
  threshold2Percent?: number | null;
  threshold2Message?: string | null;
  isActive: boolean;
  updatedAt: string;
}

export interface UpsertWarningConfigPayload {
  allowedWebsiteIds: number[];
  threshold1Percent: number;
  threshold1Message: string;
  threshold2Percent?: number | null;
  threshold2Message?: string | null;
}

export const warningConfigApi = {
  getByWebsite: (allowedWebsiteId: number) =>
    axios.get<WarningConfig>(`/warning-configs`, { params: { allowedWebsiteId } }),

  getByChild: (childId: number) =>
    axios.get<WarningConfig[]>(`/warning-configs/by-child/${childId}`),

  upsert: (payload: UpsertWarningConfigPayload) =>
    axios.post(`/warning-configs`, payload),

  delete: (id: number) =>
    axios.delete(`/warning-configs/${id}`),
};
```

---

### 2. Tạo component `WarningConfigModal`

**File:** `src/components/WarningConfigModal.tsx`

**Props:**
```typescript
interface Props {
  childId: number;
  childName: string;
  websites: { id: number; domain: string; timeLimitMinutes?: number | null }[];
  onClose: () => void;
}
```

**Chỉ nhận websites có `timeLimitMinutes > 0`** (filter trong component).

**UI gồm 3 bước:**

**Bước 1 — Chọn website:**
- Grid checkbox các website có time limit
- Cho phép chọn nhiều cùng lúc
- Nếu website đã có config thì hiện badge "● Đã có config"
- Khi chọn đúng 1 website → tự load config sẵn có vào form

**Bước 2 — Mốc 1 (bắt buộc):**
- Slider `<input type="range">` từ 10–98, step 5
- Hiện preview realtime: với mỗi website được chọn, tính và hiện "khi X phút đã qua (còn ~Y phút)"
  - Công thức: `usedMinutes = timeLimitMinutes * percent / 100`, `remaining = timeLimitMinutes - usedMinutes`
- Textarea nội dung thông báo (maxLength 300, hiện đếm ký tự)

**Bước 3 — Mốc 2 (tuỳ chọn):**
- Nút toggle "Thêm mốc thứ hai / Bỏ mốc thứ hai"
- Khi bật: slider từ `(threshold1Percent + 5)` đến 99
- Validate: mốc 2 phải lớn hơn mốc 1, hiện lỗi nếu không đúng
- Textarea nội dung mốc 2

**Danh sách config hiện tại:**
- Hiện tất cả config đang active của con này
- Mỗi dòng có nút xoá (gọi `warningConfigApi.delete`)

**Nút Lưu:**
- Disable nếu: chưa chọn website, thiếu nội dung mốc 1, mốc 2 không hợp lệ
- Sau khi lưu thành công: đổi màu xanh lá + text "✓ Đã lưu!" trong 2 giây
- Invalidate query `['warning-configs', childId]` sau khi lưu/xoá

---

### 3. Gắn modal vào `ChildDetailPage`

**File:** `src/pages/ChildDetailPage.tsx`

Thêm vào phần header của trang:

```tsx
import { useState } from 'react';
import WarningConfigModal from '../components/WarningConfigModal';

// State
const [showWarningConfig, setShowWarningConfig] = useState(false);

// Websites có time limit
const timedWebsites = child.allowedWebsites?.map((w: any) => ({
  id: w.id,
  domain: w.domain,
  timeLimitMinutes: w.timeLimitMinutes,
})) ?? [];

// Nút (đặt cạnh các nút hiện có trong header trang)
<Button
  variant="outline"
  onClick={() => setShowWarningConfig(true)}
  className="rounded-2xl gap-2 font-bold"
>
  <Bell className="w-4 h-4" />
  Cấu hình cảnh báo
</Button>

// Modal
{showWarningConfig && (
  <WarningConfigModal
    childId={child.id}
    childName={child.fullName}
    websites={timedWebsites}
    onClose={() => setShowWarningConfig(false)}
  />
)}
```

---

### 4. Lắng nghe SignalR event `TimeWarning` (tuỳ chọn — nâng cao)

Trong `useExtensionMonitor.ts` hoặc `useSignalR.ts`, thêm:

```typescript
connection.on('TimeWarning', (data) => {
  toast.warning(`⏰ ${data.childName}: ${data.message}`);
  queryClient.invalidateQueries({ queryKey: ['notifications'] });
});
```

---

## Luồng hoạt động tổng thể

```
Guardian vào ChildDetailPage
  → Nhấn "Cấu hình cảnh báo"
  → Chọn 1 hoặc nhiều website
  → Kéo slider chọn % mốc 1 (VD: 80%)
  → Nhập nội dung: "Con sắp hết giờ rồi!"
  → Tuỳ chọn thêm mốc 2 (VD: 95%) + nội dung
  → Nhấn Lưu → POST /api/warning-configs

Con dùng web → Extension heartbeat 30s → POST /api/extension/heartbeat
  → Backend tính usedPercent
  → usedPercent >= 80% và warning1_sent = false
    → Đánh dấu warning1_sent = true
    → Insert Notification vào DB
    → Push SignalR "TimeWarning" tới guardian
  → Guardian nhận toast + notification trong panel chuông
```

---

## Checklist hoàn thành

- [ ] `WebsiteWarningConfig.cs` entity
- [ ] `DailyUsageStat.cs` thêm 2 property `Warning1Sent`, `Warning2Sent`
- [ ] `AppDbContext.cs` thêm `DbSet<WebsiteWarningConfig>`
- [ ] `WarningConfigController.cs` với 4 endpoint
- [ ] `ExtensionService.cs` cập nhật `UpdateHeartbeatAsync` + thêm `SendWarningNotificationAsync`
- [ ] `warningConfig.api.ts`
- [ ] `WarningConfigModal.tsx`
- [ ] `ChildDetailPage.tsx` thêm nút + modal
- [ ] (Tuỳ chọn) `useExtensionMonitor.ts` lắng nghe `TimeWarning`