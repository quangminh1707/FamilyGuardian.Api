using FamilyGuardian.Api.Data;
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
        string googleId, string domain, string? fullUrl)
    {
        // Tìm child
        var child = await _context.Users
            .FirstOrDefaultAsync(u => u.GoogleId == googleId && u.Role == UserRole.Child);
        if (child == null) return (false, "Không tìm thấy tài khoản con");

        // Tìm tất cả guardian của child
        var guardianIds = await _context.GuardianChildRelationships
            .Where(r => r.ChildId == child.Id)
            .Select(r => r.GuardianId)
            .ToListAsync();
        if (!guardianIds.Any()) return (false, "Không tìm thấy guardian");

        // Kiểm tra đã có pending request chưa (tránh spam)
        var existingPending = await _context.AccessRequests
            .AnyAsync(r => r.ChildId == child.Id
                        && r.Domain == domain
                        && r.Status == "pending");
        if (existingPending) return (false, "Đã gửi yêu cầu cho trang này rồi, vui lòng chờ");

        // Tạo request cho từng guardian
        foreach (var guardianId in guardianIds)
        {
            var request = new AccessRequest
            {
                ChildId = child.Id,
                GuardianId = guardianId,
                Domain = domain,
                FullUrl = fullUrl,
                Status = "pending",
                RequestedAt = DateTime.Now
            };
            _context.AccessRequests.Add(request);

            // Tạo notification trong DB
            var notification = new Notification
            {
                GuardianId = guardianId,
                ChildId = child.Id,
                Title = "Yêu cầu truy cập",
                Message = $"{child.FullName} muốn truy cập {domain}",
                Type = NotificationType.Info,
                CreatedAt = DateTime.Now
            };
            _context.Notifications.Add(notification);
        }

        await _context.SaveChangesAsync();

        // Gửi SignalR tới tất cả guardian
        foreach (var guardianId in guardianIds)
        {
            await _hub.Clients
                .Group($"guardian_{guardianId}")
                .SendAsync("AccessRequest", new
                {
                    childName = child.FullName,
                    childAvatarUrl = child.AvatarUrl,
                    domain = domain,
                    message = $"{child.FullName} muốn truy cập {domain}"
                });
        }

        return (true, "Đã gửi yêu cầu thành công");
    }

    public async Task<List<AccessRequestDto>> GetPendingRequestsAsync(int guardianId)
    {
        return await _context.AccessRequests
            .Include(r => r.Child)
            .Where(r => r.GuardianId == guardianId && r.Status == "pending")
            .OrderByDescending(r => r.RequestedAt)
            .Select(r => new AccessRequestDto
            {
                Id = r.Id,
                ChildId = r.ChildId,
                ChildName = r.Child.FullName,
                ChildAvatarUrl = r.Child.AvatarUrl,
                Domain = r.Domain,
                FullUrl = r.FullUrl,
                Status = r.Status,
                RequestedAt = r.RequestedAt
            })
            .ToListAsync();
    }

    public async Task<(bool Success, string Message)> RespondToRequestAsync(
        int requestId, int guardianId, RespondAccessRequestDto dto)
    {
        var request = await _context.AccessRequests
            .Include(r => r.Child)
            .FirstOrDefaultAsync(r => r.Id == requestId && r.GuardianId == guardianId);

        if (request == null) return (false, "Không tìm thấy yêu cầu");
        if (request.Status != "pending") return (false, "Yêu cầu này đã được xử lý rồi");

        request.RespondedAt = DateTime.Now;

        if (dto.Action == "reject")
        {
            request.Status = "rejected";
            await _context.SaveChangesAsync();
            return (true, "Đã từ chối yêu cầu");
        }

        // Kiểm tra domain đã có trong whitelist chưa
        var existing = await _context.AllowedWebsites
            .FirstOrDefaultAsync(w => w.ChildId == request.ChildId && w.Domain == request.Domain);

        if (dto.Action == "approve_temp")
        {
            request.Status = "approved_temp";
            var expiresAt = DateTime.Now.AddMinutes(dto.DurationMinutes);
            request.TempExpiresAt = expiresAt;

            if (existing != null)
            {
                // Kích hoạt lại và set thời hạn
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
        else if (dto.Action == "approve_permanent")
        {
            request.Status = "approved_permanent";

            if (existing != null)
            {
                existing.IsActive = true;
                existing.TempExpiresAt = null; // xóa temp nếu có
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
                    CreatedAt = DateTime.Now
                });
            }
        }
        else
        {
            return (false, "Action không hợp lệ");
        }

        await _context.SaveChangesAsync();
        return (true, "Đã xử lý yêu cầu thành công");
    }
}
