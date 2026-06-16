# HOTFIX — Lỗi CS0236: NotificationType property trùng tên enum

> **Lỗi:** `CS0236: A field initializer cannot reference the non-static field, method, or property 'Notification.NotificationType'`
> **Nguyên nhân:** Property `NotificationType` trùng tên với enum `NotificationType` trong cùng file → C# không phân biệt được ở dòng `= NotificationType.Reminder`
> **Fix:** Đổi tên property thành `AlertType` (map cùng cột DB `notification_type`)

---

## Bước 1 — Sửa `Models/Entities/Notification.cs`

Thay toàn bộ nội dung file:

```csharp
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FamilyGuardian.Api.Models.Entities;

public class Notification
{
    public int Id { get; set; }
    public int GuardianId { get; set; }
    public int ChildId { get; set; }
    public string Title { get; set; } = null!;
    public string Message { get; set; } = null!;
    public NotificationType Type { get; set; } = NotificationType.Reminder;

    [Column("notification_type")]
    [MaxLength(50)]
    public string? AlertType { get; set; }
    // null = thông thường | "tamper_alert" = extension bị tắt đột ngột

    public bool IsRead { get; set; } = false;
    public DateTime? ScheduledAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public User Guardian { get; set; } = null!;
    public User Child { get; set; } = null!;
}

public enum NotificationType { Reminder, Warning, Info }
```

---

## Bước 2 — Tìm và đổi `.NotificationType` → `.AlertType` trong backend

Chạy lệnh tìm kiếm trong thư mục `FamilyGuardian.Api/`:

```
Tìm: .NotificationType
Trong: **/*.cs (trừ Notification.cs vừa sửa)
```

Các file **có thể** bị ảnh hưởng — kiểm tra từng file:

### `Controllers/NotificationsController.cs`

Tìm chỗ mapping entity → DTO, ví dụ:
```csharp
// Sai (cũ):
NotificationType = n.NotificationType,

// Đúng (sửa):
NotificationType = n.AlertType,
```

### `Services/ExtensionMonitorService.cs` (hoặc file tạo tamper notification)

Tìm chỗ set giá trị khi tạo notification mới:
```csharp
// Sai (cũ):
NotificationType = "tamper_alert",

// Đúng (sửa):
AlertType = "tamper_alert",
```

### DTO — `NotificationDto.cs` hoặc anonymous object trong controller

DTO trả về frontend **giữ nguyên tên `NotificationType`** để frontend không bị ảnh hưởng:
```csharp
public class NotificationDto
{
    public int Id { get; set; }
    public string Title { get; set; } = null!;
    public string Message { get; set; } = null!;
    public string? NotificationType { get; set; }  // ← Giữ tên này cho JSON response
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

Khi mapping:
```csharp
// Entity.AlertType → DTO.NotificationType
new NotificationDto
{
    Id = n.Id,
    Title = n.Title,
    Message = n.Message,
    NotificationType = n.AlertType,  // ← map AlertType sang NotificationType
    IsRead = n.IsRead,
    CreatedAt = n.CreatedAt,
}
```

---

## Bước 3 — Build kiểm tra

```bash
cd FamilyGuardian.Api
dotnet build
```

**Kết quả mong đợi:** 0 errors.

Nếu vẫn còn lỗi liên quan `NotificationType` → tìm thêm file còn sót bằng:
- VS Code: `Ctrl+Shift+F` → tìm `\.NotificationType` trong `*.cs`
- Hoặc tìm `notification.NotificationType` (chữ thường)

---

## Lưu ý

- **DB không đổi** — cột vẫn là `notification_type`, `[Column("notification_type")]` map đúng
- **Frontend không đổi** — JSON vẫn trả về field `notificationType` như cũ
- **Chỉ thay đổi tên property C# nội bộ** từ `NotificationType` → `AlertType`
