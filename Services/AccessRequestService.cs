using FamilyGuardian.Api.Data;
using FamilyGuardian.Api.Helpers;
using FamilyGuardian.Api.Hubs;
using FamilyGuardian.Api.Models.DTOs;
using FamilyGuardian.Api.Models.Entities;
using FamilyGuardian.Api.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FamilyGuardian.Api.Services;

public class AccessRequestService : IAccessRequestService
{
    private readonly AppDbContext _context;
    private readonly IHubContext<NotificationHub> _hub;

    public AccessRequestService(AppDbContext context, IHubContext<NotificationHub> hub)
    {
        _context = context;
        _hub = hub;
    }

    public async Task<(bool Success, string Message)> SubmitRequestAsync(
        string googleId,
        string domain,
        string? fullUrl,
        string reason,
        int? requestedDurationMinutes,
        string? requestedStartTime,
        string? requestedEndTime)
    {
        domain = DomainNormalizer.Normalize(domain);
        reason = string.IsNullOrWhiteSpace(reason) ? "not_in_whitelist" : reason.Trim().ToLowerInvariant();

        reason = reason switch
        {
            "time_limit_exceeded" => "time_limit_exceeded",
            var r when r.Contains("time_limit")
                    || r.Contains("timelimit")
                    || r.Contains("exceeded") => "time_limit_exceeded",
            "internet_paused" => "internet_paused",
            var r when r.Contains("internet_paused")
                    || r.Contains("internetpaused")
                    || r.Contains("paused") => "internet_paused",
            "outside_time_window" => "outside_time_window",
            var r when r.Contains("outside_time")
                    || r.Contains("time_window")
                    || r.Contains("timewindow")
                    || r.Contains("outside_window") => "outside_time_window",
            _ => "not_in_whitelist"
        };

        var child = await _context.Users
            .FirstOrDefaultAsync(u => u.GoogleId == googleId && u.Role == UserRole.Child);
        if (child == null) return (false, "Không tìm thấy tài khoản con");

        if (reason == "not_in_whitelist")
        {
            var alreadyAllowed = await _context.AllowedWebsites
                .AnyAsync(w => w.ChildId == child.Id && w.Domain == domain && w.IsActive);
            if (alreadyAllowed) return (false, "Domain này đã có trong danh sách cho phép");
        }

        var guardianIds = await _context.GuardianChildRelationships
            .Where(r => r.ChildId == child.Id)
            .Select(r => r.GuardianId)
            .ToListAsync();
        if (!guardianIds.Any()) return (false, "Không tìm thấy guardian");

        var existingPending = await _context.AccessRequests
            .AnyAsync(r => r.ChildId == child.Id
                        && r.Domain == domain
                        && r.Status == "pending");
        if (existingPending) return (false, "Đã gửi yêu cầu cho trang này rồi, vui lòng chờ");

        foreach (var guardianId in guardianIds)
        {
            var request = new AccessRequest
            {
                ChildId = child.Id,
                GuardianId = guardianId,
                Domain = domain,
                FullUrl = fullUrl,
                Reason = reason,
                RequestedDurationMinutes = requestedDurationMinutes,
                RequestedStartTime = !string.IsNullOrWhiteSpace(requestedStartTime)
                    ? TimeOnly.Parse(requestedStartTime)
                    : null,
                RequestedEndTime = !string.IsNullOrWhiteSpace(requestedEndTime)
                    ? TimeOnly.Parse(requestedEndTime)
                    : null,
                Status = "pending",
                RequestedAt = DateTime.Now
            };
            _context.AccessRequests.Add(request);

            var title = reason switch
            {
                "internet_paused" => "Yêu cầu bật Internet",
                "time_limit_exceeded" => "Yêu cầu gia hạn thời gian",
                _ => "Yêu cầu truy cập"
            };

            var message = reason switch
            {
                "internet_paused" => $"{child.FullName} muốn bật lại Internet",
                "time_limit_exceeded" => requestedDurationMinutes.HasValue
                    ? $"{child.FullName} xin thêm {requestedDurationMinutes.Value} phút cho {domain}"
                    : $"{child.FullName} xin thêm thời gian cho {domain}",
                _ => $"{child.FullName} muốn truy cập {domain}"
            };

            var notification = new Notification
            {
                GuardianId = guardianId,
                ChildId = child.Id,
                Title = title,
                Message = message,
                Type = NotificationType.Info,
                CreatedAt = DateTime.Now
            };
            _context.Notifications.Add(notification);
        }

        await _context.SaveChangesAsync();

        foreach (var guardianId in guardianIds)
        {
            await _hub.Clients
                .Group($"guardian_{guardianId}")
                .SendAsync("AccessRequest", new
                {
                    childName = child.FullName,
                    childAvatarUrl = child.AvatarUrl,
                    domain,
                    reason,
                    requestedDurationMinutes,
                    requestedStartTime,
                    requestedEndTime,
                    message = reason == "internet_paused"
                        ? $"{child.FullName} muốn bật lại Internet"
                        : reason == "time_limit_exceeded"
                            ? $"{child.FullName} xin thêm thời gian cho {domain}"
                            : $"{child.FullName} muốn truy cập {domain}"
                });
        }

        return (true, "Đã gửi yêu cầu thành công");
    }

    public async Task<List<AccessRequestDto>> GetPendingRequestsAsync(int guardianId)
        => await GetRequestsAsync(guardianId, "pending");

    public async Task<List<AccessRequestDto>> GetRequestsAsync(int guardianId, string statusFilter = "pending")
    {
        var filter = string.IsNullOrWhiteSpace(statusFilter)
            ? "pending"
            : statusFilter.Trim().ToLowerInvariant();

        var query = _context.AccessRequests
            .Include(r => r.Child)
            .Where(r => r.GuardianId == guardianId);

        if (filter == "pending")
        {
            query = query.Where(r => r.Status == "pending");
        }
        else if (filter == "handled")
        {
            query = query.Where(r => r.Status != "pending");
        }

        var result = await query
            .OrderByDescending(r => r.RequestedAt)
            .Select(r => new AccessRequestDto
            {
                Id = r.Id,
                ChildId = r.ChildId,
                ChildName = r.Child.FullName,
                ChildAvatarUrl = r.Child.AvatarUrl,
                Domain = r.Domain,
                FullUrl = r.FullUrl,
                Reason = r.Reason,
                RequestedDurationMinutes = r.RequestedDurationMinutes,
                RequestedStartTime = r.RequestedStartTime.HasValue
                    ? r.RequestedStartTime.Value.ToString(@"HH\:mm")
                    : null,
                RequestedEndTime = r.RequestedEndTime.HasValue
                    ? r.RequestedEndTime.Value.ToString(@"HH\:mm")
                    : null,
                Status = r.Status,
                RequestedAt = r.RequestedAt,
                TempExpiresAt = r.TempExpiresAt
            })
            .ToListAsync();

        var timeLimitRequests = result.Where(r => r.Reason == "time_limit_exceeded").ToList();
        foreach (var dto in timeLimitRequests)
        {
            var website = await _context.AllowedWebsites
                .FirstOrDefaultAsync(w => w.ChildId == dto.ChildId
                                       && w.Domain == dto.Domain
                                       && w.IsActive);
            if (website == null) continue;

            if (website.TimeLimitMinutes.HasValue)
            {
                dto.WebsiteRestrictionType = "minutes";
                dto.WebsiteTimeLimitMinutes = website.TimeLimitMinutes;
            }
            else if (website.AllowedStartTime.HasValue)
            {
                dto.WebsiteRestrictionType = "time_window";
                dto.WebsiteAllowedStartTime = website.AllowedStartTime.Value.ToString(@"HH\:mm");
                dto.WebsiteAllowedEndTime = website.AllowedEndTime?.ToString(@"HH\:mm");
            }
        }

        return result;
    }

    public async Task<(bool Success, string Message)> RespondToRequestAsync(
        int requestId, int guardianId, RespondAccessRequestDto dto)
    {
        var request = await _context.AccessRequests
            .Include(r => r.Child)
            .FirstOrDefaultAsync(r => r.Id == requestId && r.GuardianId == guardianId);

        if (request == null) return (false, "Không tìm thấy yêu cầu");
        if (request.Status != "pending") return (false, "Yêu cầu này đã được xử lý rồi");

        var action = dto.Action.Trim().ToLowerInvariant();
        request.RespondedAt = DateTime.Now;

        if (action == "reject")
        {
            request.Status = "rejected";
            await _context.SaveChangesAsync();
            return (true, "Đã từ chối yêu cầu");
        }

        if (action == "approve_internet")
        {
            request.Status = "approved_permanent";

            var child = await _context.Users.FindAsync(request.ChildId);
            if (child != null)
            {
                child.InternetPaused = false;
            }

            await _context.SaveChangesAsync();

            await _hub.Clients
                .Group($"child_{request.ChildId}")
                .SendAsync("InternetResumed", new { childId = request.ChildId });

            return (true, "Đã bật lại Internet");
        }

        if (action == "extend_time")
        {
            var bonusMinutes = dto.DurationMinutes ?? request.RequestedDurationMinutes ?? 30;

            var website = await _context.AllowedWebsites
                .FirstOrDefaultAsync(w => w.ChildId == request.ChildId && w.Domain == request.Domain && w.IsActive);
            if (website == null) return (false, "Không tìm thấy website trong danh sách");

            var today = DateOnly.FromDateTime(DateTime.Now);
            var stat = await _context.DailyUsageStats
                .FirstOrDefaultAsync(s => s.ChildId == request.ChildId
                                       && s.AllowedWebsiteId == website.Id
                                       && s.UsageDate == today);
            if (stat != null)
            {
                stat.BonusSeconds += bonusMinutes * 60;
                stat.Warning1Sent = false;
                stat.Warning2Sent = false;
                stat.LastUpdated = DateTime.Now;
            }

            request.Status = "approved_temp";
            request.TempExpiresAt = DateTime.Now.AddMinutes(bonusMinutes);

            await _context.SaveChangesAsync();

            await _hub.Clients
                .Group($"child_{request.ChildId}")
                .SendAsync("AccessApproved", new
                {
                    childId = request.ChildId,
                    domain = request.Domain
                });

            return (true, $"Đã gia hạn thêm {bonusMinutes} phút");
        }

        if (action == "extend_window")
        {
            request.Status = "approved_temp";

            if (string.IsNullOrEmpty(dto.NewEndTime))
                return (false, "Giờ kết thúc mới không được để trống");

            var website = await _context.AllowedWebsites
                .FirstOrDefaultAsync(w => w.ChildId == request.ChildId
                                       && w.Domain == request.Domain
                                       && w.IsActive);
            if (website == null) return (false, "Không tìm thấy website trong danh sách");

            if (TimeSpan.TryParse(dto.NewEndTime, out var newEnd))
                website.AllowedEndTime = new TimeOnly(newEnd.Hours, newEnd.Minutes);

            if (!string.IsNullOrEmpty(dto.NewStartTime) && TimeSpan.TryParse(dto.NewStartTime, out var newStart))
                website.AllowedStartTime = new TimeOnly(newStart.Hours, newStart.Minutes);

            var today = DateOnly.FromDateTime(DateTime.Now);
            var stat = await _context.DailyUsageStats
                .FirstOrDefaultAsync(s => s.ChildId == request.ChildId
                                       && s.AllowedWebsiteId == website.Id
                                       && s.UsageDate == today);
            if (stat != null)
            {
                stat.TwWarning1Sent = false;
                stat.TwWarning2Sent = false;
            }

            await _context.SaveChangesAsync();

            await _hub.Clients
                .Group($"child_{request.ChildId}")
                .SendAsync("AccessApproved", new
                {
                    childId = request.ChildId,
                    domain = request.Domain
                });

            request.RespondedAt = DateTime.Now;
            request.Status = "approved_temp";
            await _context.SaveChangesAsync();

            return (true, "Đã cập nhật khung giờ cho phép");
        }

        if (action == "approve_temp" || action == "approve_permanent")
        {
            var existing = await _context.AllowedWebsites
                .FirstOrDefaultAsync(w => w.ChildId == request.ChildId && w.Domain == request.Domain);

            if (action == "approve_temp")
            {
                var expiresAt = DateTime.Now.AddMinutes(dto.DurationMinutes ?? request.RequestedDurationMinutes ?? 30);
                request.Status = "approved_temp";
                request.TempExpiresAt = expiresAt;

                if (existing != null)
                {
                    existing.IsActive = true;
                    existing.TempExpiresAt = expiresAt;
                }
                else
                {
                    _context.AllowedWebsites.Add(new AllowedWebsite
                    {
                        ChildId = request.ChildId,
                        Domain = request.Domain,
                        DisplayName = request.Domain,
                        FaviconUrl = $"https://www.google.com/s2/favicons?domain={request.Domain}&sz=64",
                        IsActive = true,
                        AddedBy = guardianId,
                        TempExpiresAt = expiresAt,
                        CreatedAt = DateTime.Now
                    });
                }
            }
            else
            {
                request.Status = "approved_permanent";
                request.TempExpiresAt = null;

                if (existing != null)
                {
                    existing.IsActive = true;
                    existing.TempExpiresAt = null;
                    existing.TimeLimitMinutes = null;
                    existing.AllowedStartTime = null;
                    existing.AllowedEndTime = null;
                }
                else
                {
                    existing = new AllowedWebsite
                    {
                        ChildId = request.ChildId,
                        Domain = request.Domain,
                        DisplayName = request.Domain,
                        FaviconUrl = $"https://www.google.com/s2/favicons?domain={request.Domain}&sz=64",
                        IsActive = true,
                        AddedBy = guardianId,
                        CreatedAt = DateTime.Now
                    };
                    _context.AllowedWebsites.Add(existing);
                }

                if (dto.DurationMinutes.HasValue && string.IsNullOrWhiteSpace(dto.StartTime) && string.IsNullOrWhiteSpace(dto.EndTime))
                {
                    existing.TimeLimitMinutes = dto.DurationMinutes.Value;
                }
                else if (!string.IsNullOrWhiteSpace(dto.StartTime) && !string.IsNullOrWhiteSpace(dto.EndTime))
                {
                    existing.TimeLimitMinutes = null;
                    existing.AllowedStartTime = TimeOnly.Parse(dto.StartTime);
                    existing.AllowedEndTime = TimeOnly.Parse(dto.EndTime);
                }
            }

            await _context.SaveChangesAsync();

            await _hub.Clients
                .Group($"child_{request.ChildId}")
                .SendAsync("AccessApproved", new
                {
                    childId = request.ChildId,
                    domain = request.Domain
                });

            return (true, "Đã xử lý yêu cầu thành công");
        }

        return (false, "Action không hợp lệ");
    }
}
