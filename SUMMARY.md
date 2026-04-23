# Tóm tắt toàn bộ dự án Family Guardian

---

## 1. Tổng quan dự án

**Mục tiêu:** Xây dựng hệ thống kiểm soát truy cập web cho trẻ em theo tài khoản Google, tương tự Google Family Link.

**Stack công nghệ:**
- Backend: ASP.NET Core 8 + MySQL + Entity Framework Core
- Frontend: React Vite + TypeScript + TanStack Query + SignalR
- Extension: Chrome Extension (Manifest V3)
- Auth: Google OAuth + JWT

**3 Role:** Admin, Guardian (bố/mẹ), Child (con)

---

## 2. Database Schema

### Bảng chính
| Bảng | Mục đích |
|---|---|
| `users` | Tất cả người dùng, role: admin/guardian/child |
| `guardian_child_relationships` | Quan hệ bố/mẹ ↔ con |
| `allowed_websites` | Danh sách web được phép cho từng con |
| `web_access_logs` | Log mọi lần truy cập web |
| `daily_usage_stats` | Thống kê thời gian dùng web theo ngày |
| `web_sessions` | Phiên truy cập web |
| `user_online_status` | Trạng thái online của user |
| `notifications` | Thông báo từ hệ thống |
| `refresh_tokens` | JWT refresh tokens |
| `website_check_cache` | Cache kiểm tra domain an toàn |

### Các thay đổi DB trong quá trình phát triển

```sql
-- 1. Xóa bảng proxy (không dùng nữa)
DROP TABLE IF EXISTS proxy_ip_mappings;

-- 2. Thêm cột filter_enabled vào users
ALTER TABLE users
  ADD COLUMN filter_enabled TINYINT(1) NOT NULL DEFAULT 0
  COMMENT 'Bật/tắt bộ lọc web cho tài khoản con'
  AFTER is_active;

-- 3. Thêm cột tracking extension vào user_online_status
ALTER TABLE user_online_status
  ADD COLUMN extension_last_seen TIMESTAMP NULL,
  ADD COLUMN extension_active TINYINT(1) NOT NULL DEFAULT 0;
```

### Stored Procedures quan trọng

**`sp_ExtensionCheckAccess(google_id, domain)`**
- Nhận google_id từ extension
- Tìm tài khoản con theo google_id
- Kiểm tra filter_enabled
- Kiểm tra domain có trong whitelist không
- Kiểm tra khung giờ và giới hạn thời gian
- Trả về: allowed/blocked + reason

**`sp_GetGuardianChildren(guardian_id)`** — đã thêm `u.filter_enabled` vào SELECT

---

## 3. Backend API

### Controllers

#### `ExtensionController` — `/api/extension`

| Method | Endpoint | Mô tả |
|---|---|---|
| GET | `/check?domain=x` | Extension kiểm tra domain có được phép không |
| GET | `/config` | Extension lấy cấu hình (filter_enabled, tên con) |
| POST | `/heartbeat` | Tracking thời gian dùng web (mỗi 30s) |
| POST | `/ping` | Extension báo hiệu còn sống (mỗi 10s) |

**Response `/check`:**
```json
{
  "allowed": false,
  "reason": "Không có trong danh sách cho phép",
  "domain": "tiktok.com",
  "allowedWebsiteId": null
}
```

**Response `/heartbeat`:**
```json
{
  "success": true,
  "limitExceeded": true
}
```

#### `ChildrenController` — `/api/children`

| Method | Endpoint | Mô tả |
|---|---|---|
| GET | `/` | Lấy danh sách con của guardian |
| GET | `/{childId}` | Chi tiết con (có filterEnabled) |
| DELETE | `/{childId}` | Xóa liên kết con |
| PATCH | `/{childId}/filter` | Bật/tắt bộ lọc web |

#### `NotificationsController` — `/api/notifications`

| Method | Endpoint | Mô tả |
|---|---|---|
| GET | `/` | Lấy tất cả thông báo (query GuardianId) |
| GET | `/unread` | Thông báo chưa đọc |
| GET | `/history` | Lịch sử thông báo |
| POST | `/to-child/{childId}` | Gửi thông báo cho con |
| PATCH | `/{id}/read` | Đánh dấu đã đọc |
| PATCH | `/read-all` | Đánh dấu tất cả đã đọc |

---

### Services

#### `ExtensionService`
```csharp
Task<ExtensionCheckResponse> CheckAccessAsync(string googleId, string domain);
Task<ExtensionConfigResponse?> GetConfigAsync(string googleId);
Task<bool> UpdateHeartbeatAsync(string googleId, string domain, int? allowedWebsiteId);
Task UpdateExtensionPingAsync(string googleId);
Task<bool> ToggleFilterAsync(int childId, bool enabled, int requestingGuardianId);
```

**`UpdateHeartbeatAsync` trả về `bool`** — `true` nếu hết giờ → extension redirect sang blocked page ngay.

#### `ExtensionMonitorService` (BackgroundService)
- Chạy mỗi **10 giây**
- Tìm con có `extension_active = true` và `extension_last_seen < NOW() - 20s`
- Đánh dấu `extension_active = false`
- **Lưu vào bảng `notifications`**
- Push SignalR event `ExtensionOffline` tới guardian

```csharp
// Đăng ký trong Program.cs
builder.Services.AddHostedService<ExtensionMonitorService>();
```

#### `NotificationService` — Lưu ý quan trọng
Query phải dùng `GuardianId` (không phải `ChildId`) vì thông báo extension tắt gửi cho **bố/mẹ**:
```csharp
.Where(n => n.GuardianId == userId)  // ✅ đúng
// KHÔNG dùng n.ChildId == userId    // ❌ sai
```

---

### SignalR Hub — `NotificationHub`

```csharp
[Authorize]
public class NotificationHub : Hub
{
    public async Task JoinGuardianGroup(string guardianId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"guardian_{guardianId}");
    }
}
```

**Đăng ký trong `Program.cs`:**
```csharp
builder.Services.AddSignalR();
app.MapHub<NotificationHub>("/hubs/notifications");

// JWT cho SignalR (query string)
opt.Events = new JwtBearerEvents
{
    OnMessageReceived = ctx =>
    {
        var accessToken = ctx.Request.Query["access_token"];
        var path = ctx.HttpContext.Request.Path;
        if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
            ctx.Token = accessToken;
        return Task.CompletedTask;
    }
};
```

---

### Data Models quan trọng

#### `ChildSpResult` — thêm field
```csharp
[Column("filter_enabled")] public bool FilterEnabled { get; set; }
```

#### `UserOnlineStatus` — thêm 2 field
```csharp
public DateTime? ExtensionLastSeen { get; set; }
public bool ExtensionActive { get; set; } = false;
```

#### `ChildDetailDto` — có FilterEnabled
```csharp
public bool FilterEnabled { get; set; }
```

---

## 4. Chrome Extension

### Cấu trúc file
```
family-guardian-extension/
├── manifest.json
├── background.js      ← Service worker chính
├── popup.html         ← UI khi click icon
├── popup.js
├── blocked.html       ← Trang hiện khi bị chặn
├── blocked.js
└── icons/
    ├── icon16.png
    ├── icon48.png
    └── icon128.png
```

### `manifest.json`
```json
{
  "manifest_version": 3,
  "name": "Family Guardian — Bộ lọc web",
  "permissions": ["identity", "webRequest", "storage", "alarms"],
  "host_permissions": ["<all_urls>"],
  "background": { "service_worker": "background.js" },
  "action": { "default_popup": "popup.html" },
  "oauth2": {
    "client_id": "YOUR_GOOGLE_CLIENT_ID.apps.googleusercontent.com",
    "scopes": ["openid", "email", "profile"]
  }
}
```

### `background.js` — Logic chính

**Cấu hình:**
```javascript
const CONFIG = {
  API_BASE: "http://localhost:5247/api/extension",
  CACHE_TTL_MS: 1 * 60 * 1000,   // Cache 1 phút
};
```

**2 Alarm:**
- `heartbeat` — mỗi **30 giây** — tracking thời gian dùng web
- `ping` — mỗi **10 giây** — báo hiệu extension còn sống

**Luồng chặn web:**
```
Con mở tab → onUpdated trigger
  → checkDomain(domain)
  → Gọi GET /api/extension/check?domain=...
  → Nếu allowed: false → redirect sang blocked.html
  → Nếu allowed: true → cập nhật activeTab cho heartbeat
```

**Luồng heartbeat:**
```
Alarm heartbeat (30s)
  → Gửi POST /api/extension/heartbeat
  → Nhận { limitExceeded: true/false }
  → Nếu limitExceeded → xóa cache + redirect blocked.html ngay
```

**Luồng ping:**
```
Alarm ping (10s)
  → Gửi POST /api/extension/ping
  → Server cập nhật extension_last_seen = NOW()
```

**Bug đã fix:**
- Field name: `allowed_website_id` (snake_case) → `allowedWebsiteId` (camelCase)
- Cache TTL: 5 phút → 1 phút để phát hiện hết giờ nhanh hơn
- Skip URL nội bộ: `chrome://`, `about:`, localhost, IP private

### `blocked.html` + `blocked.js`
Hiển thị domain bị chặn từ URL params:
```javascript
const params = new URLSearchParams(location.search);
document.getElementById("domain-display").textContent = params.get("domain");
document.getElementById("reason-display").textContent = params.get("reason");
```

---

## 5. Frontend (React)

### Hooks

#### `useExtensionMonitor.ts`
- Kết nối SignalR hub
- Chỉ chạy với role `Guardian`
- Lắng nghe event `ExtensionOffline`
- Hiện toast cảnh báo + thêm vào notification store
- Invalidate queries: `notifications`, `children`, `child/{id}`

```typescript
connection.on('ExtensionOffline', (data) => {
  toast.warning(`⚠️ ${data.childName} vừa tắt extension!`);
  queryClient.invalidateQueries({ queryKey: ['notifications'] });
});
```

**Dùng trong `AppLayout.tsx`:**
```typescript
import { useExtensionMonitor } from '../../hooks/useExtensionMonitor';

export default function AppLayout({ children }) {
  useExtensionMonitor(); // ✅
  // ...
}
```

#### `useSignalR.ts`
- Kết nối chung SignalR
- Lắng nghe `ChildStatusChanged`, `ReceiveNotification`

### Components

#### `FilterToggle.tsx`
- Toggle bật/tắt bộ lọc web cho con
- Gọi `PATCH /api/children/{childId}/filter`
- Invalidate cả 2 query: `['children']` và `['child', childId]`
- Dùng `useEffect` để sync với `initialEnabled` khi props thay đổi:

```typescript
useEffect(() => {
  setEnabled(initialEnabled);
}, [initialEnabled]);
```

#### `WebsiteCard` + Progress bar thời gian
Trong `ChildDetailPage.tsx`, mỗi website có giới hạn thời gian hiển thị:
```tsx
{web.timeLimitMinutes && (
  <div className="bg-white rounded-2xl px-5 py-4 border">
    <div className="progress-bar" style={{ width: `${usedPercent}%` }} />
    <span>{web.limitExceeded ? "⛔ Đã hết thời gian" : `Còn lại ${remainingMinutes} phút`}</span>
  </div>
)}
```

**Refetch interval:** `30_000ms` (khớp với heartbeat 30s)

#### `Topbar.tsx`
- Bell icon nhấn vào → navigate tới `/notifications`
- Dropdown avatar: text đậm hơn, hover rõ ràng hơn, thêm icon

### Pages

#### `NotificationsPage`
- Query key: `['notifications']`
- Hiển thị danh sách thông báo với icon theo type (warning/info/reminder)
- Nút đánh dấu đã đọc từng cái + đánh dấu tất cả

---

## 6. Luồng hoạt động hoàn chỉnh

### Luồng chặn web theo tài khoản
```
1. Bố/mẹ: Dashboard → chọn con → thêm domain → bật filter
2. Con: Cài extension → đăng nhập Google → extension tự nhận config
3. Con mở tab → extension gọi /check → backend tra DB → trả allowed/blocked
4. Nếu blocked → trang blocked.html hiện lên
```

### Luồng tracking thời gian
```
1. Extension heartbeat mỗi 30s → POST /heartbeat
2. Backend upsert daily_usage_stats (+30 giây)
3. Kiểm tra có vượt time_limit_minutes không
4. Nếu vượt → trả limitExceeded: true
5. Extension redirect blocked.html ngay lập tức
6. Frontend refetch mỗi 30s → hiện progress bar cập nhật
```

### Luồng phát hiện extension tắt
```
1. Extension ping mỗi 10s → POST /ping → server cập nhật extension_last_seen
2. BackgroundService kiểm tra mỗi 10s
3. Nếu extension_last_seen < NOW() - 20s → extension đã tắt
4. Lưu notification vào DB
5. Push SignalR "ExtensionOffline" → guardian nhận ngay
6. Frontend hiện toast + refresh trang thông báo
Độ trễ: 10-20 giây
```

---

## 7. Các bug đã fix

| Bug | Nguyên nhân | Giải pháp |
|---|---|---|
| Web được phép vẫn bị chặn | Cloudflare detect Titanium MITM proxy | Bỏ proxy, chuyển sang Chrome Extension |
| Extension không chặn được | Dùng `webRequest` blocking sai cách | Dùng `tabs.onUpdated` redirect |
| Heartbeat không ghi DB | `allowed_website_id` snake_case → JS gửi null | Đổi thành `allowedWebsiteId` camelCase |
| Dashboard hiện filter sai | `ChildSpResult` thiếu `FilterEnabled` | Thêm field + sửa SP thêm `u.filter_enabled` |
| Thời gian đứng im | `refetchInterval: 60_000` quá chậm | Giảm xuống `30_000` |
| SignalR 401 | Token không được truyền qua query string | Thêm `OnMessageReceived` trong JWT config |
| Thông báo trống | Query `ChildId` thay vì `GuardianId` | Sửa tất cả query sang `GuardianId` |
| "7 giờ trước" thay vì vừa xong | Lưu `DateTime.UtcNow`, frontend tính local | Đổi sang `DateTime.Now` |
| AmbiguousMatchException | `ToggleFilter` định nghĩa ở 2 controller | Xóa khỏi `ExtensionController`, giữ trong `ChildrenController` |

---

## 8. Giới hạn hệ thống

| Vấn đề | Trạng thái |
|---|---|
| Con dùng Firefox/Edge bypass | ❌ Không ngăn được |
| Con tắt extension | ❌ Không ngăn được, nhưng bố/mẹ được thông báo trong 20s |
| Chỉ hoạt động trong Chrome browser | ✅ Chấp nhận được |
| App native (TikTok app, game) không bị chặn | ❌ Giới hạn của extension |
| Incognito mode | ❌ Có thể bypass (tắt bằng Group Policy Windows) |

---

## 9. Điểm khác biệt của hệ thống

- **Chặn theo tài khoản Google** — không theo IP hay thiết bị → con dùng máy nào cũng bị quản lý
- **Thông báo realtime** khi con tắt extension (độ trễ 10-20s) qua SignalR
- **Tracking thời gian** chính xác theo từng domain với heartbeat 30s
- **Giới hạn thời gian** tự động chặn khi hết giờ ngay lập tức
- **Khung giờ** — chỉ cho phép truy cập trong giờ quy định
- **Giao diện tiếng Việt** hoàn toàn

---

## 10. Thứ tự setup

```
1. Chạy MySQL → import schema + stored procedures
2. Chạy ALTER TABLE thêm filter_enabled + extension columns
3. Cấu hình Google OAuth Client ID
4. Chạy backend: dotnet run
5. Cài npm packages frontend: npm install @microsoft/signalr
6. Chạy frontend: npm run dev
7. Load extension: chrome://extensions → Developer mode → Load unpacked
8. Điền Client ID vào manifest.json
9. Điền API_BASE URL vào background.js
10. Test: đăng nhập guardian → thêm con → thêm domain → bật filter → cài extension trên Chrome con
```
