# BACKEND — Việc cần làm tiếp theo

---

## 1. Thay đổi Database

### ❌ Xóa bảng không còn dùng
```sql
DROP TABLE IF EXISTS `proxy_ip_mappings`;
```
> Lý do: Đã bỏ hướng Proxy, chuyển sang Chrome Extension → bảng này vô nghĩa.

---

### ✅ Thêm cột vào bảng `users`
```sql
ALTER TABLE `users`
  ADD COLUMN `filter_enabled` TINYINT(1) NOT NULL DEFAULT 0
    COMMENT 'Bật/tắt bộ lọc web cho tài khoản con'
  AFTER `is_active`;
```
> Chỉ có ý nghĩa với role = 'child'. Guardian/Admin bỏ qua.

---

### ✅ Thêm Stored Procedure mới cho Extension

```sql
DROP PROCEDURE IF EXISTS `sp_ExtensionCheckAccess`;
DELIMITER ;;
CREATE PROCEDURE `sp_ExtensionCheckAccess`(
    IN p_google_id VARCHAR(255),
    IN p_domain    VARCHAR(255)
)
proc: BEGIN
    DECLARE v_child_id       INT;
    DECLARE v_filter_enabled TINYINT(1);
    DECLARE v_website_id     INT;
    DECLARE v_time_limit     INT;
    DECLARE v_today_seconds  INT DEFAULT 0;
    DECLARE v_start_time     TIME;
    DECLARE v_end_time       TIME;

    -- Tìm child theo google_id
    SELECT id, filter_enabled
    INTO v_child_id, v_filter_enabled
    FROM users
    WHERE google_id = p_google_id AND role = 'child' AND is_active = TRUE
    LIMIT 1;

    -- Không tìm thấy tài khoản
    IF v_child_id IS NULL THEN
        SELECT 'allowed', NULL, 'Không phải tài khoản con' AS reason;
        LEAVE proc;
    END IF;

    -- Bộ lọc đang tắt
    IF v_filter_enabled = 0 THEN
        SELECT 'allowed', NULL, 'Bộ lọc đang tắt' AS reason;
        LEAVE proc;
    END IF;

    -- Tìm rule khớp domain (hỗ trợ subdomain)
    SELECT id, time_limit_minutes, allowed_start_time, allowed_end_time
    INTO v_website_id, v_time_limit, v_start_time, v_end_time
    FROM allowed_websites
    WHERE child_id = v_child_id
      AND (domain = p_domain OR p_domain LIKE CONCAT('%.', domain))
      AND is_active = TRUE
    ORDER BY LENGTH(domain) DESC
    LIMIT 1;

    -- Không có trong whitelist
    IF v_website_id IS NULL THEN
        SELECT 'blocked', NULL, 'Không có trong danh sách cho phép' AS reason;
        LEAVE proc;
    END IF;

    -- Kiểm tra khung giờ
    IF v_start_time IS NOT NULL AND v_end_time IS NOT NULL THEN
        IF CURTIME() < v_start_time OR CURTIME() > v_end_time THEN
            SELECT 'blocked', v_website_id,
                   CONCAT('Ngoài khung giờ (', v_start_time, ' - ', v_end_time, ')') AS reason;
            LEAVE proc;
        END IF;
    END IF;

    -- Kiểm tra giới hạn thời gian
    SELECT COALESCE(total_seconds, 0) INTO v_today_seconds
    FROM daily_usage_stats
    WHERE child_id = v_child_id
      AND allowed_website_id = v_website_id
      AND usage_date = CURDATE()
    LIMIT 1;

    IF v_time_limit IS NOT NULL AND v_today_seconds >= (v_time_limit * 60) THEN
        SELECT 'blocked', v_website_id,
               CONCAT('Hết thời gian (', v_time_limit, ' phút/ngày)') AS reason;
        LEAVE proc;
    END IF;

    SELECT 'allowed', v_website_id, '' AS reason;
END ;;
DELIMITER ;
```

---

## 2. API Endpoints mới cần thêm

### Controller: `ExtensionController.cs`

---

#### `GET /api/extension/check`
Extension gọi endpoint này mỗi khi người dùng mở tab mới.

**Header:** `Authorization: Bearer <google_access_token>`

**Query:** `?domain=youtube.com`

**Logic:**
1. Nhận Google Access Token từ header
2. Gọi `https://www.googleapis.com/oauth2/v3/userinfo` để lấy `google_id`
3. Gọi `sp_ExtensionCheckAccess(google_id, domain)`
4. Nếu `access_result = 'allowed'` → ghi log + trả về `{ allowed: true }`
5. Nếu `access_result = 'blocked'` → ghi log + trả về `{ allowed: false, reason: "..." }`

**Response:**
```json
{
  "allowed": false,
  "reason": "Không có trong danh sách cho phép",
  "domain": "tiktok.com"
}
```

**Ghi log vào `web_access_logs`:**
```sql
INSERT INTO web_access_logs (child_id, domain, access_result, allowed_website_id, session_start)
VALUES (@child_id, @domain, @result, @website_id, NOW());
```

---

#### `GET /api/extension/config`
Extension gọi lúc khởi động để biết trạng thái hiện tại.

**Header:** `Authorization: Bearer <google_access_token>`

**Response:**
```json
{
  "filter_enabled": true,
  "child_id": 2,
  "full_name": "Nguyễn Văn Con",
  "block_page_url": "https://yourserver.com/blocked"
}
```

---

#### `PATCH /api/children/{childId}/filter`
Guardian bật/tắt bộ lọc cho con.

**Auth:** JWT của Guardian (role = guardian)

**Body:**
```json
{ "filter_enabled": true }
```

**Logic:**
1. Kiểm tra `guardian_child_relationships` — guardian có quyền quản lý `childId` không
2. Cập nhật `users.filter_enabled` cho `childId`

**Response:** `200 OK` hoặc `403 Forbidden`

---

#### `POST /api/extension/heartbeat`
Extension gửi mỗi 30 giây khi tab đang mở để tracking thời gian.

**Header:** `Authorization: Bearer <google_access_token>`

**Body:**
```json
{
  "domain": "youtube.com",
  "allowed_website_id": 3
}
```

**Logic:** Upsert `daily_usage_stats` — cộng thêm 30 giây vào `total_seconds`.

```sql
INSERT INTO daily_usage_stats (child_id, allowed_website_id, domain, usage_date, total_seconds, request_count)
VALUES (@child_id, @website_id, @domain, CURDATE(), 30, 1)
ON DUPLICATE KEY UPDATE
  total_seconds = total_seconds + 30,
  request_count = request_count + 1,
  last_updated  = NOW();
```

---

## 3. Cấu trúc thư mục Backend cần thêm

```
Controllers/
  ExtensionController.cs       ← MỚI

Services/
  ExtensionService.cs          ← MỚI (logic check + log)
  GoogleTokenService.cs        ← MỚI (verify Google token → lấy google_id)

Models/Requests/
  FilterToggleRequest.cs       ← MỚI
  HeartbeatRequest.cs          ← MỚI

Models/Responses/
  ExtensionCheckResponse.cs    ← MỚI
  ExtensionConfigResponse.cs   ← MỚI
```

---

## 4. Lưu ý CORS

Extension Chrome gọi API từ `chrome-extension://` origin → cần thêm vào CORS policy:

```csharp
// Program.cs
builder.Services.AddCors(options => {
    options.AddPolicy("ExtensionPolicy", policy => {
        policy.WithOrigins(
            "http://localhost:5173",           // React dev
            "https://yourserver.com",          // Production
            "chrome-extension://*"             // Chrome Extension
        )
        .AllowAnyMethod()
        .AllowAnyHeader();
    });
});
```

---

## 5. Thứ tự làm

1. Chạy SQL: xóa `proxy_ip_mappings`, thêm cột `filter_enabled`, thêm SP mới
2. Tạo `GoogleTokenService.cs` — verify Google token
3. Tạo `ExtensionService.cs` — gọi SP + ghi log
4. Tạo `ExtensionController.cs` — 4 endpoints
5. Cập nhật CORS trong `Program.cs`
6. Test bằng Postman với Google Access Token thật