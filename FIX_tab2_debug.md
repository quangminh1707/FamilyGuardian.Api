# DEBUG & FIX: Tab "Khung Giờ (Tùy Chỉnh)" — 2 lỗi frontend

> **DB OK** — google.com id=39 có `allowed_start_time='08:00:00'`, `allowed_end_time='20:00:00'`  
> **API OK** — trả về `allowedStartTime: "08:00"`, `allowedEndTime: "20:00"`  
> **Lỗi 100% ở frontend `WarningConfigModal.tsx` Tab 2**

---

## Lỗi 1 — Subtitle website hiện "Không có khung giờ" dù có data

### Debug: Tìm đúng chỗ bị lỗi

Mở `WarningConfigModal.tsx`, tìm Tab 2, tìm chỗ render subtitle của website card.

**Kiểm tra 3 nguyên nhân phổ biến:**

**A. Sai tên field** — API trả `allowedStartTime` (camelCase) nhưng code đang check field khác:
```typescript
// SAI — có thể đang check sai tên
website.allowed_start_time   // snake_case → undefined
website.AllowedStartTime     // PascalCase → undefined
website.startTime            // tên khác → undefined

// ĐÚNG — phải là camelCase khớp API response
website.allowedStartTime
```

**B. Data format "08:00" bị coi là falsy** — Kiểm tra bằng log:
```typescript
// Thêm tạm vào để debug
console.log('DEBUG website:', website.domain, website.allowedStartTime, website.allowedEndTime);
```
Nếu log ra `undefined` → lỗi A (sai tên field)  
Nếu log ra `"08:00"` → lỗi C

**C. Websites prop không được truyền đúng** — Kiểm tra prop truyền vào modal:
```typescript
// Trong ChildDetailPage.tsx — chỗ render WarningConfigModal
// Đảm bảo websites có đủ allowedStartTime/allowedEndTime
<WarningConfigModal
  websites={child.allowedWebsites} // ← kiểm tra object này có field đó không
/>
```

### Fix sau khi debug

```typescript
// Helper function — đặt trong formatters.ts hoặc đầu file component
function formatTimeRange(start: string | null | undefined, end: string | null | undefined): string | null {
  if (!start || !end) return null;
  const fmt = (t: string) => t.substring(0, 5); // "08:00:00" hoặc "08:00" → "08:00"
  const [sh, sm] = start.split(':').map(Number);
  const [eh, em] = end.split(':').map(Number);
  const totalMins = (eh * 60 + em) - (sh * 60 + sm);
  if (totalMins <= 0) return `${fmt(start)} → ${fmt(end)}`;
  const hours = Math.floor(totalMins / 60);
  const mins = totalMins % 60;
  const duration = mins > 0 ? `${hours} giờ ${mins} phút` : `${hours} giờ`;
  return `${fmt(start)} → ${fmt(end)} · ${duration}/ngày`;
}

// Trong website card selector của Tab 2:
{(() => {
  const range = formatTimeRange(website.allowedStartTime, website.allowedEndTime);
  return (
    <p className="text-xs text-tx-secondary">
      {range ?? 'Chưa có khung giờ'}
    </p>
  );
})()}
```

---

## Lỗi 2 — Giao diện Tab 2 không giống Tab 1

### Vấn đề cụ thể (so hình 2 vs hình 3)

| | Tab 1 (Giới hạn phút) — ĐÚNG | Tab 2 (Khung giờ) — SAI |
|---|---|---|
| Layout | 2 cột (trái: form, phải: "Cấu hình đang áp dụng") | 1 cột |
| Input cảnh báo | Input số phút (number input) | Slider % ← SAI |
| "Cấu hình đang áp dụng" | Có panel bên phải | Không có |

### Fix — Cấu trúc layout Tab 2 phải là 2 cột giống Tab 1

```tsx
{/* Tab 2 — Khung giờ — CẤU TRÚC LAYOUT PHẢI GIỐNG HỆT TAB 1 */}
<div className="flex gap-6">

  {/* ── Cột trái: Form ── */}
  <div className="flex-1 space-y-6 min-w-0">

    {/* Bước 1 — Chọn website */}
    <section className="space-y-3">
      <div className="flex items-center gap-2">
        <span className="w-6 h-6 rounded-full bg-violet-600 text-white text-xs font-bold 
                         flex items-center justify-center">1</span>
        <h3 className="font-bold text-tx-primary text-sm uppercase tracking-wide">
          Chọn website
        </h3>
      </div>
      <div className="grid grid-cols-2 gap-2">
        {timeWindowWebsites.map(website => {
          const isSelected = selectedWebsiteId === website.id;
          const range = formatTimeRange(website.allowedStartTime, website.allowedEndTime);
          return (
            <button
              key={website.id}
              onClick={() => setSelectedWebsiteId(website.id)}
              className={cn(
                'flex flex-col items-start gap-1 p-3 rounded-2xl border-2 text-left transition-all',
                isSelected
                  ? 'border-violet-500 bg-brand-subtle'
                  : 'border-border-base bg-bg-surface hover:border-brand-DEFAULT/50'
              )}
            >
              <span className={cn('text-sm font-bold', isSelected ? 'text-tx-primary' : 'text-tx-primary')}>
                {website.domain}
              </span>
              <span className={cn(
                'text-xs font-medium',
                range ? 'text-brand-DEFAULT' : 'text-tx-muted'
              )}>
                {range ?? 'Chưa có khung giờ'}
              </span>
              {hasExistingConfig(website.id) && (
                <span className="text-[10px] text-green-500 font-semibold">● Đã có config</span>
              )}
            </button>
          );
        })}
      </div>
    </section>

    {/* Bước 2 — Cảnh báo (chỉ hiện khi đã chọn website) */}
    {selectedWebsiteId && (
      <section className="space-y-4">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2">
            <span className="w-6 h-6 rounded-full bg-violet-600 text-white text-xs font-bold 
                             flex items-center justify-center">2</span>
            <h3 className="font-bold text-tx-primary text-sm uppercase tracking-wide">
              Cảnh báo khung giờ
            </h3>
            <span className="text-xs text-tx-muted">(tùy chọn)</span>
          </div>
          <button
            onClick={() => setShowWarning(p => !p)}
            className={cn(
              'text-xs font-bold px-3 py-1 rounded-lg transition-colors',
              showWarning
                ? 'bg-yellow-500/20 text-yellow-400'
                : 'bg-bg-subtle text-tx-secondary hover:text-tx-primary'
            )}
          >
            {showWarning ? 'Tắt cảnh báo' : 'Bật cảnh báo'}
          </button>
        </div>

        {showWarning && (
          <div className="bg-bg-elevated rounded-2xl p-4 space-y-4 border border-border-base">

            {/* Toggle mode: 2 nút loại trừ nhau */}
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
                ⏱ Trước N phút khi hết giờ
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
                🕐 Vào giờ cụ thể
              </button>
            </div>

            {/* ── Mode: minutes_before ── */}
            {warnMode === 'minutes_before' && (
              <div className="space-y-4">
                {/* Mốc 1 — BẮT BUỘC */}
                <div className="bg-bg-subtle rounded-2xl p-4 space-y-3">
                  <p className="text-xs font-bold text-tx-secondary uppercase tracking-wider">
                    🔔 Cảnh báo mốc 1
                  </p>
                  {/* Input số phút — KHÔNG dùng slider % */}
                  <div className="flex items-center gap-2">
                    <span className="text-xs text-tx-secondary whitespace-nowrap">Trước</span>
                    <input
                      type="number" min={1} max={240}
                      value={minutesBefore1}
                      onChange={e => setMinutesBefore1(Number(e.target.value))}
                      className="w-24 px-3 py-2 rounded-xl border border-border-base bg-bg-surface
                                 text-tx-primary text-sm text-center
                                 focus:outline-none focus:border-brand-DEFAULT"
                    />
                    <span className="text-xs text-tx-secondary whitespace-nowrap">phút khi hết khung giờ</span>
                  </div>
                  {/* Preview */}
                  {selectedWebsite && (
                    <p className="text-xs text-tx-secondary">
                      ≈ Cảnh báo lúc{' '}
                      <span className="font-bold text-brand-DEFAULT">
                        {calcWarnTime(selectedWebsite.allowedEndTime!, minutesBefore1)}
                      </span>
                    </p>
                  )}
                  {/* Nội dung thông báo */}
                  <div>
                    <label className="text-xs font-semibold text-tx-secondary uppercase tracking-wider block mb-1.5">
                      Nội dung thông báo
                    </label>
                    <textarea
                      value={message1} onChange={e => setMessage1(e.target.value)}
                      placeholder="VD: Sắp hết khung giờ rồi con ơi!"
                      maxLength={300} rows={2}
                      className="w-full px-3 py-2 rounded-xl border border-border-base bg-bg-surface
                                 text-tx-primary text-sm resize-none placeholder:text-tx-muted
                                 focus:outline-none focus:border-brand-DEFAULT"
                    />
                    <p className="text-right text-[10px] text-tx-muted mt-1">{message1.length}/300</p>
                  </div>
                </div>

                {/* Mốc 2 — tùy chọn */}
                {showMoc2 ? (
                  <div className="bg-bg-subtle rounded-2xl p-4 space-y-3">
                    <div className="flex items-center justify-between">
                      <p className="text-xs font-bold text-tx-secondary uppercase tracking-wider">
                        🔔 Cảnh báo mốc 2
                      </p>
                      <button onClick={() => setShowMoc2(false)}
                        className="text-xs text-red-400 hover:text-red-500">− Xóa</button>
                    </div>
                    <div className="flex items-center gap-2">
                      <span className="text-xs text-tx-secondary whitespace-nowrap">Trước</span>
                      <input
                        type="number" min={1} max={240}
                        value={minutesBefore2}
                        onChange={e => setMinutesBefore2(Number(e.target.value))}
                        className="w-24 px-3 py-2 rounded-xl border border-border-base bg-bg-surface
                                   text-tx-primary text-sm text-center
                                   focus:outline-none focus:border-brand-DEFAULT"
                      />
                      <span className="text-xs text-tx-secondary whitespace-nowrap">phút khi hết khung giờ</span>
                    </div>
                    <textarea
                      value={message2} onChange={e => setMessage2(e.target.value)}
                      placeholder="Nội dung cảnh báo mốc 2..."
                      maxLength={300} rows={2}
                      className="w-full px-3 py-2 rounded-xl border border-border-base bg-bg-surface
                                 text-tx-primary text-sm resize-none placeholder:text-tx-muted
                                 focus:outline-none focus:border-brand-DEFAULT"
                    />
                  </div>
                ) : (
                  <button onClick={() => setShowMoc2(true)}
                    className="text-xs font-semibold text-brand-DEFAULT hover:text-brand-hover flex items-center gap-1">
                    + Thêm mốc cảnh báo thứ 2
                  </button>
                )}
              </div>
            )}

            {/* ── Mode: at_time ── */}
            {warnMode === 'at_time' && (
              <div className="space-y-4">
                {/* Hint khung giờ */}
                {selectedWebsite && (
                  <div className="flex items-center gap-2 px-3 py-2 bg-bg-subtle rounded-xl">
                    <span className="text-xs text-tx-secondary">
                      💡 Chọn giờ trong khung{' '}
                      <span className="font-bold text-brand-DEFAULT">
                        {formatTimeRange(selectedWebsite.allowedStartTime, selectedWebsite.allowedEndTime) ?? '—'}
                      </span>
                    </span>
                  </div>
                )}

                {/* Mốc 1 */}
                <div className="bg-bg-subtle rounded-2xl p-4 space-y-3">
                  <p className="text-xs font-bold text-tx-secondary uppercase tracking-wider">
                    🔔 Cảnh báo lúc (mốc 1)
                  </p>
                  <input
                    type="time" value={warnAtTime1} onChange={e => setWarnAtTime1(e.target.value)}
                    className="w-full px-3 py-2 rounded-xl border border-border-base bg-bg-surface
                               text-tx-primary focus:outline-none focus:border-brand-DEFAULT"
                  />
                  <textarea
                    value={warnAtMsg1} onChange={e => setWarnAtMsg1(e.target.value)}
                    placeholder="VD: Con sắp hết khung giờ dùng mạng!"
                    maxLength={300} rows={2}
                    className="w-full px-3 py-2 rounded-xl border border-border-base bg-bg-surface
                               text-tx-primary text-sm resize-none placeholder:text-tx-muted
                               focus:outline-none focus:border-brand-DEFAULT"
                  />
                  <p className="text-right text-[10px] text-tx-muted">{warnAtMsg1.length}/300</p>
                </div>

                {/* Mốc 2 tùy chọn */}
                {showAtTime2 ? (
                  <div className="bg-bg-subtle rounded-2xl p-4 space-y-3">
                    <div className="flex items-center justify-between">
                      <p className="text-xs font-bold text-tx-secondary uppercase tracking-wider">
                        🔔 Cảnh báo lúc (mốc 2)
                      </p>
                      <button onClick={() => { setShowAtTime2(false); setWarnAtTime2(''); setWarnAtMsg2(''); }}
                        className="text-xs text-red-400 hover:text-red-500">− Xóa</button>
                    </div>
                    <input type="time" value={warnAtTime2} onChange={e => setWarnAtTime2(e.target.value)}
                      className="w-full px-3 py-2 rounded-xl border border-border-base bg-bg-surface
                                 text-tx-primary focus:outline-none focus:border-brand-DEFAULT"
                    />
                    <textarea value={warnAtMsg2} onChange={e => setWarnAtMsg2(e.target.value)}
                      placeholder="Nội dung mốc 2..." maxLength={300} rows={2}
                      className="w-full px-3 py-2 rounded-xl border border-border-base bg-bg-surface
                                 text-tx-primary text-sm resize-none placeholder:text-tx-muted
                                 focus:outline-none focus:border-brand-DEFAULT"
                    />
                  </div>
                ) : (
                  <button onClick={() => setShowAtTime2(true)}
                    className="text-xs font-semibold text-brand-DEFAULT hover:text-brand-hover flex items-center gap-1">
                    + Thêm mốc cảnh báo thứ 2
                  </button>
                )}
              </div>
            )}
          </div>
        )}
      </section>
    )}

    {/* Nút lưu */}
    <button
      onClick={handleSave}
      disabled={!canSave || isSaving}
      className={cn(
        'w-full h-12 rounded-2xl font-bold text-sm flex items-center justify-center gap-2 transition-all',
        'bg-violet-600 text-white hover:bg-violet-700',
        'dark:bg-violet-500 dark:hover:bg-violet-400',
        'disabled:opacity-40 disabled:cursor-not-allowed'
      )}
    >
      {isSaving ? 'Đang lưu...' : 'Lưu cấu hình'}
    </button>
  </div>

  {/* ── Cột phải: Cấu hình đang áp dụng (giống Tab 1) ── */}
  <div className="w-64 shrink-0">
    <div className="bg-bg-subtle rounded-2xl p-4 space-y-3">
      <div className="flex items-center gap-2 mb-3">
        <span className="text-green-500">✓</span>
        <h4 className="text-xs font-bold text-tx-primary uppercase tracking-wider">
          Khung giờ đang áp dụng
        </h4>
      </div>
      {existingTwConfigs.length === 0 ? (
        <div className="text-center py-8">
          <p className="text-tx-muted text-xs">Chưa có website nào được đặt khung giờ cảnh báo.</p>
        </div>
      ) : (
        existingTwConfigs.map(c => (
          <div key={c.id} className="bg-bg-surface rounded-xl p-3 border border-border-base space-y-1">
            <div className="flex items-center justify-between">
              <span className="text-sm font-bold text-tx-primary">{c.domain}</span>
              <button onClick={() => deleteTwConfig(c.id)}
                className="text-tx-muted hover:text-red-400 transition-colors p-1 rounded-lg hover:bg-red-50 dark:hover:bg-red-900/20">
                <Trash2 className="w-3.5 h-3.5" />
              </button>
            </div>
            <p className="text-[11px] text-tx-secondary">
              {c.warnMode === 'at_time'
                ? `🕐 Cảnh báo lúc ${c.warnAtTime1?.substring(0,5)}`
                : `⏱ Trước ${c.warnMinutesBefore1} phút`}
              {(c.warnMode === 'at_time' ? c.warnAtTime2 : c.warnMinutesBefore2) && ' · +mốc 2'}
            </p>
          </div>
        ))
      )}
    </div>
  </div>

</div>
```

---

## Helper function tính giờ cảnh báo ≈

```typescript
// Tính giờ cảnh báo khi biết giờ kết thúc và số phút trước
function calcWarnTime(endTime: string, minutesBefore: number): string {
  const [h, m] = endTime.split(':').map(Number);
  const totalMins = h * 60 + m - minutesBefore;
  const wh = Math.floor(((totalMins % 1440) + 1440) % 1440 / 60);
  const wm = ((totalMins % 1440) + 1440) % 1440 % 60;
  return `${String(wh).padStart(2,'0')}:${String(wm).padStart(2,'0')}`;
}
// VD: endTime="21:00", minutesBefore=10 → "20:50"
```

---

## Checklist

- [ ] **Debug lỗi 1**: Log `website.allowedStartTime` ra console để xác nhận tên field đúng
- [ ] **Fix lỗi 1**: Đảm bảo field name khớp với API response (`allowedStartTime` camelCase)
- [ ] **Fix lỗi 2a**: Đổi layout Tab 2 thành 2 cột (trái: form, phải: "Cấu hình đang áp dụng")
- [ ] **Fix lỗi 2b**: Phần cảnh báo dùng number input (N phút) KHÔNG dùng slider %
- [ ] **Fix lỗi 2c**: Thêm panel "Khung giờ đang áp dụng" bên phải
- [ ] Thêm helper `formatTimeRange` và `calcWarnTime`
- [ ] Tất cả class màu dùng CSS variable (`bg-bg-surface`, `text-tx-primary`, `border-border-base`)
- [ ] **KHÔNG thay đổi Tab 1** (Giới hạn phút) và logic khác
