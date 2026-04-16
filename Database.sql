-- ============================================================
--  FAMILY GUARDIAN - MySQL Database Schema v2.0
--  Luồng chức năng:
--  1. Bố mẹ đăng nhập Google → lấy avatar + tên
--  2. Bố mẹ link tài khoản con qua Google Login
--  3. Bố mẹ thêm rule cho phép web + time limit
--  4. Proxy ASP.NET Core chặn web không có rule
--  5. Hiển thị thời gian sử dụng từng web của con
-- ============================================================

CREATE DATABASE IF NOT EXISTS family_guardian
  CHARACTER SET utf8mb4
  COLLATE utf8mb4_unicode_ci;

USE family_guardian;

-- ============================================================
-- TABLE: users
-- Lưu tất cả user: admin, guardian (bố mẹ), child (con)
-- Bố mẹ: đăng nhập Google → google_id, email, full_name, avatar_url lấy từ Google
-- Con: liên kết qua Google Login hoặc tạo thủ công
-- ============================================================
CREATE TABLE users (
    id            INT AUTO_INCREMENT PRIMARY KEY,
    google_id     VARCHAR(255) UNIQUE,              -- Google sub claim (NULL nếu tạo thủ công)
    email         VARCHAR(255) NOT NULL UNIQUE,
    full_name     VARCHAR(255) NOT NULL,
    avatar_url    VARCHAR(1000),                    -- URL ảnh đại diện từ Google
    role          ENUM('admin','guardian','child') NOT NULL DEFAULT 'guardian',
    is_active     BOOLEAN NOT NULL DEFAULT TRUE,
    created_at    TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at    TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    INDEX idx_google_id (google_id),
    INDEX idx_role (role),
    INDEX idx_email (email)
);

-- ============================================================
-- TABLE: refresh_tokens
-- Lưu refresh token để tự động renew JWT
-- Tách riêng khỏi users để có thể revoke từng token
-- ============================================================
CREATE TABLE refresh_tokens (
    id            INT AUTO_INCREMENT PRIMARY KEY,
    user_id       INT NOT NULL,
    token         VARCHAR(500) NOT NULL UNIQUE,
    expires_at    TIMESTAMP NOT NULL,
    is_revoked    BOOLEAN NOT NULL DEFAULT FALSE,
    created_at    TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
    INDEX idx_token (token),
    INDEX idx_user_id (user_id)
);

-- ============================================================
-- TABLE: guardian_child_relationships
-- Quan hệ bố mẹ → con (1 bố mẹ quản lý nhiều con)
-- ============================================================
CREATE TABLE guardian_child_relationships (
    id           INT AUTO_INCREMENT PRIMARY KEY,
    guardian_id  INT NOT NULL,
    child_id     INT NOT NULL,
    linked_at    TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (guardian_id) REFERENCES users(id) ON DELETE CASCADE,
    FOREIGN KEY (child_id)    REFERENCES users(id) ON DELETE CASCADE,
    UNIQUE KEY uq_guardian_child (guardian_id, child_id),
    INDEX idx_guardian (guardian_id),
    INDEX idx_child (child_id)
);

-- ============================================================
-- TABLE: allowed_websites
-- Danh sách web được bố mẹ cho phép con sử dụng
-- Mặc định: chặn tất cả, chỉ cho phép những web trong bảng này
-- Có thể thêm nhiều web (1 web, 2 web, ...)
-- ============================================================
CREATE TABLE allowed_websites (
    id                  INT AUTO_INCREMENT PRIMARY KEY,
    child_id            INT NOT NULL,
    domain              VARCHAR(255) NOT NULL,          -- đã normalize: youtube.com
    display_name        VARCHAR(255),                   -- tên hiển thị: YouTube
    favicon_url         VARCHAR(500),                   -- icon của web (lấy từ Google S2)
    is_active           BOOLEAN NOT NULL DEFAULT TRUE,
    -- Giới hạn thời gian sử dụng
    time_limit_minutes  INT DEFAULT NULL,               -- NULL = không giới hạn
    -- Khung giờ được phép dùng
    allowed_start_time  TIME DEFAULT NULL,              -- VD: 07:00:00
    allowed_end_time    TIME DEFAULT NULL,              -- VD: 21:00:00
    -- Thông tin kiểm tra website
    is_verified         BOOLEAN DEFAULT FALSE,          -- đã kiểm tra còn sống không
    is_safe             BOOLEAN DEFAULT NULL,           -- kết quả Google Safe Browsing
    http_status_code    INT DEFAULT NULL,               -- 200, 404, etc
    last_checked_at     TIMESTAMP NULL,                 -- lần kiểm tra cuối
    -- Meta
    added_by            INT NOT NULL,                   -- guardian_id
    created_at          TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    FOREIGN KEY (child_id)  REFERENCES users(id) ON DELETE CASCADE,
    FOREIGN KEY (added_by)  REFERENCES users(id),
    UNIQUE KEY uq_child_domain (child_id, domain),
    INDEX idx_child_active (child_id, is_active),
    INDEX idx_domain (domain)
);

-- ============================================================
-- TABLE: web_access_logs
-- Lịch sử truy cập web của con (qua proxy)
-- Ghi lại cả web được phép lẫn bị chặn
-- ============================================================
CREATE TABLE web_access_logs (
    id              BIGINT AUTO_INCREMENT PRIMARY KEY,
    child_id        INT NOT NULL,
    domain          VARCHAR(255) NOT NULL,
    full_url        VARCHAR(2000),
    access_result   ENUM('allowed','blocked') NOT NULL,
    -- allowed_website_id để join lấy thông tin web nếu được phép
    allowed_website_id INT DEFAULT NULL,
    session_start   TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    session_end     TIMESTAMP NULL,                     -- NULL nếu chưa kết thúc
    duration_seconds INT NOT NULL DEFAULT 0,
    FOREIGN KEY (child_id)            REFERENCES users(id) ON DELETE CASCADE,
    FOREIGN KEY (allowed_website_id)  REFERENCES allowed_websites(id) ON DELETE SET NULL,
    INDEX idx_child_date (child_id, session_start),
    INDEX idx_domain (domain),
    INDEX idx_result (access_result)
);

-- ============================================================
-- TABLE: daily_usage_stats
-- Thống kê thời gian sử dụng mỗi web theo ngày
-- Cập nhật sau mỗi session (hoặc theo heartbeat)
-- Bố mẹ xem được con dùng web đó bao lâu
-- ============================================================
CREATE TABLE daily_usage_stats (
    id                  INT AUTO_INCREMENT PRIMARY KEY,
    child_id            INT NOT NULL,
    allowed_website_id  INT NOT NULL,
    domain              VARCHAR(255) NOT NULL,
    usage_date          DATE NOT NULL,
    total_seconds       INT NOT NULL DEFAULT 0,         -- tổng giây trong ngày
    request_count       INT NOT NULL DEFAULT 0,         -- số lần truy cập
    last_updated        TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    FOREIGN KEY (child_id)           REFERENCES users(id) ON DELETE CASCADE,
    FOREIGN KEY (allowed_website_id) REFERENCES allowed_websites(id) ON DELETE CASCADE,
    UNIQUE KEY uq_child_web_date (child_id, allowed_website_id, usage_date),
    INDEX idx_child_date (child_id, usage_date)
);

-- ============================================================
-- TABLE: user_online_status
-- Trạng thái online/offline của user (cập nhật theo heartbeat)
-- ============================================================
CREATE TABLE user_online_status (
    user_id        INT PRIMARY KEY,
    is_online      BOOLEAN NOT NULL DEFAULT FALSE,
    last_seen_at   TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    ip_address     VARCHAR(45),                         -- IPv4 hoặc IPv6
    device_info    VARCHAR(255),                        -- OS, browser
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
);

-- ============================================================
-- TABLE: website_check_cache
-- Cache kết quả kiểm tra website (HTTP + Safe Browsing)
-- Tránh gọi API nhiều lần cho cùng domain
-- ============================================================
CREATE TABLE website_check_cache (
    domain           VARCHAR(255) PRIMARY KEY,
    is_reachable     BOOLEAN,
    http_status_code INT,
    response_time_ms INT,
    is_safe          BOOLEAN,
    threat_type      VARCHAR(100),                      -- MALWARE, SOCIAL_ENGINEERING, etc
    favicon_url      VARCHAR(500),
    display_name     VARCHAR(255),
    checked_at       TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    expires_at       TIMESTAMP NOT NULL                 -- sau thời gian này phải check lại
);

-- ============================================================
-- TABLE: notifications
-- Thông báo nhắc nhở từ bố mẹ gửi cho con
-- ============================================================
CREATE TABLE notifications (
    id                INT AUTO_INCREMENT PRIMARY KEY,
    guardian_id       INT NOT NULL,
    child_id          INT NOT NULL,
    title             VARCHAR(255) NOT NULL,
    message           TEXT NOT NULL,
    type              ENUM('reminder','warning','info') NOT NULL DEFAULT 'reminder',
    is_read           BOOLEAN NOT NULL DEFAULT FALSE,
    scheduled_at      TIMESTAMP NULL,                   -- NULL = gửi ngay
    sent_at           TIMESTAMP NULL,
    created_at        TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (guardian_id) REFERENCES users(id) ON DELETE CASCADE,
    FOREIGN KEY (child_id)    REFERENCES users(id) ON DELETE CASCADE,
    INDEX idx_child_unread (child_id, is_read),
    INDEX idx_scheduled (scheduled_at)
);

-- ============================================================
-- TABLE: system_settings
-- Cài đặt hệ thống (proxy port, timeouts, ...)
-- ============================================================
CREATE TABLE system_settings (
    setting_key   VARCHAR(100) PRIMARY KEY,
    setting_value TEXT,
    description   VARCHAR(500),
    updated_at    TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);

-- ============================================================
-- TABLE: proxy_ip_mappings
-- Map IP máy của con với child_id
-- Bố mẹ cấu hình trong app → proxy biết request từ IP nào là của con nào
-- ============================================================
CREATE TABLE proxy_ip_mappings (
    id          INT AUTO_INCREMENT PRIMARY KEY,
    child_id    INT NOT NULL,
    ip_address  VARCHAR(45) NOT NULL,
    device_name VARCHAR(255),                           -- "Máy tính con An"
    is_active   BOOLEAN NOT NULL DEFAULT TRUE,
    created_by  INT NOT NULL,
    created_at  TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (child_id)   REFERENCES users(id) ON DELETE CASCADE,
    FOREIGN KEY (created_by) REFERENCES users(id),
    UNIQUE KEY uq_ip (ip_address),
    INDEX idx_child (child_id)
);

-- ============================================================
-- DỮ LIỆU MẶC ĐỊNH
-- ============================================================
INSERT INTO system_settings (setting_key, setting_value, description) VALUES
('proxy_port',              '8888',  'Port TCP proxy lắng nghe'),
('proxy_enabled',           'true',  'Bật/tắt proxy server'),
('default_block_all',       'true',  'Mặc định chặn tất cả web (chỉ cho phép web trong danh sách)'),
('website_check_timeout_ms','5000',  'Timeout kiểm tra website (ms)'),
('website_check_cache_min', '30',    'Cache kết quả check website bao nhiêu phút'),
('max_log_days',            '90',    'Giữ log bao nhiêu ngày'),
('heartbeat_interval_sec',  '30',    'Frontend gửi heartbeat mỗi bao nhiêu giây');

-- ============================================================
-- STORED PROCEDURES
-- ============================================================

DELIMITER $$

-- Lấy danh sách con của guardian kèm online status và stats hôm nay
CREATE PROCEDURE sp_GetGuardianChildren(IN p_guardian_id INT)
BEGIN
    SELECT
        u.id,
        u.full_name,
        u.email,
        u.avatar_url,
        u.is_active,
        u.created_at,
        COALESCE(os.is_online, FALSE)          AS is_online,
        os.last_seen_at,
        os.ip_address,
        -- Số web được phép
        (SELECT COUNT(*) FROM allowed_websites aw
         WHERE aw.child_id = u.id AND aw.is_active = TRUE) AS active_websites_count,
        -- Tổng giây dùng web hôm nay
        (SELECT COALESCE(SUM(dus.total_seconds), 0)
         FROM daily_usage_stats dus
         WHERE dus.child_id = u.id AND dus.usage_date = CURDATE()) AS today_total_seconds
    FROM users u
    INNER JOIN guardian_child_relationships gcr ON gcr.child_id = u.id AND gcr.guardian_id = p_guardian_id
    LEFT  JOIN user_online_status os ON os.user_id = u.id
    WHERE u.is_active = TRUE
    ORDER BY u.full_name;
END$$

-- Lấy danh sách web được phép của con kèm usage hôm nay
CREATE PROCEDURE sp_GetChildAllowedWebsites(IN p_child_id INT)
BEGIN
    SELECT
        aw.id,
        aw.domain,
        aw.display_name,
        aw.favicon_url,
        aw.is_active,
        aw.time_limit_minutes,
        aw.allowed_start_time,
        aw.allowed_end_time,
        aw.is_verified,
        aw.is_safe,
        aw.http_status_code,
        aw.last_checked_at,
        aw.created_at,
        -- Usage hôm nay
        COALESCE(dus.total_seconds, 0)          AS today_seconds,
        COALESCE(dus.request_count, 0)          AS today_requests,
        -- Có vượt giới hạn không
        CASE
            WHEN aw.time_limit_minutes IS NULL THEN FALSE
            WHEN COALESCE(dus.total_seconds, 0) >= (aw.time_limit_minutes * 60) THEN TRUE
            ELSE FALSE
        END AS limit_exceeded
    FROM allowed_websites aw
    LEFT JOIN daily_usage_stats dus
           ON dus.allowed_website_id = aw.id
          AND dus.usage_date = CURDATE()
    WHERE aw.child_id = p_child_id
    ORDER BY aw.is_active DESC, aw.domain;
END$$

-- Proxy gọi để kiểm tra child có được phép truy cập domain không
DELIMITER $$

CREATE PROCEDURE sp_CheckWebAccess(
    IN p_child_id INT,
    IN p_domain   VARCHAR(255)
)
proc: BEGIN

    DECLARE v_website_id     INT DEFAULT NULL;
    DECLARE v_time_limit     INT;
    DECLARE v_today_seconds  INT DEFAULT 0;
    DECLARE v_start_time     TIME;
    DECLARE v_end_time       TIME;

    -- Tim rule
    SELECT id, time_limit_minutes, allowed_start_time, allowed_end_time
    INTO v_website_id, v_time_limit, v_start_time, v_end_time
    FROM allowed_websites
    WHERE child_id = p_child_id
      AND (domain = p_domain OR p_domain LIKE CONCAT('%.', domain))
      AND is_active = TRUE
    ORDER BY LENGTH(domain) DESC
    LIMIT 1;

    IF v_website_id IS NULL THEN
        SELECT 'blocked', NULL, 'Không có trong danh sách cho phép';
        LEAVE proc;
    END IF;

    -- Kiem tra khung gio
    IF v_start_time IS NOT NULL AND v_end_time IS NOT NULL THEN
        IF CURTIME() < v_start_time OR CURTIME() > v_end_time THEN
            SELECT 'blocked', v_website_id,
                   CONCAT('Ngoài khung giờ (', v_start_time, ' - ', v_end_time, ')');
            LEAVE proc;
        END IF;
    END IF;

    -- Kiem tra thoi gian
    SELECT COALESCE(total_seconds, 0)
    INTO v_today_seconds
    FROM daily_usage_stats
    WHERE child_id = p_child_id
      AND allowed_website_id = v_website_id
      AND usage_date = CURDATE()
    LIMIT 1;

    IF v_time_limit IS NOT NULL AND v_today_seconds >= (v_time_limit * 60) THEN
        SELECT 'blocked', v_website_id,
               CONCAT('Hết thời gian (', v_time_limit, ' phút)');
        LEAVE proc;
    END IF;

    SELECT 'allowed', v_website_id, '';

END$$

DELIMITER ;

-- Upsert daily usage stats (proxy gọi sau mỗi request)
CREATE PROCEDURE sp_UpsertDailyUsage(
    IN p_child_id           INT,
    IN p_allowed_website_id INT,
    IN p_domain             VARCHAR(255),
    IN p_add_seconds        INT
)
BEGIN
    INSERT INTO daily_usage_stats (child_id, allowed_website_id, domain, usage_date, total_seconds, request_count)
    VALUES (p_child_id, p_allowed_website_id, p_domain, CURDATE(), p_add_seconds, 1)
    ON DUPLICATE KEY UPDATE
        total_seconds = total_seconds + p_add_seconds,
        request_count = request_count + 1;
END$$

-- Lấy thống kê usage nhiều ngày của 1 child
CREATE PROCEDURE sp_GetUsageHistory(
    IN p_child_id  INT,
    IN p_from_date DATE,
    IN p_to_date   DATE
)
BEGIN
    SELECT
        dus.usage_date,
        dus.domain,
        aw.display_name,
        aw.favicon_url,
        dus.total_seconds,
        dus.request_count,
        aw.time_limit_minutes,
        CASE
            WHEN aw.time_limit_minutes IS NULL THEN FALSE
            WHEN dus.total_seconds >= (aw.time_limit_minutes * 60) THEN TRUE
            ELSE FALSE
        END AS limit_exceeded
    FROM daily_usage_stats dus
    INNER JOIN allowed_websites aw ON aw.id = dus.allowed_website_id
    WHERE dus.child_id = p_child_id
      AND dus.usage_date BETWEEN p_from_date AND p_to_date
    ORDER BY dus.usage_date DESC, dus.total_seconds DESC;
END$$

-- Lịch sử truy cập của con (phân trang)
CREATE PROCEDURE sp_GetAccessLogs(
    IN p_child_id  INT,
    IN p_from_date DATE,
    IN p_to_date   DATE,
    IN p_page      INT,
    IN p_page_size INT
)
BEGIN
    DECLARE v_offset INT DEFAULT (p_page - 1) * p_page_size;

    SELECT
        wal.id,
        wal.domain,
        wal.full_url,
        wal.access_result,
        wal.duration_seconds,
        wal.session_start,
        wal.session_end,
        aw.display_name,
        aw.favicon_url
    FROM web_access_logs wal
    LEFT JOIN allowed_websites aw ON aw.id = wal.allowed_website_id
    WHERE wal.child_id = p_child_id
      AND DATE(wal.session_start) BETWEEN p_from_date AND p_to_date
    ORDER BY wal.session_start DESC
    LIMIT p_page_size OFFSET v_offset;

    SELECT COUNT(*) AS total FROM web_access_logs
    WHERE child_id = p_child_id
      AND DATE(session_start) BETWEEN p_from_date AND p_to_date;
END$$

-- Cập nhật online status
CREATE PROCEDURE sp_UpdateOnlineStatus(
    IN p_user_id    INT,
    IN p_is_online  BOOLEAN,
    IN p_ip         VARCHAR(45)
)
BEGIN
    INSERT INTO user_online_status (user_id, is_online, last_seen_at, ip_address)
    VALUES (p_user_id, p_is_online, NOW(), p_ip)
    ON DUPLICATE KEY UPDATE
        is_online    = p_is_online,
        last_seen_at = NOW(),
        ip_address   = COALESCE(p_ip, ip_address);
END$$

DELIMITER ;