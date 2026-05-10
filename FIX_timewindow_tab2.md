# FIX: Tab "Khung Giờ (Tùy Chỉnh)" trong WarningConfigModal

> **SQL:** ✅ Đã chạy xong từ `FEATURE_timewindow_warning_refactor.md`  
> Bảng `website_timewindow_warning_configs` đã có `warn_mode`, `warn_at_time1`, `warn_at_time2`  
> **Không thay đổi logic** — chỉ sửa giao diện Tab 2.  
> **Dark mode:** Chỉ dùng CSS variables, KHÔNG dùng `bg-white`, `text-gray-*`, `bg-purple-*`.

---

## Kiểm tra trước khi sửa

### Backend — kiểm tra response của `GET /api/children/{childId}/websites` (hoặc SP)
Đảm bảo mỗi website object trả về đủ 2 field:
```json
{
  "allowedStartTime": "07:00:00",
  "allowedEndTime": "21:00:00"
}
```
Nếu thiếu → thêm vào DTO/SP trước khi làm frontend.

### Frontend — xác định file cần sửa
- Mở `WarningConfigModal.tsx` → tìm Tab 2 (tab khung giờ)
- Tìm section đang render "KHUNG GIỜ CHO PHÉP" (input giờ bắt đầu/kết thúc) → đây là thứ cần xóa
- Tìm chỗ render subtitle "Chưa giới hạn" trong website selector của Tab 2 → đây là thứ cần sửa

---

## Fix 1 — Xóa section "Khung giờ cho phép" (bước 2 cũ)

**Frontend — `WarningConfigModal.tsx` Tab 2**

Tìm và **xóa hoàn toàn** block JSX có tiêu đề "KHUNG GIỜ CHO PHÉP" — block này chứa:
- Label "GIỜ BẮT ĐẦU" / "GIỜ KẾT THÚC"
- 2 input `type="time"`
- Preview "Con được dùng từ X đến Y (Z giờ/ngày)"

Block này thuộc `EditWebsiteModal`, không phải modal cảnh báo → xóa hẳn.

Sau khi xóa, Tab 2 chỉ còn:
```
Bước 1 — CHỌN WEBSITE
Bước 2 — CẢNH BÁO  (số thứ tự đổi từ 3 → 2)
[Nút LƯU KHUNG GIỜ]
```

---

## Fix 2 — Website selector hiện khung giờ thay vì "Chưa giới hạn"

**Frontend — `WarningConfigModal.tsx` Tab 2, phần render website card**

### Thêm helper function (đặt trong file hoặc `src/lib/formatters.ts`)

```typescript
// "07:00:00" + "21:00:00" → "07:00 → 21:00 · 14 giờ/ngày"
export function formatTimeRange(start: string, end: string): string {
  const fmt = (t: string) => t.substring(0, 5); // cắt lấy "HH:mm"
  const [sh, sm] = start.split(':').map(Number);
  const [eh, em] = end.split(':').map(Number);
  const totalMins = (eh * 60 + em) - (sh * 60 + sm);
  const hours = Math.floor(totalMins / 60);
  const mins = totalMins % 60;
  const duration = mins > 0 ? `${hours} giờ ${mins} phút` : `${hours} giờ`;
  return `${fmt(start)} → ${fmt(end)} · ${duration}/ngày`;
}
```

### Sửa subtitle trong website card selector

```tsx
// TRƯỚC — đang hiện cứng "Chưa giới hạn"
<p className="text-xs text-tx-secondary">Chưa giới hạn</p>

// SAU — hiện khung giờ thực tế từ data
<p className="text-xs text-tx-secondary">
  {website.allowedStartTime && website.allowedEndTime
    ? formatTimeRange(website.allowedStartTime, website.allowedEndTime)
    : 'Chưa có khung giờ'}
</p>
```

Kết quả mong muốn: card website hiện `07:00 → 21:00 · 14 giờ/ngày`

---

## Fix 3 — Renumber + Thêm mode cảnh báo (bước 2 mới)

### 3a. Đổi số thứ tự

Sau khi xóa section "Khung giờ cho phép":
- Bước 1: CHỌN WEBSITE (giữ nguyên)
- Bước 2: CẢNH BÁO TRƯỚC KHI HẾT GIỜ ← đổi từ số `3` thành `2`

### 3b. Thêm state cho mode (loại trừ nhau)

Trong component Tab 2 thêm:
```typescript
const [warnMode, setWarnMode] = useState<'minutes_before' | 'at_time'>('minutes_before');
const [warnAtTime1, setWarnAtTime1] = useState('');
const [warnAtTimeMsg1, setWarnAtTimeMsg1] = useState('');
const [showAtTime2, setShowAtTime2] = useState(false);
const [warnAtTime2, setWarnAtTime2] = useState('');
const [warnAtTimeMsg2, setWarnAtTimeMsg2] = useState('');
```

Khi load config hiện tại (useEffect):
```typescript
if (twConfig) {
  setWarnMode(twConfig.warnMode ?? 'minutes_before');
  if (twConfig.warnMode === 'at_time') {
    setWarnAtTime1(twConfig.warnAtTime1 ?? '');
    setWarnAtTimeMsg1(twConfig.message1 ?? '');
    if (twConfig.warnAtTime2) {
      setShowAtTime2(true);
      setWarnAtTime2(twConfig.warnAtTime2);
      setWarnAtTimeMsg2(twConfig.message2 ?? '');
    }
  }
  // ... load minutes_before fields như cũ
}
```

### 3c. UI phần cảnh báo bước 2

**Cấu trúc giống hệt Tab 1 (Giới hạn phút), chỉ thay nội dung:**

```tsx
{/* Bước 2 — Cảnh báo (tùy chọn) */}
<div className="space-y-4">
  <div className="flex items-center justify-between">
    <div className="flex items-center gap-2">
      <span className="w-6 h-6 rounded-full bg-tx-muted text-bg-surface 
                       text-xs font-bold flex items-center justify-center">2</span>
      <h3 className="font-bold text-tx-primary text-sm uppercase tracking-wide">
        Cảnh báo trước khi hết giờ
      </h3>
      <span className="text-xs text-tx-muted">(tùy chọn)</span>
    </div>
    {/* Nút bật/tắt cảnh báo — giữ nguyên pattern hiện tại */}
    <button onClick={() => setShowWarning(prev => !prev)}
      className={cn('text-xs font-bold px-3 py-1 rounded-lg transition-colors',
        showWarning
          ? 'bg-yellow-500/20 text-yellow-400 hover:bg-yellow-500/30'
          : 'bg-bg-subtle text-tx-secondary hover:text-tx-primary'
      )}>
      {showWarning ? 'Tắt cảnh báo' : 'Bật cảnh báo'}
    </button>
  </div>

  {showWarning && (
    <div className="bg-bg-elevated rounded-2xl p-4 space-y-4 border border-border-base">

      {/* Toggle chọn mode — 2 nút loại trừ nhau */}
      <div className="flex gap-1 p-1 bg-bg-subtle rounded-xl">
        <button
          onClick={() => setWarnMode('minutes_before')}
          className={cn(
            'flex-1 py-2 px-3 rounded-lg text-xs font-bold transition-all',
            warnMode === 'minutes_before'
              ? 'bg-bg-surface text-tx-primary shadow-sm'
              : 'text-tx-muted hover:text-tx-secondary'
          )}
        >
          ⏱ Trước N phút
        </button>
        <button
          onClick={() => setWarnMode('at_time')}
          className={cn(
            'flex-1 py-2 px-3 rounded-lg text-xs font-bold transition-all',
            warnMode === 'at_time'
              ? 'bg-bg-surface text-tx-primary shadow-sm'
              : 'text-tx-muted hover:text-tx-secondary'
          )}
        >
          🕐 Giờ cụ thể
        </button>
      </div>

      {/* ── Mode: Trước N phút (giữ nguyên UI cũ) ── */}
      {warnMode === 'minutes_before' && (
        <div className="space-y-3">
          {/* Mốc 1 */}
          <div className="space-y-2">
            <p className="text-xs font-semibold text-tx-secondary uppercase tracking-wider">
              🔔 Cảnh báo mốc 1
            </p>
            <div className="flex items-center gap-2">
              <span className="text-xs text-tx-secondary">Trước</span>
              <input
                type="number" min={1} max={240}
                value={minutesBefore1}
                onChange={e => setMinutesBefore1(Number(e.target.value))}
                className="w-20 px-3 py-1.5 rounded-lg border border-border-base 
                           bg-bg-surface text-tx-primary text-sm text-center
                           focus:outline-none focus:border-brand-DEFAULT"
              />
              <span className="text-xs text-tx-secondary">phút khi hết khung giờ</span>
            </div>
            <textarea
              value={message1} onChange={e => setMessage1(e.target.value)}
              placeholder="VD: Sắp hết khung giờ rồi con ơi!"
              maxLength={300}
              rows={2}
              className="w-full px-3 py-2 rounded-xl border border-border-base 
                         bg-bg-surface text-tx-primary text-sm resize-none
                         placeholder:text-tx-muted
                         focus:outline-none focus:border-brand-DEFAULT"
            />
            <p className="text-right text-[10px] text-tx-muted">{message1.length}/300</p>
          </div>
          {/* Mốc 2 — tùy chọn, giữ nguyên pattern hiện tại */}
          {/* ... */}
        </div>
      )}

      {/* ── Mode: Giờ cụ thể ── */}
      {warnMode === 'at_time' && (
        <div className="space-y-3">
          {/* Hint: giờ phải nằm trong khung giờ */}
          {selectedWebsite?.allowedStartTime && (
            <p className="text-xs text-tx-secondary bg-bg-subtle rounded-lg px-3 py-2">
              💡 Chọn giờ trong khung{' '}
              <span className="font-bold text-brand-DEFAULT">
                {formatTimeRange(selectedWebsite.allowedStartTime, selectedWebsite.allowedEndTime!)}
              </span>
            </p>
          )}

          {/* Mốc 1 */}
          <div className="space-y-2">
            <p className="text-xs font-semibold text-tx-secondary uppercase tracking-wider">
              🔔 Cảnh báo lúc (mốc 1)
            </p>
            <input
              type="time"
              value={warnAtTime1}
              onChange={e => setWarnAtTime1(e.target.value)}
              className="w-full px-3 py-2 rounded-xl border border-border-base 
                         bg-bg-surface text-tx-primary
                         focus:outline-none focus:border-brand-DEFAULT"
            />
            <textarea
              value={warnAtTimeMsg1} onChange={e => setWarnAtTimeMsg1(e.target.value)}
              placeholder="VD: Con sắp hết giờ dùng mạng hôm nay!"
              maxLength={300} rows={2}
              className="w-full px-3 py-2 rounded-xl border border-border-base 
                         bg-bg-surface text-tx-primary text-sm resize-none
                         placeholder:text-tx-muted
                         focus:outline-none focus:border-brand-DEFAULT"
            />
            <p className="text-right text-[10px] text-tx-muted">{warnAtTimeMsg1.length}/300</p>
          </div>

          {/* Mốc 2 — tùy chọn */}
          {showAtTime2 ? (
            <div className="space-y-2 pt-2 border-t border-border-subtle">
              <div className="flex items-center justify-between">
                <p className="text-xs font-semibold text-tx-secondary uppercase tracking-wider">
                  🔔 Cảnh báo lúc (mốc 2)
                </p>
                <button onClick={() => { setShowAtTime2(false); setWarnAtTime2(''); setWarnAtTimeMsg2(''); }}
                  className="text-xs text-red-400 hover:text-red-500">
                  − Xóa mốc 2
                </button>
              </div>
              <input
                type="time" value={warnAtTime2} onChange={e => setWarnAtTime2(e.target.value)}
                className="w-full px-3 py-2 rounded-xl border border-border-base 
                           bg-bg-surface text-tx-primary
                           focus:outline-none focus:border-brand-DEFAULT"
              />
              <textarea
                value={warnAtTimeMsg2} onChange={e => setWarnAtTimeMsg2(e.target.value)}
                placeholder="Nội dung cảnh báo mốc 2..."
                maxLength={300} rows={2}
                className="w-full px-3 py-2 rounded-xl border border-border-base 
                           bg-bg-surface text-tx-primary text-sm resize-none
                           placeholder:text-tx-muted
                           focus:outline-none focus:border-brand-DEFAULT"
              />
            </div>
          ) : (
            <button onClick={() => setShowAtTime2(true)}
              className="text-xs font-semibold text-brand-DEFAULT hover:text-brand-hover 
                         flex items-center gap-1">
              + Thêm mốc cảnh báo thứ 2
            </button>
          )}
        </div>
      )}
    </div>
  )}
</div>
```

### 3d. Payload khi submit

```typescript
const payload = {
  allowedWebsiteId: selectedWebsiteId,
  warnMode,
  isActive: true,
  ...(warnMode === 'minutes_before'
    ? {
        warnMinutesBefore1: minutesBefore1,
        message1,
        warnMinutesBefore2: showMoc2 ? minutesBefore2 : null,
        message2: showMoc2 ? message2 : null,
        warnAtTime1: null,
        warnAtTime2: null,
      }
    : {
        warnMinutesBefore1: null,
        warnMinutesBefore2: null,
        message1: warnAtTimeMsg1,
        message2: showAtTime2 ? warnAtTimeMsg2 : null,
        warnAtTime1: warnAtTime1 || null,
        warnAtTime2: showAtTime2 ? (warnAtTime2 || null) : null,
      }
  ),
};
```

---

## Fix 4 — Backend: validate `warnAtTime` nằm trong khung giờ

**Backend — `TimeWindowWarningConfigController.cs`**

Thêm validate khi POST/upsert với mode `at_time`:

```csharp
if (request.WarnMode == "at_time")
{
    // Lấy website để kiểm tra khung giờ
    var website = await _db.AllowedWebsites
        .FirstOrDefaultAsync(w => w.Id == request.AllowedWebsiteId);

    if (website?.AllowedStartTime == null || website.AllowedEndTime == null)
        return BadRequest("Website chưa có khung giờ. Hãy thiết lập khung giờ trước.");

    if (!string.IsNullOrEmpty(request.WarnAtTime1))
    {
        var t1 = TimeOnly.Parse(request.WarnAtTime1);
        var start = TimeOnly.FromTimeSpan(website.AllowedStartTime.Value);
        var end = TimeOnly.FromTimeSpan(website.AllowedEndTime.Value);
        if (t1 < start || t1 > end)
            return BadRequest($"Giờ cảnh báo {request.WarnAtTime1} phải nằm trong khung {start:HH:mm} - {end:HH:mm}");
    }
    // Tương tự cho WarnAtTime2
}
```

---

## Checklist

### Frontend — `WarningConfigModal.tsx` Tab 2
- [ ] Xóa section "KHUNG GIỜ CHO PHÉP" (input giờ bắt đầu/kết thúc + preview)
- [ ] Subtitle website card: `"Chưa giới hạn"` → `formatTimeRange(start, end)` (vd `07:00 → 21:00 · 14 giờ/ngày`)
- [ ] Số thứ tự bước: Cảnh báo đổi từ `3` → `2`
- [ ] Thêm toggle mode "⏱ Trước N phút" / "🕐 Giờ cụ thể" — loại trừ nhau
- [ ] Mode `minutes_before`: giữ nguyên UI cũ
- [ ] Mode `at_time`: 2 input `type="time"` + textarea + hint khung giờ + mốc 2 tùy chọn
- [ ] Load config đúng mode khi mở modal (nếu đã có config `at_time` thì hiện đúng section)
- [ ] Payload submit đúng theo mode
- [ ] Tất cả class màu dùng CSS variable (không hardcode)

### Frontend — `src/lib/formatters.ts`
- [ ] Thêm hàm `formatTimeRange(start, end)` export

### Backend — `TimeWindowWarningConfigController.cs`
- [ ] Validate `warnAtTime` nằm trong `allowedStartTime`–`allowedEndTime`
- [ ] Logic upsert đã xử lý đúng `warnMode` (từ file cũ, kiểm tra lại)

### Backend — `ExtensionService.cs`
- [ ] Nhánh `at_time` đã có từ file cũ — kiểm tra lại logic `now >= warnAtTime1` không bị lỗi timezone
- [ ] Nhánh `minutes_before` không bị đụng vào
