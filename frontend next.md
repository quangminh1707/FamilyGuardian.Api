# FRONTEND + CHROME EXTENSION — Việc cần làm tiếp theo

---

## PHẦN 1: Dashboard (React Vite — thêm vào hệ thống hiện tại)

### Thêm Toggle "Bộ lọc web" vào trang quản lý con

**Vị trí:** Trang chi tiết của từng con (ChildDetailPage hoặc ChildManagePage)

**Component cần thêm:** `FilterToggle.tsx`

```
[Trang quản lý con - Con: Nguyễn Văn Con]
─────────────────────────────────────────
  🛡️ Bộ lọc web
  Khi bật, con chỉ truy cập được các web trong danh sách cho phép

  [Toggle Switch: ●───] BẬT          ← component mới

  ✅ Danh sách web cho phép (tab đã có)
  📊 Lịch sử truy cập (tab đã có)
  ⏱️ Thống kê thời gian (tab đã có)
```

**API call khi toggle:**
```typescript
// PATCH /api/children/{childId}/filter
const toggleFilter = async (childId: number, enabled: boolean) => {
  await api.patch(`/children/${childId}/filter`, { filter_enabled: enabled });
};
```

**State cần lưu:**
```typescript
interface ChildDetail {
  // ... các field đã có
  filter_enabled: boolean;  // THÊM MỚI
}
```

---

### Thêm badge trạng thái trên card danh sách con

Trên màn hình danh sách con của Guardian, mỗi card con hiển thị thêm:

```
┌─────────────────────────────┐
│ 👦 Nguyễn Văn Con           │
│ 📧 con@gmail.com            │
│ 🌐 5 web được phép          │
│ 🛡️ Bộ lọc: [BẬT ✅]        │  ← THÊM MỚI
│ ⏱️ Hôm nay: 1h 23m          │
└─────────────────────────────┘
```

---

### Trang hướng dẫn cài Extension (mới hoàn toàn)

**Route:** `/guide/extension`

**Nội dung:**
```
Bước 1: Tải Extension
  [Nút: Tải về extension.crx]  hoặc  [Link Chrome Web Store nếu đã publish]

Bước 2: Cài vào Chrome
  - Mở chrome://extensions
  - Bật "Developer mode"
  - Kéo thả file .crx vào cửa sổ
  [Ảnh minh họa từng bước]

Bước 3: Đăng nhập
  - Click icon extension trên thanh công cụ Chrome
  - Đăng nhập bằng tài khoản Google của CON (không phải bố mẹ)
  - Extension tự động nhận cấu hình

Bước 4: Bật bộ lọc
  - Vào dashboard bố mẹ → chọn con → bật toggle "Bộ lọc web"
```

**Link tới trang này:** Hiển thị trong trang quản lý con, dưới toggle bộ lọc.

---

## PHẦN 2: Chrome Extension (tự build — tích hợp hệ thống)

### Cấu trúc thư mục

```
family-guardian-extension/
├── manifest.json
├── background.js          ← Service worker: intercept request + gọi API
├── popup.html             ← UI khi click icon extension
├── popup.js
├── blocked.html           ← Trang hiện khi bị chặn
├── blocked.js
├── icons/
│   ├── icon16.png
│   ├── icon48.png
│   └── icon128.png
└── config.js              ← Chứa API_BASE_URL
```

---

### `manifest.json`

```json
{
  "manifest_version": 3,
  "name": "Family Guardian — Bộ lọc web",
  "version": "1.0.0",
  "description": "Kiểm soát truy cập web theo tài khoản Google",
  "icons": {
    "16": "icons/icon16.png",
    "48": "icons/icon48.png",
    "128": "icons/icon128.png"
  },
  "permissions": [
    "identity",
    "webRequest",
    "storage",
    "alarms"
  ],
  "host_permissions": ["<all_urls>"],
  "background": {
    "service_worker": "background.js"
  },
  "action": {
    "default_popup": "popup.html",
    "default_icon": "icons/icon48.png"
  },
  "oauth2": {
    "client_id": "YOUR_GOOGLE_CLIENT_ID.apps.googleusercontent.com",
    "scopes": ["openid", "email", "profile"]
  }
}
```

---

### `config.js`

```javascript
const CONFIG = {
  API_BASE: "https://yourserver.com/api/extension",
  CACHE_TTL_MS: 5 * 60 * 1000,    // Cache 5 phút
  HEARTBEAT_INTERVAL_MS: 30000,    // Gửi heartbeat mỗi 30 giây
};
```

---

### `background.js`

```javascript
importScripts("config.js");

// ─── Cache ───────────────────────────────────────────────
const domainCache = new Map(); // domain → { allowed, reason, websiteId, time }

function isCacheValid(entry) {
  return entry && (Date.now() - entry.time) < CONFIG.CACHE_TTL_MS;
}

// ─── Google Token ─────────────────────────────────────────
async function getGoogleToken() {
  return new Promise((resolve) => {
    chrome.identity.getAuthToken({ interactive: false }, (token) => {
      resolve(chrome.runtime.lastError ? null : token);
    });
  });
}

// ─── Check domain với backend ─────────────────────────────
async function checkDomain(domain) {
  // Kiểm tra cache
  const cached = domainCache.get(domain);
  if (isCacheValid(cached)) return cached;

  const token = await getGoogleToken();
  if (!token) return { allowed: true, reason: "Chưa đăng nhập" };

  try {
    const res = await fetch(`${CONFIG.API_BASE}/check?domain=${domain}`, {
      headers: { Authorization: `Bearer ${token}` }
    });

    if (!res.ok) return { allowed: true, reason: "Lỗi server" };

    const data = await res.json();
    const entry = {
      allowed: data.allowed,
      reason: data.reason || "",
      websiteId: data.allowed_website_id || null,
      time: Date.now()
    };

    domainCache.set(domain, entry);
    return entry;
  } catch {
    return { allowed: true, reason: "Lỗi mạng" }; // Lỗi → không chặn
  }
}

// ─── Intercept request ────────────────────────────────────
chrome.webRequest.onBeforeRequest.addListener(
  async (details) => {
    // Chỉ xử lý tab chính (không chặn iframe, fetch, XHR)
    if (details.type !== "main_frame") return {};

    let domain;
    try {
      domain = new URL(details.url).hostname.replace(/^www\./, "");
    } catch {
      return {};
    }

    // Không chặn localhost và IP nội bộ
    if (domain === "localhost" || /^\d+\.\d+\.\d+\.\d+$/.test(domain)) {
      return {};
    }

    const result = await checkDomain(domain);

    if (!result.allowed) {
      const blockedUrl = chrome.runtime.getURL("blocked.html")
        + `?domain=${encodeURIComponent(domain)}`
        + `&reason=${encodeURIComponent(result.reason)}`;
      return { redirectUrl: blockedUrl };
    }

    return {};
  },
  { urls: ["http://*/*", "https://*/*"] },
  ["blocking"]
);

// ─── Heartbeat (tracking thời gian) ──────────────────────
let activeTab = null; // { domain, websiteId, startTime }

chrome.tabs.onActivated.addListener(async ({ tabId }) => {
  const tab = await chrome.tabs.get(tabId);
  if (!tab.url) return;

  try {
    const domain = new URL(tab.url).hostname.replace(/^www\./, "");
    const cached = domainCache.get(domain);
    if (cached?.allowed && cached?.websiteId) {
      activeTab = { domain, websiteId: cached.websiteId };
    } else {
      activeTab = null;
    }
  } catch {
    activeTab = null;
  }
});

// Gửi heartbeat mỗi 30 giây
chrome.alarms.create("heartbeat", { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(async (alarm) => {
  if (alarm.name !== "heartbeat" || !activeTab) return;

  const token = await getGoogleToken();
  if (!token) return;

  fetch(`${CONFIG.API_BASE}/heartbeat`, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${token}`,
      "Content-Type": "application/json"
    },
    body: JSON.stringify({
      domain: activeTab.domain,
      allowed_website_id: activeTab.websiteId
    })
  }).catch(() => {}); // Bỏ qua lỗi heartbeat
});

// ─── Xóa cache khi extension nhận message từ server ──────
chrome.runtime.onMessage.addListener((msg) => {
  if (msg.type === "CLEAR_CACHE") {
    domainCache.clear();
  }
});
```

---

### `popup.html`

```html
<!DOCTYPE html>
<html lang="vi">
<head>
  <meta charset="UTF-8">
  <style>
    body { width: 280px; padding: 16px; font-family: Arial, sans-serif; }
    .header { display: flex; align-items: center; gap: 8px; margin-bottom: 16px; }
    .header img { width: 32px; }
    .header h2 { margin: 0; font-size: 14px; }
    .status-card { background: #f0f9ff; border-radius: 8px; padding: 12px; margin-bottom: 12px; }
    .status-row { display: flex; justify-content: space-between; font-size: 13px; margin: 4px 0; }
    .badge { padding: 2px 8px; border-radius: 12px; font-size: 11px; font-weight: bold; }
    .badge.on  { background: #dcfce7; color: #166534; }
    .badge.off { background: #fee2e2; color: #991b1b; }
    .badge.na  { background: #f3f4f6; color: #6b7280; }
    .btn { width: 100%; padding: 8px; border-radius: 6px; border: none;
           cursor: pointer; font-size: 13px; margin-top: 4px; }
    .btn-logout { background: #f3f4f6; color: #374151; }
    .btn-login  { background: #2563eb; color: white; }
    #loading { text-align: center; color: #6b7280; font-size: 13px; }
  </style>
</head>
<body>
  <div class="header">
    <img src="icons/icon48.png" alt="">
    <h2>Family Guardian</h2>
  </div>

  <div id="loading">Đang tải...</div>
  <div id="content" style="display:none">
    <div class="status-card">
      <div class="status-row">
        <span>Tài khoản</span>
        <span id="user-email" style="font-size:11px; color:#374151"></span>
      </div>
      <div class="status-row">
        <span>Bộ lọc web</span>
        <span id="filter-badge" class="badge">—</span>
      </div>
    </div>
    <button class="btn btn-logout" id="btn-logout">Đăng xuất</button>
  </div>
  <div id="login-section" style="display:none">
    <p style="font-size:13px; color:#6b7280; text-align:center">
      Đăng nhập bằng tài khoản Google của con để kích hoạt bộ lọc.
    </p>
    <button class="btn btn-login" id="btn-login">Đăng nhập Google</button>
  </div>

  <script src="popup.js"></script>
</body>
</html>
```

---

### `popup.js`

```javascript
importScripts("config.js"); // không cần nếu popup.js là module riêng

const API_BASE = "https://yourserver.com/api/extension"; // đồng bộ với config.js

async function getToken(interactive = false) {
  return new Promise((resolve) => {
    chrome.identity.getAuthToken({ interactive }, (token) => {
      resolve(chrome.runtime.lastError ? null : token);
    });
  });
}

async function init() {
  const token = await getToken(false);

  if (!token) {
    document.getElementById("loading").style.display = "none";
    document.getElementById("login-section").style.display = "block";
    return;
  }

  try {
    const res = await fetch(`${API_BASE}/config`, {
      headers: { Authorization: `Bearer ${token}` }
    });
    const data = await res.json();

    document.getElementById("loading").style.display = "none";
    document.getElementById("content").style.display = "block";
    document.getElementById("user-email").textContent = data.full_name || "—";

    const badge = document.getElementById("filter-badge");
    if (data.filter_enabled) {
      badge.textContent = "BẬT ✅";
      badge.className = "badge on";
    } else {
      badge.textContent = "TẮT";
      badge.className = "badge off";
    }
  } catch {
    document.getElementById("loading").textContent = "Lỗi kết nối server.";
  }
}

document.getElementById("btn-login")?.addEventListener("click", async () => {
  const token = await getToken(true);
  if (token) init();
});

document.getElementById("btn-logout")?.addEventListener("click", () => {
  chrome.identity.clearAllCachedAuthTokens(() => {
    location.reload();
  });
});

init();
```

---

### `blocked.html`

```html
<!DOCTYPE html>
<html lang="vi">
<head>
  <meta charset="UTF-8">
  <title>Trang bị chặn</title>
  <style>
    * { margin: 0; padding: 0; box-sizing: border-box; }
    body {
      font-family: Arial, sans-serif;
      background: #f8fafc;
      display: flex;
      align-items: center;
      justify-content: center;
      min-height: 100vh;
    }
    .card {
      background: white;
      border-radius: 16px;
      padding: 48px 40px;
      max-width: 480px;
      width: 90%;
      text-align: center;
      box-shadow: 0 4px 24px rgba(0,0,0,0.08);
    }
    .icon { font-size: 64px; margin-bottom: 24px; }
    h1 { font-size: 22px; color: #111827; margin-bottom: 8px; }
    .domain {
      background: #fef2f2;
      border: 1px solid #fecaca;
      border-radius: 8px;
      padding: 8px 16px;
      font-family: monospace;
      font-size: 15px;
      color: #dc2626;
      display: inline-block;
      margin: 16px 0;
    }
    .reason { font-size: 14px; color: #6b7280; margin-bottom: 24px; }
    .btn-back {
      background: #2563eb;
      color: white;
      border: none;
      padding: 10px 24px;
      border-radius: 8px;
      font-size: 14px;
      cursor: pointer;
    }
    .footer { margin-top: 24px; font-size: 12px; color: #9ca3af; }
  </style>
</head>
<body>
  <div class="card">
    <div class="icon">🚫</div>
    <h1>Trang web bị chặn</h1>
    <div class="domain" id="domain-display"></div>
    <p class="reason" id="reason-display"></p>
    <button class="btn-back" onclick="history.back()">← Quay lại</button>
    <p class="footer">Liên hệ bố/mẹ để được cấp quyền truy cập</p>
  </div>
  <script src="blocked.js"></script>
</body>
</html>
```

---

### `blocked.js`

```javascript
const params = new URLSearchParams(location.search);
const domain = params.get("domain") || "trang web này";
const reason = params.get("reason") || "Không có trong danh sách được phép";

document.getElementById("domain-display").textContent = domain;
document.getElementById("reason-display").textContent = reason;
document.title = `Bị chặn — ${domain}`;
```

---

## PHẦN 3: Thứ tự làm

### Bước 1 — Backend trước
Xem file `BACKEND_NEXT.md`

### Bước 2 — Frontend (Dashboard)
1. Thêm `filter_enabled` vào interface `ChildDetail`
2. Tạo component `FilterToggle.tsx`
3. Gọi `PATCH /api/children/{childId}/filter` khi toggle
4. Hiển thị badge trạng thái trên card danh sách con
5. Tạo trang `/guide/extension` hướng dẫn cài đặt

### Bước 3 — Extension
1. Tạo thư mục `family-guardian-extension/`
2. Copy các file theo cấu trúc trên
3. Điền `YOUR_GOOGLE_CLIENT_ID` trong `manifest.json`
4. Điền URL server thật trong `config.js` và `popup.js`
5. Mở `chrome://extensions` → bật Developer mode → Load unpacked → chọn thư mục
6. Test: đăng nhập tài khoản con → vào trang bị chặn → kiểm tra

### Bước 4 — Test end-to-end
1. Bố mẹ đăng nhập dashboard → thêm domain → bật toggle bộ lọc
2. Con mở Chrome đã cài extension → thử vào domain không được phép → hiện trang blocked
3. Thử vào domain được phép → vào bình thường
4. Dashboard bố mẹ → kiểm tra lịch sử có ghi log không

---

## Lưu ý quan trọng

| Vấn đề | Giải pháp |
|--------|-----------|
| Google Client ID phải thêm extension ID vào OAuth origins | Sau khi load extension, lấy ID từ `chrome://extensions` và thêm vào Google Cloud Console |
| Extension chỉ hoạt động trên Chrome | Firefox/Edge không áp dụng được |
| Con dùng Incognito bypass được | Có thể tắt Incognito bằng Group Policy (Windows) |
| Cache 5 phút có thể trễ khi bố mẹ thay đổi rule | Chấp nhận được, hoặc giảm TTL xuống 1 phút |