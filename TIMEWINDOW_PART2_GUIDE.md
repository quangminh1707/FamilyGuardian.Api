# ⏰ Tiếp theo TIMEWINDOW_FEATURE_GUIDE.md — Phần 2

> **SQL đã tạo xong** ở file trước. Không cần SQL mới.  
> **Không thay đổi logic backend/extension đang chạy.**  
> **Dark mode:** Tất cả class màu dùng CSS variable (`bg-bg-surface`, `text-tx-primary`, `border-border-base`...).

---

## Kiểm tra trước khi làm

### Backend cần kiểm tra
- `PUT /api/children/{childId}/websites/{websiteId}` — đảm bảo khi set `timeLimitMinutes` thì tự null `allowedStartTime`/`allowedEndTime` và ngược lại
- `GET /api/children/{childId}` (hoặc SP `sp_GetChildAllowedWebsites`) — trả về đủ `allowedStartTime`, `allowedEndTime`, `timeLimitMinutes`, `todaySeconds`, `requestCount`

### Frontend cần kiểm tra
- `ChildDetailPage.tsx` — xác định tên state/props của modal thêm website hiện tại
- `AddWebsiteModal.tsx` (hoặc tên tương đương) — xác định cấu trúc form hiện có
- `WebsiteCard.tsx` — xác định props nhận vào, cách hiển thị progress bar hiện tại
- `WarningConfigModal.tsx` — xác định cách tab/section đang render

---

## 1. Modal Thêm Website — Tách 2 phần riêng biệt

**Frontend — `AddWebsiteModal.tsx` (hoặc tên file đang dùng)**

### Vấn đề hiện tại (hình 1)
Toggle "Giới hạn sử dụng mỗi ngày" và "Khung giờ cho phép" hiển thị cùng nhau, chưa có logic loại trừ nhau.

### Yêu cầu
Tách 2 phần thành **2 section riêng biệt**, có logic loại trừ nhau:

**UI mới:**
```
ĐỊA CHỈ WEBSITE
[input domain]  [Kiểm tra]

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
 CHỌN LOẠI GIỚI HẠN (chỉ được chọn 1)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

┌─────────────────────────────────────┐
│  ⏱  GIỚI HẠN SỬ DỤNG MỖI NGÀY      │  [Toggle]
│  Con sẽ bị chặn khi hết thời gian   │
│                                     │
│  (khi toggle ON, hiện input):       │
│  Số phút: [____] phút/ngày          │
└─────────────────────────────────────┘

┌─────────────────────────────────────┐
│  🕐  KHUNG GIỜ CHO PHÉP             │  [Toggle]
│  Giới hạn giờ dùng trong ngày      │
│                                     │
│  (khi toggle ON, hiện input):       │
│  Giờ bắt đầu: [__:__]              │
│  Giờ kết thúc: [__:__]             │
└─────────────────────────────────────┘
```

**Logic loại trừ:**
```typescript
// State
const [limitType, setLimitType] = useState<'none' | 'minuteLimit' | 'timeWindow'>('none');

// Khi bật toggle Giới hạn phút
const handleMinuteLimitToggle = (checked: boolean) => {
  if (checked) setLimitType('minuteLimit'); // tắt timeWindow
  else setLimitType('none');
};

// Khi bật toggle Khung giờ
const handleTimeWindowToggle = (checked: boolean) => {
  if (checked) setLimitType('timeWindow'); // tắt minuteLimit
  else setLimitType('none');
};
```

**Khi toggle đang OFF thì section đó bị mờ (opacity-50) và không thể tương tác.**

**Payload gửi lên khi XÁC NHẬN & THÊM:**
```typescript
{
  domain: string,
  timeLimitMinutes: limitType === 'minuteLimit' ? minuteValue : null,
  allowedStartTime: limitType === 'timeWindow' ? startTime : null,
  allowedEndTime:   limitType === 'timeWindow' ? endTime   : null,
}
```

**Validate trước khi submit:**
- Nếu `limitType === 'minuteLimit'` và chưa nhập phút → báo lỗi
- Nếu `limitType === 'timeWindow'` và chưa nhập giờ hoặc end <= start → báo lỗi
- Nếu `limitType === 'none'` → cho phép submit (website không giới hạn)

---

## 2. Modal Thiết lập Khung giờ — Lọc website

**Frontend — `TimeWindowModal.tsx`**

### Vấn đề hiện tại (hình 2)
Modal đang hiện cả website dùng "Giới hạn phút" lẫn "Khung giờ". Cần lọc bỏ website dùng giới hạn phút.

### Yêu cầu
Chỉ hiển thị website **chưa có giới hạn phút** (`timeLimitMinutes == null`):

```typescript
// Trong TimeWindowModal, filter danh sách website:
const eligibleWebsites = websites.filter(w => w.timeLimitMinutes == null);

// Nếu eligibleWebsites.length === 0:
// Hiện empty state: "Tất cả website đang dùng giới hạn phút. 
//  Hãy chỉnh sửa website để chuyển sang khung giờ trước."
```

**Lý do:** Một website chỉ được dùng 1 trong 2 tính năng. Website đang dùng giới hạn phút không thể đồng thời có khung giờ → ẩn khỏi danh sách chọn của modal này.

---

## 3. Gộp TimeWindowModal vào WarningConfigModal — Dùng Tabs

**Frontend — `WarningConfigModal.tsx`**

### Yêu cầu (hình 3 + 4)
Thay vì 2 nút riêng "CẤU HÌNH CẢNH BÁO" và "KHUNG GIỜ", gộp chung vào 1 modal với 2 tab:

```
┌──────────────────────────────────────┐
│  🔔 CẤU HÌNH                         │ [X]
│  Cho tài khoản: Minh Dương           │
│                                      │
│  [Tab: Cảnh báo] [Tab: Khung giờ]   │
│  ──────────────────────────────────  │
│  (nội dung tab hiện tại)             │
└──────────────────────────────────────┘
```

**Cấu trúc tab:**

**Tab 1 — Cảnh báo** (nội dung giữ nguyên như hiện tại `WarningConfigModal`):
- Chọn website (chỉ website có `timeLimitMinutes != null`)
- Mốc cảnh báo 1, 2
- Cấu hình đang áp dụng

**Tab 2 — Khung giờ** (nội dung của `TimeWindowModal`):
- Chọn website (chỉ website có `timeLimitMinutes == null`)
- Thiết lập giờ bắt đầu / kết thúc
- Cảnh báo trước khi hết khung giờ
- Khung giờ đang áp dụng

**Cập nhật ChildDetailPage:**
- Xóa nút "KHUNG GIỜ" riêng
- Giữ lại 1 nút "CẤU HÌNH CẢNH BÁO" mở modal gộp
- Khi mở từ nút Khung Giờ (nếu còn) → tự động active tab Khung giờ

**State quản lý tab:**
```typescript
const [activeTab, setActiveTab] = useState<'warning' | 'timewindow'>('warning');
```

---

## 4. Nút màu tím trong WarningConfigModal — Hiển thị tên

**Frontend — `WarningConfigModal.tsx`**

### Vấn đề hiện tại
Nút lưu/xác nhận ở cuối modal đang thiếu text label — chỉ hiện khi hover.

### Yêu cầu
Đảm bảo nút luôn hiện text rõ ràng ở cả light và dark mode:

```tsx
<button
  onClick={handleSave}
  disabled={!canSave || isSaving}
  className={cn(
    // Kích thước & hình dạng
    'w-full h-12 rounded-2xl font-bold text-sm',
    'flex items-center justify-center gap-2',
    'transition-all duration-200',
    // Light mode
    'bg-violet-600 text-white hover:bg-violet-700',
    'disabled:bg-violet-300 disabled:cursor-not-allowed',
    // Dark mode
    'dark:bg-violet-500 dark:hover:bg-violet-400',
    'dark:disabled:bg-violet-800 dark:disabled:text-violet-500',
  )}
>
  <Save className="w-4 h-4" />
  {isSaving ? 'Đang lưu...' : 'Lưu cấu hình'}
</button>
```

**Kiểm tra cả icon lẫn text đều visible** — không dùng `sr-only` hay `opacity-0` cho text.

---

## 5. WebsiteCard — Progress bar cho Khung giờ

**Frontend — `WebsiteCard.tsx`**

### Yêu cầu (hình 5)
Website dùng khung giờ hiện nay chỉ hiện "KHUNG GIỜ — 0 GIÂY" và dải màu tím tĩnh.  
Cần thêm:
- **Progress bar** thể hiện % thời gian đã dùng trong khung giờ
- **Hiển thị số phút/giờ** đã dùng và còn lại

### Tính toán % cho khung giờ

```typescript
// Props đã có từ sp_GetChildAllowedWebsites:
// allowedStartTime: "07:00:00"
// allowedEndTime:   "21:00:00"
// todaySeconds:     number (tổng giây đã dùng hôm nay)

function calcTimeWindowProgress(
  startTime: string,   // "07:00:00"
  endTime: string,     // "21:00:00"
  todaySeconds: number
): { usedPercent: number; windowTotalMinutes: number; usedMinutes: number; remainingMinutes: number } {
  const [sh, sm] = startTime.split(':').map(Number);
  const [eh, em] = endTime.split(':').map(Number);
  const windowTotalMinutes = (eh * 60 + em) - (sh * 60 + sm);
  const windowTotalSeconds = windowTotalMinutes * 60;
  const usedSeconds = Math.min(todaySeconds, windowTotalSeconds);
  const usedPercent = Math.round((usedSeconds / windowTotalSeconds) * 100);
  const usedMinutes = Math.floor(usedSeconds / 60);
  const remainingMinutes = Math.max(0, windowTotalMinutes - usedMinutes);
  return { usedPercent, windowTotalMinutes, usedMinutes, remainingMinutes };
}
```

### UI khi dùng khung giờ (thay thế/bổ sung section hiện tại):

```tsx
{website.allowedStartTime && website.allowedEndTime && (
  <div className="space-y-2">
    {/* Dòng info giờ + thời gian dùng */}
    <div className="flex items-center justify-between text-xs">
      <span className="flex items-center gap-1.5 text-tx-secondary">
        <Clock className="w-3.5 h-3.5 text-violet-500" />
        {formatTime(website.allowedStartTime)} → {formatTime(website.allowedEndTime)}
      </span>
      <span className="text-tx-muted">
        {progress.usedMinutes} / {progress.windowTotalMinutes} phút
      </span>
    </div>

    {/* Progress bar */}
    <div className="relative h-2 bg-bg-muted rounded-full overflow-hidden">
      <div
        className={cn(
          'h-full rounded-full transition-all duration-500',
          progress.usedPercent >= 100
            ? 'bg-red-500'
            : progress.usedPercent >= 80
              ? 'bg-orange-400'
              : 'bg-violet-500'
        )}
        style={{ width: `${Math.min(progress.usedPercent, 100)}%` }}
      />
    </div>

    {/* % và trạng thái */}
    <div className="flex items-center justify-between text-[11px]">
      <span className={cn(
        'font-bold',
        progress.usedPercent >= 100 ? 'text-red-500' : 'text-violet-600 dark:text-violet-400'
      )}>
        {progress.usedPercent}% ĐÃ DÙNG
      </span>
      {progress.usedPercent < 100 && (
        <span className="text-tx-muted">Còn {progress.remainingMinutes} phút</span>
      )}
      {progress.usedPercent >= 100 && (
        <span className="text-red-500 font-bold">⛔ Đã hết khung giờ</span>
      )}
    </div>
  </div>
)}
```

**Lưu ý:**
- `todaySeconds` lấy từ `daily_usage_stats` — đã có trong response của `sp_GetChildAllowedWebsites`
- Nếu hiện tại ngoài khung giờ (giờ hiện tại < start hoặc > end) → hiện badge "Ngoài khung giờ" thay progress
- Refetch interval giữ nguyên 30s như hiện tại

---

## Checklist hoàn thành

### Frontend — AddWebsiteModal
- [ ] Tách 2 section "Giới hạn phút" và "Khung giờ" thành 2 card riêng biệt
- [ ] Logic loại trừ: bật cái này tắt cái kia (state `limitType`)
- [ ] Section OFF bị mờ opacity-50, không tương tác được
- [ ] Validate đúng theo `limitType` trước khi submit
- [ ] Payload gửi đúng: `timeLimitMinutes` hoặc `allowedStartTime/EndTime`, còn lại null
- [ ] Dark mode: dùng CSS variable

### Frontend — TimeWindowModal (filter website)
- [ ] Lọc chỉ hiện website có `timeLimitMinutes == null`
- [ ] Hiện empty state nếu không có website nào đủ điều kiện
- [ ] Dark mode: dùng CSS variable

### Frontend — WarningConfigModal (gộp tab)
- [ ] Thêm tab bar: "Cảnh báo" và "Khung giờ"
- [ ] Tab Cảnh báo: giữ nguyên nội dung hiện tại
- [ ] Tab Cảnh báo: chỉ hiện website có `timeLimitMinutes != null`
- [ ] Tab Khung giờ: nội dung của TimeWindowModal (chỉ website có `timeLimitMinutes == null`)
- [ ] Xóa nút "KHUNG GIỜ" riêng trong ChildDetailPage, chỉ giữ 1 nút mở modal gộp
- [ ] Nút lưu cuối modal: hiển thị text rõ, không bị ẩn ở cả light/dark mode

### Frontend — WebsiteCard
- [ ] Hàm `calcTimeWindowProgress` tính % từ `allowedStartTime`, `allowedEndTime`, `todaySeconds`
- [ ] Progress bar màu violet → orange (≥80%) → đỏ (100%)
- [ ] Hiển thị "X / Y phút" và "Còn Z phút"
- [ ] Hiện badge "Ngoài khung giờ" khi giờ hiện tại ngoài khung
- [ ] Dark mode: dùng CSS variable

### Backend — Không cần sửa thêm
Nếu `sp_GetChildAllowedWebsites` đã trả `allowed_start_time`, `allowed_end_time`, `today_seconds` thì không cần sửa gì.  
Kiểm tra bằng cách log response API trong frontend trước khi code UI.
