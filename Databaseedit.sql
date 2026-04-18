-- ============================================================
--  DB ADDITIONS - Chạy THÊM vào database hiện có
--  KHÔNG chạy lại database.sql cũ
-- ============================================================

USE family_guardian;

-- ============================================================
-- TABLE MỚI: web_sessions
-- Lưu mỗi phiên truy cập web theo domain
-- Session = chuỗi request liên tục đến cùng domain
-- Nếu idle > 5 phút → đóng session, tạo session mới
-- ============================================================
CREATE TABLE IF NOT EXISTS web_sessions (
    id                  BIGINT AUTO_INCREMENT PRIMARY KEY,
    child_id            INT NOT NULL,
    allowed_website_id  INT NOT NULL,
    domain              VARCHAR(255) NOT NULL,
    started_at          TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_activity_at    TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    ended_at            TIMESTAMP NULL,                 -- NULL nếu chưa đóng
    duration_seconds    INT NOT NULL DEFAULT 0,         -- tính khi đóng session
    FOREIGN KEY (child_id)           REFERENCES users(id) ON DELETE CASCADE,
    FOREIGN KEY (allowed_website_id) REFERENCES allowed_websites(id) ON DELETE CASCADE,
    INDEX idx_child_date (child_id, started_at),
    INDEX idx_child_website (child_id, allowed_website_id),
    INDEX idx_open_sessions (ended_at, last_activity_at)  -- để CloseIdleSessionsJob query nhanh
);

-- ============================================================
-- Thêm cột vào bảng daily_usage_stats nếu chưa có
-- (an toàn khi chạy lại, dùng IF NOT EXISTS sẽ báo lỗi trong MySQL
--  nên dùng stored procedure để check trước)
-- ============================================================
DELIMITER $$
CREATE PROCEDURE IF NOT EXISTS add_column_if_not_exists(
    IN p_table VARCHAR(100),
    IN p_column VARCHAR(100),
    IN p_definition TEXT
)
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE()
          AND table_name   = p_table
          AND column_name  = p_column
    ) THEN
        SET @sql = CONCAT('ALTER TABLE `', p_table, '` ADD COLUMN `', p_column, '` ', p_definition);
        PREPARE stmt FROM @sql;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;
    END IF;
END$$
DELIMITER ;

-- Thêm session_count vào daily_usage_stats (nếu chưa có)
CALL add_column_if_not_exists('daily_usage_stats', 'session_count',
    'INT NOT NULL DEFAULT 0 COMMENT "Số phiên truy cập trong ngày"');

-- ============================================================
-- Stored Procedure: Lấy thống kê thời gian sử dụng theo child
-- Dùng cho API GET /api/children/{childId}/logs/daily-usage
-- ============================================================
DROP PROCEDURE IF EXISTS sp_GetDailyUsage;

DELIMITER $$
CREATE PROCEDURE sp_GetDailyUsage(
    IN p_child_id  INT,
    IN p_date      DATE
)
BEGIN
    SELECT
        dus.domain,
        aw.display_name,
        aw.favicon_url,
        aw.is_active,
        dus.total_seconds,
        dus.request_count,
        dus.session_count,
        dus.usage_date,
        aw.time_limit_minutes,
        -- Tính phần trăm sử dụng so với giới hạn
        CASE
            WHEN aw.time_limit_minutes IS NULL THEN NULL
            ELSE LEAST(100, ROUND(dus.total_seconds * 100.0 / (aw.time_limit_minutes * 60), 1))
        END AS usage_percent,
        -- Đã vượt giới hạn chưa
        CASE
            WHEN aw.time_limit_minutes IS NULL THEN FALSE
            WHEN dus.total_seconds >= (aw.time_limit_minutes * 60) THEN TRUE
            ELSE FALSE
        END AS limit_exceeded,
        -- Thời gian còn lại (giây)
        CASE
            WHEN aw.time_limit_minutes IS NULL THEN NULL
            ELSE GREATEST(0, (aw.time_limit_minutes * 60) - dus.total_seconds)
        END AS remaining_seconds
    FROM daily_usage_stats dus
    INNER JOIN allowed_websites aw
           ON aw.id = dus.allowed_website_id
    WHERE dus.child_id  = p_child_id
      AND dus.usage_date = p_date
    ORDER BY dus.total_seconds DESC;
END$$
DELIMITER ;

-- ============================================================
-- Stored Procedure: Lấy lịch sử session theo child (cho API)
-- ============================================================
DROP PROCEDURE IF EXISTS sp_GetSessionHistory;

DELIMITER $$
CREATE PROCEDURE sp_GetSessionHistory(
    IN p_child_id   INT,
    IN p_from_date  DATE,
    IN p_to_date    DATE,
    IN p_page       INT,
    IN p_page_size  INT
)
BEGIN
    DECLARE v_offset INT DEFAULT (p_page - 1) * p_page_size;

    -- Kết quả chính
    SELECT
        ws.id,
        ws.domain,
        aw.display_name,
        aw.favicon_url,
        ws.started_at,
        ws.ended_at,
        ws.duration_seconds,
        ws.last_activity_at,
        CASE WHEN ws.ended_at IS NULL THEN TRUE ELSE FALSE END AS is_active
    FROM web_sessions ws
    INNER JOIN allowed_websites aw ON aw.id = ws.allowed_website_id
    WHERE ws.child_id = p_child_id
      AND DATE(ws.started_at) BETWEEN p_from_date AND p_to_date
    ORDER BY ws.started_at DESC
    LIMIT p_page_size OFFSET v_offset;

    -- Tổng count
    SELECT COUNT(*) AS total_count
    FROM web_sessions
    WHERE child_id = p_child_id
      AND DATE(started_at) BETWEEN p_from_date AND p_to_date;
END$$
DELIMITER ;

-- ============================================================
-- Stored Procedure: Thống kê 7 ngày gần nhất (cho chart)
-- ============================================================
DROP PROCEDURE IF EXISTS sp_GetUsageSummary;

DELIMITER $$
CREATE PROCEDURE sp_GetUsageSummary(
    IN p_child_id  INT,
    IN p_days      INT    -- số ngày nhìn lại, VD: 7
)
BEGIN
    -- Tổng theo domain
    SELECT
        dus.domain,
        aw.display_name,
        aw.favicon_url,
        SUM(dus.total_seconds) AS total_seconds,
        SUM(dus.request_count) AS total_requests,
        aw.time_limit_minutes
    FROM daily_usage_stats dus
    INNER JOIN allowed_websites aw ON aw.id = dus.allowed_website_id
    WHERE dus.child_id  = p_child_id
      AND dus.usage_date >= DATE_SUB(CURDATE(), INTERVAL p_days DAY)
    GROUP BY dus.domain, aw.display_name, aw.favicon_url, aw.time_limit_minutes
    ORDER BY total_seconds DESC;

    -- Breakdown theo ngày (cho line chart)
    SELECT
        dus.usage_date,
        SUM(dus.total_seconds) AS day_total_seconds
    FROM daily_usage_stats dus
    WHERE dus.child_id  = p_child_id
      AND dus.usage_date >= DATE_SUB(CURDATE(), INTERVAL p_days DAY)
    GROUP BY dus.usage_date
    ORDER BY dus.usage_date;
END$$
DELIMITER ;