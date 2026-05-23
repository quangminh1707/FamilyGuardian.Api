# Family Guardian — Fix blocked.html: Thông báo sau khi gửi yêu cầu (Phần 14)

> **Ngày tạo:** 2026-05-21
> **Tiếp nối:** FEATURE_IMPLEMENTATION_GUIDE_13.md (Phần 13)

---

## ⚠️ Quy tắc

- KHÔNG thay đổi logic gửi request hiện có
- KHÔNG thay đổi `background.js`
- KHÔNG thay đổi SQL hoặc Backend
- Chỉ sửa `blocked.js` — thêm UI feedback sau khi gửi

---

## SQL cần chạy

Không có.

---

## Phân tích

`blocked.html` đã có sẵn 2 elements nhưng bị ẩn (`display: none`):

```html
<div id="request-status"></div>           <!-- style="display:none" -->
<div id="polling-indicator">              <!-- style="display:none" -->
  ⟳ Đang kiểm tra trạng thái... trang sẽ tự mở khi được cho phép
</div>
```

`blocked.js` gửi message `REQUEST_ACCESS` → nhận callback `{ success: true }` hoặc `{ success: false, error }` → **không làm gì với UI** → con không thấy phản hồi.

---

## Fix — Chỉ sửa `blocked.js`

### Bước 1 — Kiểm tra trước

Mở `blocked.js`. Tìm đoạn xử lý kết quả sau khi gửi request. Thường có dạng:

```javascript
chrome.runtime.sendMessage({ type: "REQUEST_ACCESS", ... }, (response) => {
  if (response?.success) {
    // ← CHỖ NÀY HIỆN TẠI ĐANG TRỐNG HOẶC THIẾU UI FEEDBACK
  } else {
    // ← CHỖ NÀY CŨNG CÓ THỂ THIẾU
  }
});
```

Xác định chính xác callback đó rồi thêm vào.

### Bước 2 — Thêm hàm helper `showRequestFeedback`

Thêm hàm này vào `blocked.js` (thêm vào cuối file hoặc cạnh các hàm helper khác):

```javascript
// ── THÊM: Feedback sau khi gửi yêu cầu ──
function showRequestFeedback(success, errorMsg) {
  const statusEl    = document.getElementById('request-status');
  const pollingEl   = document.getElementById('polling-indicator');
  const btnRequest  = document.getElementById('btn-request-access');
  const btnText     = document.getElementById('btn-request-text');

  if (!statusEl) return;

  statusEl.style.display = 'block';

  if (success) {
    // ── Thành công ──
    statusEl.style.background  = 'rgba(34, 197, 94, 0.12)';
    statusEl.style.border      = '1px solid rgba(34, 197, 94, 0.3)';
    statusEl.style.borderRadius = '8px';
    statusEl.style.padding     = '10px 14px';
    statusEl.style.color       = '#86efac';
    statusEl.style.fontSize    = '13px';
    statusEl.style.marginTop   = '10px';
    statusEl.innerHTML = '✅ Đã gửi yêu cầu thành công! Bố/mẹ sẽ nhận được thông báo ngay.';

    // Vô hiệu hóa nút để tránh gửi trùng
    if (btnRequest) btnRequest.disabled = true;
    if (btnText)    btnText.textContent  = 'Đã gửi yêu cầu';

    // Hiện polling indicator
    if (pollingEl) pollingEl.style.display = 'block';

  } else {
    // ── Thất bại ──
    statusEl.style.background  = 'rgba(239, 68, 68, 0.12)';
    statusEl.style.border      = '1px solid rgba(239, 68, 68, 0.3)';
    statusEl.style.borderRadius = '8px';
    statusEl.style.padding     = '10px 14px';
    statusEl.style.color       = '#fca5a5';
    statusEl.style.fontSize    = '13px';
    statusEl.style.marginTop   = '10px';
    statusEl.innerHTML = `❌ Gửi thất bại: ${errorMsg || 'Vui lòng thử lại'}`;

    // Cho phép gửi lại
    if (btnRequest) btnRequest.disabled = false;
    if (btnText)    btnText.textContent  = 'Gửi lại yêu cầu';
  }
}
// ── KẾT THÚC THÊM ──
```

### Bước 3 — Gọi `showRequestFeedback` trong callback

Tìm đoạn callback sau `chrome.runtime.sendMessage`. Thêm gọi hàm:

```javascript
// TRƯỚC (thiếu feedback):
chrome.runtime.sendMessage({ type: "REQUEST_ACCESS", ... }, (response) => {
  if (response?.success) {
    // không làm gì
  }
});

// SAU (thêm feedback — KHÔNG thay đổi gì khác):
chrome.runtime.sendMessage({ type: "REQUEST_ACCESS", ... }, (response) => {
  if (response?.success) {
    showRequestFeedback(true, null);      // ← THÊM
  } else {
    const err = response?.error || 'Lỗi không xác định';
    showRequestFeedback(false, err);      // ← THÊM
  }
});
```

### Bước 4 — Thêm trạng thái loading khi đang gửi

Tìm chỗ disabled nút khi đang gửi (thường là ngay trước khi gọi `sendMessage`). Thêm text loading:

```javascript
// Tìm đoạn: btnRequest.disabled = true; (trước khi gửi)
// Thêm đổi text:
if (btnRequest) btnRequest.disabled = true;
if (btnText)    btnText.textContent  = 'Đang gửi...';   // ← THÊM
```

---

## Thứ tự làm việc

```
1 — Mở blocked.js, tìm đoạn sendMessage REQUEST_ACCESS và callback
2 — Thêm hàm showRequestFeedback vào cuối file
3 — Gọi showRequestFeedback(true) trong success callback
4 — Gọi showRequestFeedback(false, err) trong error callback
5 — Thêm text "Đang gửi..." khi disabled nút
6 — node build-config.js → reload extension → test
```

---

## Test

```
1. Con vào trang bị chặn
2. Nhấn "Gửi yêu cầu cho bố/mẹ"
3. Nút đổi thành "Đang gửi..." + disabled
4. → Sau ~1s: hiện box xanh "✅ Đã gửi yêu cầu thành công! Bố/mẹ sẽ nhận được thông báo ngay."
5. → Nút đổi thành "Đã gửi yêu cầu" + disabled (không gửi được 2 lần)
6. → Hiện "⟳ Đang kiểm tra trạng thái... trang sẽ tự mở khi được cho phép"
7. Guardian nhận notification bình thường

Test lỗi (tắt mạng rồi gửi):
→ Hiện box đỏ "❌ Gửi thất bại: ..."
→ Nút đổi thành "Gửi lại yêu cầu" (có thể gửi lại)
```
