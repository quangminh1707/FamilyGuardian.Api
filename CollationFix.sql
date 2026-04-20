-- ============================================================
-- COLLATION FIX - Fix MySQL collation mismatch errors
-- ============================================================
-- Issue: Illegal mix of collations (utf8mb4_unicode_ci) and (utf8mb4_0900_ai_ci)
-- Root cause: Different columns/parameters have different collations
-- Solution: Standardize all string columns to utf8mb4_unicode_ci

USE family_guardian;

-- ============================================================
-- Fix allowed_websites table - ensure all VARCHAR columns use correct collation
-- ============================================================

-- Fix domain column
ALTER TABLE `allowed_websites`
  MODIFY COLUMN `domain` VARCHAR(255) NOT NULL COLLATE utf8mb4_unicode_ci,
  MODIFY COLUMN `display_name` VARCHAR(255) COLLATE utf8mb4_unicode_ci,
  MODIFY COLUMN `favicon_url` VARCHAR(500) COLLATE utf8mb4_unicode_ci;

-- Fix users table - google_id and other string fields
ALTER TABLE `users`
  MODIFY COLUMN `google_id` VARCHAR(255) UNIQUE COLLATE utf8mb4_unicode_ci,
  MODIFY COLUMN `email` VARCHAR(255) NOT NULL UNIQUE COLLATE utf8mb4_unicode_ci,
  MODIFY COLUMN `full_name` VARCHAR(255) NOT NULL COLLATE utf8mb4_unicode_ci,
  MODIFY COLUMN `avatar_url` VARCHAR(1000) COLLATE utf8mb4_unicode_ci;

-- Fix web_access_logs
ALTER TABLE `web_access_logs`
  MODIFY COLUMN `domain` VARCHAR(255) NOT NULL COLLATE utf8mb4_unicode_ci,
  MODIFY COLUMN `full_url` VARCHAR(2000) COLLATE utf8mb4_unicode_ci;

-- Fix daily_usage_stats
ALTER TABLE `daily_usage_stats`
  MODIFY COLUMN `domain` VARCHAR(255) NOT NULL COLLATE utf8mb4_unicode_ci;

-- Fix user_online_status
ALTER TABLE `user_online_status`
  MODIFY COLUMN `ip_address` VARCHAR(45) COLLATE utf8mb4_unicode_ci,
  MODIFY COLUMN `device_info` VARCHAR(255) COLLATE utf8mb4_unicode_ci;

-- Fix website_check_cache
ALTER TABLE `website_check_cache`
  MODIFY COLUMN `domain` VARCHAR(255) PRIMARY KEY COLLATE utf8mb4_unicode_ci,
  MODIFY COLUMN `threat_type` VARCHAR(100) COLLATE utf8mb4_unicode_ci,
  MODIFY COLUMN `favicon_url` VARCHAR(500) COLLATE utf8mb4_unicode_ci,
  MODIFY COLUMN `display_name` VARCHAR(255) COLLATE utf8mb4_unicode_ci;

-- Fix notifications
ALTER TABLE `notifications`
  MODIFY COLUMN `title` VARCHAR(255) NOT NULL COLLATE utf8mb4_unicode_ci,
  MODIFY COLUMN `message` TEXT NOT NULL COLLATE utf8mb4_unicode_ci;

-- Fix proxy_ip_mappings (if still exists)
ALTER TABLE `proxy_ip_mappings`
  MODIFY COLUMN `ip_address` VARCHAR(45) NOT NULL COLLATE utf8mb4_unicode_ci,
  MODIFY COLUMN `device_name` VARCHAR(255) COLLATE utf8mb4_unicode_ci;

-- Fix web_sessions (if exists)
ALTER TABLE `web_sessions`
  MODIFY COLUMN `domain` VARCHAR(255) NOT NULL COLLATE utf8mb4_unicode_ci;

-- ============================================================
-- Recreate stored procedure with explicit collation in all comparisons
-- ============================================================
DROP PROCEDURE IF EXISTS `sp_ExtensionCheckAccess`;

DELIMITER $$

CREATE PROCEDURE `sp_ExtensionCheckAccess`(
    IN p_google_id VARCHAR(255) COLLATE utf8mb4_unicode_ci,
    IN p_domain    VARCHAR(255) COLLATE utf8mb4_unicode_ci
)
proc: BEGIN
    DECLARE v_child_id       INT;
    DECLARE v_filter_enabled TINYINT(1);
    DECLARE v_website_id     INT;
    DECLARE v_time_limit     INT;
    DECLARE v_today_seconds  INT DEFAULT 0;
    DECLARE v_start_time     TIME;
    DECLARE v_end_time       TIME;

    -- Tìm child theo google_id (with explicit collation)
    SELECT id, filter_enabled
    INTO v_child_id, v_filter_enabled
    FROM users
    WHERE google_id COLLATE utf8mb4_unicode_ci = p_google_id COLLATE utf8mb4_unicode_ci
      AND role = 'child'
      AND is_active = TRUE
    LIMIT 1;

    -- Không tìm thấy tài khoản
    IF v_child_id IS NULL THEN
        SELECT 'allowed' AS access_result, NULL AS allowed_website_id, 'Không phải tài khoản con' AS reason;
        LEAVE proc;
    END IF;

    -- Bộ lọc đang tắt
    IF v_filter_enabled = 0 THEN
        SELECT 'allowed' AS access_result, NULL AS allowed_website_id, 'Bộ lọc đang tắt' AS reason;
        LEAVE proc;
    END IF;

    -- Tìm rule khớp domain (hỗ trợ subdomain, with explicit collation)
    SELECT id, time_limit_minutes, allowed_start_time, allowed_end_time
    INTO v_website_id, v_time_limit, v_start_time, v_end_time
    FROM allowed_websites
    WHERE child_id = v_child_id
      AND (
        domain COLLATE utf8mb4_unicode_ci = p_domain COLLATE utf8mb4_unicode_ci
        OR p_domain COLLATE utf8mb4_unicode_ci LIKE CONCAT('%.', domain COLLATE utf8mb4_unicode_ci)
      )
      AND is_active = TRUE
    ORDER BY LENGTH(domain) DESC
    LIMIT 1;

    -- Không có trong whitelist → CHẶN
    IF v_website_id IS NULL THEN
        SELECT 'blocked' AS access_result, NULL AS allowed_website_id, 'Không có trong danh sách cho phép' AS reason;
        LEAVE proc;
    END IF;

    -- Kiểm tra khung giờ
    IF v_start_time IS NOT NULL AND v_end_time IS NOT NULL THEN
        IF CURTIME() < v_start_time OR CURTIME() > v_end_time THEN
            SELECT 'blocked' AS access_result, v_website_id AS allowed_website_id,
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
        SELECT 'blocked' AS access_result, v_website_id AS allowed_website_id,
               CONCAT('Hết thời gian (', v_time_limit, ' phút/ngày)') AS reason;
        LEAVE proc;
    END IF;

    -- Được phép truy cập
    SELECT 'allowed' AS access_result, v_website_id AS allowed_website_id, '' AS reason;
END$$

DELIMITER ;

-- ============================================================
-- Verify fix - test the procedure
-- ============================================================
-- To test, run:
-- CALL sp_ExtensionCheckAccess('your_google_id', 'youtube.com');
-- Should return results without collation errors

SELECT '✅ Collation fix applied successfully' AS status;
